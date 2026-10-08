using System.Globalization;
using Commerce.Domain.CashSessions;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The close-cash summary rows (<see cref="CloseCashLines"/>): one amount per row, so no label is ever pushed aside by a
/// long amount, and the expected cash shown as the sum it is.
/// </summary>
public sealed class PosCloseCashLinesTests
{
    private static string Money(decimal amount) => amount.ToString("C", CultureInfo.CurrentCulture);

    private static readonly CashSessionSummary Busy = new(
        OpeningFloat: 50_000m, SaleCount: 37, CashKept: 412_350.50m, CardTotal: 1_284_900m, QrTotal: 356_200m, UntenderedTotal: 0m,
        AccountTotal: 245_800m, CollectedCash: 120_000m, CollectedCard: 85_000m, CollectedQr: 15_500m, CollectionCount: 4,
        CashWithdrawn: 200_000m, CashDeposited: 30_000m, CashMovementCount: 3);

    [Fact]
    public void Sales_AreOneRowPerTender_AndTheAccountRowSaysItNeverEntersTheDrawer()
    {
        var lines = CloseCashLines.Sales(Busy);

        Assert.Equal(["Cantidad de ventas", "Con tarjeta", "Con QR", "A cuenta corriente"], lines.Select(l => l.Label));
        Assert.Equal(["37", Money(1_284_900m), Money(356_200m), Money(245_800m)], lines.Select(l => l.Amount));
        Assert.True(lines[3].HasHint);
    }

    [Fact]
    public void OtherMoney_PutsTheCollectionDetailUnderItsAmount_AndSplitsWithdrawalsFromDeposits()
    {
        var lines = CloseCashLines.OtherMoney(Busy);

        Assert.Equal(["Cobros de cuenta corriente", "Retiros de caja", "Ingresos de caja"], lines.Select(l => l.Label));
        Assert.Equal(Money(220_500m), lines[0].Amount);
        Assert.Equal($"4 cobros: efectivo {Money(120_000m)}, tarjeta {Money(85_000m)}, QR {Money(15_500m)}", lines[0].Hint);
        Assert.Equal($"−{Money(200_000m)}", lines[1].Amount);
        Assert.Equal($"+{Money(30_000m)}", lines[2].Amount);
    }

    [Fact]
    public void ExpectedCash_IsTheSumRowByRow_LeavingOutWhatAddsNothing()
    {
        var busy = CloseCashLines.ExpectedCash(Busy);
        Assert.Equal(["Efectivo inicial", "+ Efectivo de ventas", "+ Cobros en efectivo", "+ Ingresos", "− Retiros"], busy.Select(l => l.Label));

        var quiet = CloseCashLines.ExpectedCash(new CashSessionSummary(10_000m, 2, 5_000m, 0m, 0m, 0m));
        Assert.Equal(["Efectivo inicial", "+ Efectivo de ventas"], quiet.Select(l => l.Label));
        Assert.Equal(["Retiros de caja", "Ingresos de caja"], CloseCashLines.OtherMoney(new CashSessionSummary(10_000m, 2, 5_000m, 0m, 0m, 0m))
            .Skip(1).Where(l => l.Amount == "—").Select(l => l.Label));
    }
}
