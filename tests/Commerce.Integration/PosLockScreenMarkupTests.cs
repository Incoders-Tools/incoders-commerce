using System.Text.RegularExpressions;

namespace Commerce.Integration;

/// <summary>
/// Structural checks for the lock screen: with no operator the main window shows
/// only a full-window sign-in layer (the sale, the nav and the sections are
/// collapsed, not merely covered), the old modal sign-in windows are gone, and the
/// lock view is themed by palette keys and busy-guarded.
/// </summary>
public sealed class PosLockScreenMarkupTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Src(params string[] path) =>
        File.ReadAllText(Path.Combine([Root(), "src", "Commerce.Pos.Windows", .. path]));

    [Fact]
    public void MainWindow_HasALockLayerAboveTheNavAndTheContent()
    {
        var xaml = Src("MainWindow.xaml");
        var shell = xaml.IndexOf("x:Name=\"ShellContent\"", StringComparison.Ordinal);
        var lockHost = xaml.IndexOf("x:Name=\"LockHost\"", StringComparison.Ordinal);

        Assert.True(shell >= 0 && lockHost > shell, "LockHost must come after (above) ShellContent");
        foreach (var name in new[] { "NavBar", "SaleScreen", "SectionHost", "CashClosedOverlay" })
        {
            var at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(at > shell && at < lockHost, $"{name} must live inside ShellContent");
        }

        var lockTag = Regex.Match(xaml, @"<ContentControl[^>]*x:Name=""LockHost""[^>]*/>");
        Assert.True(lockTag.Success);
        Assert.Contains("Grid.RowSpan=\"2\"", lockTag.Value);
        Assert.Contains("Grid.Row=\"0\"", lockTag.Value);
    }

    [Fact]
    public void MainWindow_CollapsesTheWholeShell_WhenThereIsNoOperator_AndNeverClearsTheSale()
    {
        var code = Src("MainWindow.xaml.cs");
        var apply = Regex.Match(code, @"void ApplyLockState\(\)[\s\S]*?\n    }");

        Assert.True(apply.Success);
        Assert.Contains("_currentOperator.Value is null", apply.Value);
        Assert.Contains("ShellContent.Visibility", apply.Value);
        Assert.Contains("LockHost.Visibility", apply.Value);
        Assert.DoesNotContain("_cart.Clear", apply.Value);
        Assert.Contains("ApplyLockState();", Regex.Match(code, @"void RefreshIdentityText\(\)[\s\S]*?\n    }").Value);
    }

    [Fact]
    public void MainWindow_PromptsForTheCashSession_OnlyOnceAnOperatorIsSignedIn()
    {
        var code = Src("MainWindow.xaml.cs");

        Assert.DoesNotContain("SignInOperatorFromPrompt", code);
        Assert.DoesNotContain("PromptOperatorSignIn", code);
        Assert.Contains("new LockScreenView(", code);
        Assert.Contains("_lockScreen.SignedIn", code);
        var prompt = Regex.Match(code, @"void PromptOpenCash\(\)[\s\S]*?\n    }");
        Assert.True(prompt.Success);
        Assert.Contains("_currentOperator.Value is null", prompt.Value);
    }

    [Fact]
    public void Startup_NoLongerShowsAModalSignIn()
    {
        var code = Src("App.xaml.cs");

        Assert.DoesNotContain("OperatorSignInFlow", code);
        Assert.DoesNotContain("OperatorLoginMode", code);
    }

    [Fact]
    public void TheModalSignInWindows_AreGone_AndNothingReferencesThem()
    {
        foreach (var file in new[]
                 {
                     "OperatorLoginWindow.xaml", "OperatorLoginWindow.xaml.cs",
                     "ProvisionOperatorWindow.xaml", "ProvisionOperatorWindow.xaml.cs", "OperatorSignInFlow.cs",
                 })
        {
            Assert.False(File.Exists(Path.Combine(Root(), "src", "Commerce.Pos.Windows", file)), $"{file} should be deleted");
        }

        var dir = Path.Combine(Root(), "src", "Commerce.Pos.Windows");
        var offenders = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("OperatorLoginWindow") || text.Contains("ProvisionOperatorWindow") || text.Contains("OperatorSignInFlow");
            })
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void LockScreenView_HasTheTilesThePinEntryTheCredentialsAndTheCreatePinParts()
    {
        var xaml = Src("LockScreenView.xaml");

        foreach (var name in new[]
                 {
                     "TilesPanel", "TilesItemsControl", "PinPanel", "PinBox", "CredentialsPanel", "EmailTextBox", "PasswordBox",
                     "ResetPinCheckBox", "CreatePinPanel", "NewPinBox", "ConfirmPinBox", "StatusText", "CredentialsLinkButton",
                     "BackButton", "BranchText", "FormPanel", "BusyPanel", "BusyProgressBar", "BusyText",
                 })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml);
        }

        Assert.Contains("Ingresar con usuario y contraseña", xaml);
        Assert.Contains("<ScrollViewer", xaml);
        Assert.Empty(Regex.Matches(xaml, @"\{StaticResource\s+\w*Brush\w*\}"));
    }

    [Fact]
    public void LockScreenView_ReadsItsStateFromTheModel_AndNeverStoresThePasswordOrPin()
    {
        var code = Src("LockScreenView.xaml.cs");

        Assert.Contains("LockScreenModel", code);
        Assert.Contains("SubmitPin(", code);
        Assert.Contains("SubmitCredentialsAsync(", code);
        Assert.Contains("SubmitNewPin(", code);
        Assert.Matches(@"finally\s*\{[^}]*PasswordBox\.Clear\(\)", code);
        Assert.DoesNotContain("MessageBox", code);
    }

    [Fact]
    public void LockScreenView_ShowsTheTerminalAndBranch()
    {
        Assert.Contains("BranchText", Src("LockScreenView.xaml.cs"));
    }

    [Fact]
    public void SpecStatesThatTheLockScreenBlocksTheSaleUi_WhileTheDomainFallbackRemains()
    {
        var spec = File.ReadAllText(Path.Combine(Root(), "openspec", "specs", "pos-operator-session", "spec.md"));

        Assert.Contains("Lock Screen Gates The Sale UI", spec);
        Assert.DoesNotContain("Operator Identification Never Blocks a Sale", spec);
        Assert.DoesNotContain("Operator Sign-Out Keeps the Cash Session", spec);
        Assert.Contains("Sign-Out Keeps The Cash Session And The Cart", spec);
    }
}
