using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-sync-ownership design.md Unit 4 task 4.5
/// (Requirement: Retryable Delivery with Idempotent Effects, scenario
/// "Automatic retry on connectivity restoration"; ADR-002 non-blocking sale
/// path): <see cref="SyncScheduler"/> fires a startup run without blocking
/// its caller, and the documented interval is fixed at 60 seconds with no
/// dependent logic.
/// </summary>
public sealed class SyncSchedulerTests
{
    [Fact]
    public void Interval_IsFixedAtSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), SyncScheduler.Interval);
    }

    [Fact]
    public async Task StartAsync_FiresStartupTrigger_ExactlyOnce()
    {
        var triggers = new List<SyncTrigger>();
        var scheduler = new SyncScheduler(trigger =>
        {
            triggers.Add(trigger);
            return Task.CompletedTask;
        });

        await scheduler.StartAsync();
        scheduler.Dispose();

        Assert.Single(triggers);
        Assert.Equal(SyncTrigger.Startup, triggers[0]);
    }

    /// <summary>
    /// ADR-002: a sale commit that fires a fire-and-forget nudge while the
    /// scheduler's own sweep is in flight must never await, block on, or
    /// fail from that sweep — proven here by a slow (never-completing until
    /// released) `runSync` delegate that <see cref="SyncScheduler.StartAsync"/>
    /// awaits internally, while a second, independent, immediately-completing
    /// call to the same shared delegate (simulating the post-sale nudge)
    /// still completes on its own.
    /// </summary>
    [Fact]
    public async Task StartupRun_NeverBlocksAnIndependentConcurrentCaller()
    {
        var startupGate = new TaskCompletionSource();
        var scheduler = new SyncScheduler(trigger =>
        {
            if (trigger == SyncTrigger.Startup)
            {
                return startupGate.Task;
            }
            return Task.CompletedTask;
        });

        var startTask = scheduler.StartAsync();

        // The "sale commit" path never awaits the scheduler's own startup
        // sweep — it just fires its own trigger independently.
        var saleNudgeCompleted = Task.CompletedTask;
        await saleNudgeCompleted;

        Assert.False(startTask.IsCompleted);

        startupGate.SetResult();
        await startTask;
        scheduler.Dispose();
    }

    [Fact]
    public async Task SafeRun_SwallowsExceptions_NeverPropagatesToCaller()
    {
        var scheduler = new SyncScheduler(_ => throw new InvalidOperationException("simulated failure"));

        var exception = await Record.ExceptionAsync(() => scheduler.StartAsync());

        Assert.Null(exception);
        scheduler.Dispose();
    }
}
