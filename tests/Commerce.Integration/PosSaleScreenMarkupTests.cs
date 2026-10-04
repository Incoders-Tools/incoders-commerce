using System.Text.RegularExpressions;

namespace Commerce.Integration;

/// <summary>
/// operator-ux-adjustments T3, structural guards (WPF cannot be instantiated in this test host): the search box takes
/// the focus with its caret aligned with the placeholder, a weighted product goes through ONE kilos entry point, and
/// the sale's actions use the shared spacing tokens of the theme.
/// </summary>
public sealed class PosSaleScreenMarkupTests
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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([PosDirectory(), .. parts]));

    private static string Element(string xaml, string name)
    {
        var match = Regex.Match(xaml, $@"<\w+[^>]*x:Name=""{name}""[^>]*>", RegexOptions.Singleline);
        Assert.True(match.Success, name + " not found");
        return match.Value;
    }

    private static string Attribute(string element, string attribute)
    {
        var match = Regex.Match(element, $@"\s{attribute}=""([^""]*)""");
        Assert.True(match.Success, attribute + " not found in " + element);
        return match.Groups[1].Value;
    }

    [Fact]
    public void ThePlaceholder_IsLaidOutLikeTheSearchText_SoTheCaretStartsWhereThePlaceholderStarts()
    {
        var xaml = Read("MainWindow.xaml");
        var search = Element(xaml, "ScanCodeTextBox");
        var placeholder = Element(xaml, "ScanPlaceholderTextBox");

        // Same control and template, same padding and font: the text offset is identical by construction.
        Assert.StartsWith("<TextBox", placeholder);
        Assert.Equal(Attribute(search, "Padding"), Attribute(placeholder, "Padding"));
        Assert.Equal(Attribute(search, "FontSize"), Attribute(placeholder, "FontSize"));
        Assert.Equal("False", Attribute(placeholder, "IsHitTestVisible"));
        Assert.Equal("False", Attribute(placeholder, "Focusable"));
    }

    [Fact]
    public void TheSearchBox_TakesTheFocus_OnOpenUnlockAfterASaleAndOnReturningToTheSale()
    {
        var code = Read("MainWindow.xaml.cs");

        Assert.Contains("private void FocusSearchBox()", code);
        Assert.Contains("Loaded += (_, _) => FocusSearchBox();", code);
        // Every former direct focus call goes through the one method (it places the caret at the start).
        Assert.Single(Regex.Matches(code, @"ScanCodeTextBox\.Focus\(\)"));
        var clears = Regex.Matches(code, @"_cart\.Clear\(\);").Count;
        var focusAfterClear = Regex.Matches(code, @"_cart\.Clear\(\);(\s*\w+\(\);)*\s*FocusSearchBox\(\);").Count;
        Assert.Equal(clears, focusAfterClear);
    }

    [Fact]
    public void AWeightedProduct_AsksForItsKilos_ThroughOneEntryPoint()
    {
        var code = Read("MainWindow.xaml.cs");

        Assert.Contains("private decimal? RequestMeasuredQuantity(", code);
        Assert.Single(Regex.Matches(code, @"new MeasuredQuantityWindow\("));
        Assert.True(File.Exists(Path.Combine(PosDirectory(), "MeasuredQuantityWindow.xaml")));
        var window = Read("MeasuredQuantityWindow.xaml");
        Assert.Contains("x:Name=\"KilosTextBox\"", window);
        Assert.Contains("IsCancel=\"True\"", window);
        Assert.Contains("IsDefault=\"True\"", window);
        Assert.Empty(Regex.Matches(window, @"\{StaticResource\s+\w*Brush\w*\}"));
    }

    [Fact]
    public void TheSaleActions_UseTheThemeSpacingTokens()
    {
        var theme = Read("Themes", "DesktopTheme.xaml");
        Assert.Contains("x:Key=\"ActionSpacing\"", theme);
        Assert.Contains("x:Key=\"ActionGroupSpacing\"", theme);

        Assert.Contains("{StaticResource ActionSpacing}", Read("Controls", "SaleLinesTable.xaml"));
        Assert.Contains("{StaticResource ActionSpacing}", Read("Controls", "TotalsPanel.xaml"));
        Assert.Contains("{StaticResource ActionGroupSpacing}", Read("MainWindow.xaml"));
    }
}
