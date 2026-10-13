using System.Globalization;
using Commerce.Domain.CurrentAccounts;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// owner feedback P3: the POS reads dates (and money) the Argentine way whatever Windows' language: dd/mm/aaaa, a day's
/// name in Spanish, "$ 1.234,00".
/// </summary>
public sealed class PosCultureTests
{
    [Fact]
    public void DatesAndMoney_ReadTheArgentineWay_EvenOnAnEnglishWindows()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var moment = new DateTime(2026, 1, 5, 9, 7, 0);

            Assert.Equal("05/01/2026 09:07", PosCulture.Moment(moment));
            Assert.Equal("05/01/2026", PosCulture.Day(DateOnly.FromDateTime(moment)));
            Assert.Equal("lunes 5 de enero", moment.ToString("dddd d 'de' MMMM", PosCulture.Spanish));
            Assert.Equal("$ 1.234,50", 1234.5m.ToString("C", PosCulture.Spanish).Replace('\u00a0', ' '));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void ADueDateAndATime_KeepTheirSlashesAndColons_OnAWindowsWithOtherSeparators()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            // German Windows: "." between the parts of a date; a "dd/MM/yyyy" read through it gives "04.11.2026".
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal(
                "Vence el 04/11/2026 (30 días, plazo general de la organización).",
                CustomerPaymentInput.DueText(new PaymentTerms(30, PaymentTermsSource.Organization), new DateOnly(2026, 10, 5)));
            Assert.Equal("09:07", PosCulture.Time(new DateTime(2026, 1, 5, 9, 7, 0)));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void NoScreen_ReadsADateOrATimeThroughWindowsCulture()
    {
        var root = Path.Combine(PostgresTestFixture.RepoRoot(), "src", "Commerce.Pos.Windows");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            Assert.False(source.Contains("\"HH:mm\", CultureInfo.CurrentCulture"), file);
            Assert.False(source.Contains("\"dd/MM/yyyy\", CultureInfo.CurrentCulture"), file);
        }
    }

    [Fact]
    public void TheSalesAndSyncScreens_NeverUseWindowsCulture_NorAnIsoOrShortDate()
    {
        var root = Path.Combine(PostgresTestFixture.RepoRoot(), "src", "Commerce.Pos.Windows");
        foreach (var file in new[] { "SalesView.xaml.cs", "MainWindow.xaml.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, file));
            Assert.DoesNotContain("CultureInfo.CurrentCulture", source);
            Assert.DoesNotContain("\"yyyy-MM-dd HH:mm\"", source);
            Assert.DoesNotContain("\"d/M HH:mm\"", source);
        }
    }
}
