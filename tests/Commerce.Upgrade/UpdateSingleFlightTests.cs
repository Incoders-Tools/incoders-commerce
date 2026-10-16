using Commerce.Updater;

namespace Commerce.Upgrade;

public sealed class UpdateSingleFlightTests
{
    [Fact]
    public async Task ACallDuringAnInFlightRun_AwaitsTheSameRun_InsteadOfBeingDropped()
    {
        var flight = new SingleFlight();
        var gate = new TaskCompletionSource();
        var runs = 0;
        var finished = false;

        var first = flight.RunAsync(async () =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
            finished = true;
        });
        var second = flight.RunAsync(() =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });

        Assert.False(second.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, runs);
        Assert.True(finished);
    }

    [Fact]
    public async Task AfterTheRunCompletes_ANewCallStartsANewRun()
    {
        var flight = new SingleFlight();
        var runs = 0;

        await flight.RunAsync(() => { runs++; return Task.CompletedTask; });
        await flight.RunAsync(() => { runs++; return Task.CompletedTask; });

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task AFaultedRun_DoesNotBlockTheNextOne()
    {
        var flight = new SingleFlight();

        await Assert.ThrowsAsync<InvalidOperationException>(() => flight.RunAsync(() => throw new InvalidOperationException("x")));
        await flight.RunAsync(() => Task.CompletedTask);
    }
}
