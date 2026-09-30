using System.Text.RegularExpressions;

namespace Commerce.Integration;

/// <summary>
/// Structural guards for the cash session UI (WPF cannot be instantiated in
/// this test host): "Cerrar Caja" is enabled in the nav bar, the open and close
/// prompts exist and are themed only through DynamicResource, the sale screen is
/// locked while no session is open, and sale commits handle the refusal without
/// ever reading the device token.
/// </summary>
public sealed class PosCashSessionMarkupTests
{
    private static string PosDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Commerce.sln")))
            {
                return Path.Combine(directory.FullName, "src", "Commerce.Pos.Windows");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([PosDirectory(), .. path]));

    [Fact]
    public void NavBar_HasAnEnabledCloseCashButton_AndShowsTheSessionState()
    {
        var xaml = Read("Controls", "PosNavBar.xaml");
        var code = Read("Controls", "PosNavBar.xaml.cs");

        Assert.Contains("x:Name=\"CloseCashButton\"", xaml);
        Assert.Contains("Cerrar Caja", xaml);
        Assert.Contains("x:Name=\"CashSessionStatusText\"", xaml);
        Assert.Contains("CloseCashRequested", code);
        // The old disabled placeholder is gone: no disabled entry is named "Cerrar Caja".
        Assert.DoesNotMatch(new Regex(@"IsEnabled=""False""[^>]*AutomationProperties.Name=""Cerrar Caja"""), xaml);
    }

    [Theory]
    [InlineData("OpenCashWindow", new[] { "OpeningFloatTextBox", "OperatorText", "ConfirmButton", "Abrir caja" })]
    [InlineData("CloseCashWindow", new[] { "ExpectedCashText", "CardTotalText", "QrTotalText", "SaleCountText", "CountedCashTextBox", "DifferenceText", "ConfirmButton" })]
    public void CashPrompts_HaveTheirNamedControls_AndAreThemedThroughDynamicResourceOnly(string window, string[] required)
    {
        var xaml = Read(window + ".xaml");

        foreach (var name in required)
        {
            Assert.Contains(name, xaml);
        }

        Assert.Contains("{DynamicResource ShellBrush}", xaml);
        Assert.Empty(Regex.Matches(xaml, @"\{StaticResource\s+\w*Brush\w*\}"));
        Assert.True(File.Exists(Path.Combine(PosDirectory(), window + ".xaml.cs")));
    }

    [Fact]
    public void MainWindow_LocksTheSaleScreenWithoutASession_AndWiresOpenAndClose()
    {
        var xaml = Read("MainWindow.xaml");
        var code = Read("MainWindow.xaml.cs");

        Assert.Contains("x:Name=\"SaleScreen\"", xaml);
        Assert.Contains("x:Name=\"CashClosedOverlay\"", xaml);
        Assert.Contains("x:Name=\"OpenCashButton\"", xaml);
        Assert.Contains("CloseCashRequested=", xaml);
        Assert.Contains("new OpenCashWindow", code);
        Assert.Contains("new CloseCashWindow", code);
        Assert.Contains("SaleScreen.IsEnabled", code);
    }

    [Fact]
    public void BothSaleCommits_HandleTheNoOpenSessionRefusal_AndNeverReadTheDeviceToken()
    {
        var code = Read("MainWindow.xaml.cs");

        var manual = code[code.IndexOf("private void CommitSaleButton_Click", StringComparison.Ordinal)..];
        manual = manual[..manual.IndexOf("private void RefreshCustomerPicker", StringComparison.Ordinal)];
        var scanned = code[code.IndexOf("private void CommitScannedSaleButton_Click", StringComparison.Ordinal)..];
        scanned = scanned[..scanned.IndexOf("Task 7.7", StringComparison.Ordinal)];

        foreach (var commit in new[] { manual, scanned })
        {
            Assert.Contains("Refusal", commit);
            var executable = string.Join(Environment.NewLine, commit.Split((char)10).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotContain("DeviceToken", executable);
        }
    }
}
