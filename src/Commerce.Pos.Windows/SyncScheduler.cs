using System.Windows.Threading;

namespace Commerce.Pos.Windows;

/// <summary>
/// Task 4.6 (commerce-sync-ownership design.md "Retry: where and how"):
/// answer (c) — "sync the moment conditions allow" — implemented as a fixed
/// 60s <see cref="DispatcherTimer"/> sweep plus a run at startup, both
/// calling the SAME <see cref="SyncRunner.RunAsync"/> the manual button and
/// the post-sale nudge call. No exponential backoff, no dead letter (answers
/// (a)/(d)): a periodic sweep is connectivity detection enough, since a push
/// against a down link fails in milliseconds and needs no network-change
/// subscription. An in-process WPF timer, not a hosted background service
/// (explicit non-goal) — disposed with the window.
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    /// <summary>
    /// A starting value, not a measured one (design.md Open Questions):
    /// a single constant with no dependent logic, so tuning it later is a
    /// one-line change.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly DispatcherTimer _timer;
    private readonly Func<SyncTrigger, Task> _runSync;

    public SyncScheduler(Func<SyncTrigger, Task> runSync)
        : this(runSync, Interval)
    {
    }

    /// <summary>Test-only seam for a shorter interval; production always uses <see cref="Interval"/>.</summary>
    internal SyncScheduler(Func<SyncTrigger, Task> runSync, TimeSpan interval)
    {
        _runSync = runSync;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += async (_, _) => await SafeRunAsync(SyncTrigger.Timer);
    }

    /// <summary>
    /// Starts the periodic sweep and fires the startup run. Never awaited by
    /// a caller that cannot afford to block (design.md Data Flow: "the sale
    /// path never awaits any of this") — callers fire-and-forget this.
    /// </summary>
    public async Task StartAsync()
    {
        _timer.Start();
        await SafeRunAsync(SyncTrigger.Startup);
    }

    /// <summary>
    /// Every exception is swallowed and recorded durably by
    /// <see cref="SyncRunner"/>/<c>BranchSyncStore.RecordAttemptFailure</c> —
    /// never rethrown here, so a failed automatic sweep never crashes the POS
    /// process and never surfaces to the operator (answer (d)).
    /// </summary>
    private async Task SafeRunAsync(SyncTrigger trigger)
    {
        try
        {
            await _runSync(trigger);
        }
        catch
        {
            // Swallowed by design — see the doc comment above.
        }
    }

    public void Dispose() => _timer.Stop();
}
