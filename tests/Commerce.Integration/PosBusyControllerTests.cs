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
}
