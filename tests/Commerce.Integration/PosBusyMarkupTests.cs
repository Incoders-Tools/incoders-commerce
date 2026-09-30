namespace Commerce.Integration;

/// <summary>
/// Structural check that the request-issuing windows lock their inputs and show
/// an indeterminate progress bar themed with the shared palette keys.
/// </summary>
public sealed class PosBusyMarkupTests
{
    private static string Src(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Commerce.Pos.Windows", file));
    }

    [Theory]
    [InlineData("PairingWindow")]
    [InlineData("ProvisionOperatorWindow")]
    [InlineData("UsersWindow")]
    public void Window_HasProgressBarBusyTextAndAGuardedInputHost(string window)
    {
        var xaml = Src(window + ".xaml");
        var code = Src(window + ".xaml.cs");

        Assert.Contains("x:Name=\"BusyProgressBar\"", xaml);
        Assert.Contains("IsIndeterminate=\"True\"", xaml);
        Assert.Contains("x:Name=\"BusyText\"", xaml);
        Assert.Contains("x:Name=\"BusyPanel\"", xaml);
        Assert.Contains("x:Name=\"FormPanel\"", xaml);
        Assert.Contains("BusyController", code);
        Assert.Contains("FormPanel.IsEnabled", code);
        Assert.Contains("Closing", code);
    }

    [Theory]
    [InlineData("PairingWindow")]
    [InlineData("ProvisionOperatorWindow")]
    public void ThemedWindows_ColorTheProgressBarFromPaletteKeys(string window)
    {
        var xaml = Src(window + ".xaml");

        Assert.Contains("{DynamicResource PrimaryBrush}", xaml);
        Assert.Contains("{DynamicResource BorderBrushMuted}", xaml);
    }

    [Theory]
    [InlineData("PairingWindow")]
    [InlineData("ProvisionOperatorWindow")]
    [InlineData("UsersWindow")]
    public void Window_RoutesItsRequestsThroughTheBusyController_WithoutEnglishLiterals(string window)
    {
        var code = Src(window + ".xaml.cs");

        Assert.Contains("_busy.RunAsync", code);
        Assert.DoesNotContain("\"Invalid email or password.\"", code);
        Assert.DoesNotContain("\"Sign in failed.\"", code);
        Assert.DoesNotContain("\"Select a branch first.\"", code);
    }

    [Fact]
    public void App_InstallsTheGlobalHandlersAndTheFileLogger()
    {
        var code = Src("App.xaml.cs");

        Assert.Contains("DispatcherUnhandledException", code);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException", code);
        Assert.Contains("TaskScheduler.UnobservedTaskException", code);
        Assert.Contains("PosLog.Configure", code);
        Assert.Contains("PosMessages.Unexpected", code);
    }
}
