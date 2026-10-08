using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// While a request is in flight the window is busy: inputs are locked, a
/// second action is ignored, and whatever happens the busy state is restored.
/// </summary>
[Collection("PosLog")]
public sealed class PosBusyControllerTests
{
    private readonly List<string> _events = [];

    private BusyController Controller() =>
        new((busy, text) => _events.Add(busy ? $"busy:{text}" : "idle"), "TestWindow", message => _events.Add($"error:{message}"));

    [Fact]
    public async Task RunAsync_RendersBusyThenIdle_AroundTheWork()
    {
        var controller = Controller();

        await controller.RunAsync("Verificando…", () => { _events.Add("work"); return Task.CompletedTask; });

        Assert.Equal(["busy:Verificando…", "work", "idle"], _events);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task RunAsync_WhileBusy_IgnoresASecondAction()
    {
        var controller = Controller();
        var release = new TaskCompletionSource();
        var ran = 0;

        var first = controller.RunAsync("a", async () => { ran++; await release.Task; });
        Assert.True(controller.IsBusy);
        await controller.RunAsync("b", () => { ran++; return Task.CompletedTask; });
        release.SetResult();
        await first;

        Assert.Equal(1, ran);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task RunAsync_WhenTheWorkThrows_ShowsTheFriendlyMessage_AndRestoresTheUi()
    {
        var controller = Controller();

        await controller.RunAsync("a", () => throw new InvalidOperationException("kaput"));

        Assert.Equal(["busy:a", $"error:{PosMessages.Unexpected}", "idle"], _events);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task RunAsync_RaisesIdle_AfterTheWorkEnds_WithTheControllerNoLongerBusy()
    {
        var controller = Controller();
        var busyAtIdle = true;
        controller.Idle += () => { busyAtIdle = controller.IsBusy; _events.Add("idle-event"); };

        await controller.RunAsync("a", () => Task.CompletedTask);

        Assert.False(busyAtIdle);
        Assert.Equal(["busy:a", "idle", "idle-event"], _events);
    }

    [Fact]
    public async Task RunAsync_RaisesIdle_EvenWhenTheWorkThrows_AndNotForAnIgnoredSecondAction()
    {
        var controller = Controller();
        var idles = 0;
        controller.Idle += () => idles++;
        var release = new TaskCompletionSource();

        var first = controller.RunAsync("a", async () => { await release.Task; throw new InvalidOperationException("kaput"); });
        await controller.RunAsync("b", () => Task.CompletedTask);
        Assert.Equal(0, idles);
        release.SetResult();
        await first;

        Assert.Equal(1, idles);
    }

    [Fact]
    public async Task RunAsync_WhenTheWorkThrows_TheDetailIsLogged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pos-busy-" + Guid.NewGuid().ToString("N"));
        PosLog.Configure(new PosFileLogger(dir));
        try
        {
            await Controller().RunAsync("a", () => throw new InvalidOperationException("kaput-detail"));

            var text = string.Concat(Directory.GetFiles(dir, "pos-*.log").Select(File.ReadAllText));
            Assert.Contains("TestWindow", text);
            Assert.Contains("kaput-detail", text);
        }
        finally
        {
            PosLog.Configure(null);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Cancel_EndsTheWorkQuietly_AndStillRaisesIdle()
    {
        var controller = Controller();
        var idle = 0;
        controller.Idle += () => idle++;

        var run = controller.RunAsync("a", async () => await Task.Delay(Timeout.Infinite, controller.Token));
        Assert.True(controller.IsBusy);
        controller.Cancel();
        await run;

        Assert.False(controller.IsBusy);
        Assert.Equal(1, idle);
        Assert.DoesNotContain(_events, e => e.StartsWith("error:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Idle_IsRaised_WhenTheWorkThrows_AndWhenShowingTheErrorThrowsToo()
    {
        var idle = 0;
        var controller = new BusyController(
            (busy, _) => { if (!busy) throw new InvalidOperationException("render"); },
            "TestWindow",
            _ => throw new InvalidOperationException("show"));
        controller.Idle += () => idle++;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.RunAsync("a", () => throw new InvalidOperationException("kaput")));

        Assert.Equal(1, idle);
        Assert.False(controller.IsBusy);
    }
}
