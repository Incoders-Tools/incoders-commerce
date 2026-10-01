using System.Globalization;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Tenancy;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// pos-scan-sale "Sale Number": what the operator reads after a sale. The text shows
/// the human number (never the GUID, never the database file name), says so when the
/// number is still pending, and the tooltip explains how the number is composed.
/// </summary>
public sealed class SaleResultMessageTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static SaleEffect Effect(bool numbered = true) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 855m, DateTimeOffset.UnixEpoch,
            BranchCode: numbered ? 1 : null, RegisterNumber: numbered ? 1 : null, SaleSequence: numbered ? 125 : null);

    [Fact]
    public void ANumberedSale_ShowsItsNumberTotalAndTender_WithoutTheGuidOrTheDatabaseName()
    {
        var effect = Effect();

        var text = SaleResultMessage.Registered(effect, "Efectivo", Culture);

        Assert.StartsWith("Venta V01-C1-125 registrada por ", text);
        Assert.EndsWith(" (Efectivo).", text);
        Assert.DoesNotContain(effect.SaleId.ToString(), text);
        Assert.DoesNotContain("branch.db", text);
    }

    [Fact]
    public void AnUnnumberedSale_SaysTheNumberIsPending()
    {
        var effect = Effect(numbered: false);

        var text = SaleResultMessage.Registered(effect, "Tarjeta", Culture);

        Assert.StartsWith("Venta registrada por ", text);
        Assert.Contains("Tarjeta", text);
        Assert.EndsWith("número pendiente).", text);
        Assert.DoesNotContain(effect.SaleId.ToString(), text);
        Assert.DoesNotContain("branch.db", text);
    }

    [Theory]
    [InlineData(true, "La venta V01-C1-125 ya estaba registrada (reintento idempotente).")]
    [InlineData(false, "Esta venta ya estaba registrada (reintento idempotente).")]
    public void ARetry_NamesTheSaleByItsNumber_OrWithoutOne_NeverByGuid(bool numbered, string expected)
    {
        var effect = Effect(numbered);

        var text = SaleResultMessage.AlreadyRegistered(effect);

        Assert.Equal(expected, text);
        Assert.DoesNotContain(effect.SaleId.ToString(), text);
    }

    [Fact]
    public void TheTooltip_ExplainsEveryPartOfTheNumber()
    {
        var tooltip = SaleResultMessage.Composition(Effect());

        Assert.Equal("V = Venta · 01 = Sucursal · C1 = Caja 1 · 125 = número de venta de esta caja", tooltip);
    }

    [Fact]
    public void ThereIsNoTooltip_ForASaleWithoutANumber()
    {
        Assert.Null(SaleResultMessage.Composition(Effect(numbered: false)));
    }

    private static DevicePairing Pairing(int? code, int? register) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Ruta 51", "op@example.com", "token") { BranchCode = code, RegisterNumber = register };

    [Fact]
    public void ThePairingNumbersItsSales_OnlyOnceItKnowsBothTheBranchCodeAndTheRegister()
    {
        var numbering = TerminalIdentityRefresher.NumberingOf(Pairing(1, 2));

        Assert.Equal(new SaleNumbering(new BranchCode(1), new RegisterNumber(2)), numbering);
        Assert.Null(TerminalIdentityRefresher.NumberingOf(Pairing(null, null)));
        Assert.Null(TerminalIdentityRefresher.NumberingOf(Pairing(1, null)));
        Assert.Null(TerminalIdentityRefresher.NumberingOf(Pairing(null, 2)));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1000, 2)]
    [InlineData(1, 1000)]
    public void AnOutOfRangeStoredIdentity_IsTreatedAsUnknown_NotAsACrash(int code, int register)
    {
        Assert.Null(TerminalIdentityRefresher.NumberingOf(Pairing(code, register)));
    }

    [Fact]
    public void BothSaleHandlers_NumberTheSale_AndShowTheNumberedMessage_NeverTheGuidOrTheDatabaseName()
    {
        var code = File.ReadAllText(Path.Combine(PostgresTestFixture.RepoRoot(), "src", "Commerce.Pos.Windows", "MainWindow.xaml.cs"));

        var manual = code[code.IndexOf("private void CommitSaleButton_Click", StringComparison.Ordinal)..];
        manual = manual[..manual.IndexOf("private void RefreshCustomerPicker", StringComparison.Ordinal)];
        var scanned = code[code.IndexOf("private void CommitScannedSaleButton_Click", StringComparison.Ordinal)..];
        scanned = scanned[..scanned.IndexOf("Task 7.7", StringComparison.Ordinal)];

        foreach (var handler in new[] { manual, scanned })
        {
            Assert.Contains("numbering: TerminalIdentityRefresher.NumberingOf(_pairing)", handler);
            Assert.Contains("ShowSaleResult(", handler);
            Assert.DoesNotContain("branch.db", handler);
            Assert.DoesNotContain("Effect.SaleId", handler);
        }
    }
}
