using Commerce.Domain.Pricing;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine task 2.2 (RED+GREEN): pure, no-I/O unit
/// coverage for <see cref="PriceList"/>/<see cref="PriceListEntry"/>/
/// <see cref="Money"/>. The database-level append-only/one-default/
/// one-per-day proofs (task 2.3/2.5) live in
/// <see cref="MigrationRlsTests"/> alongside every other migration's RLS
/// proof, per this repo's existing convention — this file is intentionally
/// I/O-free, matching <c>UserAccountCustomerGuardTests</c>'s precedent.
/// </summary>
public sealed class PriceListTests
{
    [Fact]
    public void Round2_UsesAwayFromZero_NotBankersRounding()
    {
        // .NET's default MidpointRounding.ToEven would round 2.125 to 2.12;
        // AwayFromZero (design.md "Rounding policy") rounds it to 2.13 —
        // this is the exact behavior a hand-written Argentine price list
        // expects, and the one property this test locks in.
        Assert.Equal(2.13m, Money.Round2(2.125m));
        Assert.Equal(-2.13m, Money.Round2(-2.125m));
        Assert.Equal(10.00m, Money.Round2(10m));
    }

    [Fact]
    public void Round2_AppliedToLineTotals_SumEqualsSeparatelyRoundedTotal()
    {
        // Design.md "Rounding policy": unitNet = Round(unitList * (1 -
        // d/100), 2), then lineTotal = Round(unitNet * quantity, 2). The
        // sale total is the PLAIN SUM of already-rounded line totals, never
        // re-rounded — this test proves that composition holds for a
        // representative 3-line sale.
        var unitNet1 = Money.Round2(100.00m * (1 - 0.10m));
        var lineTotal1 = Money.Round2(unitNet1 * 3);

        var unitNet2 = Money.Round2(49.99m);
        var lineTotal2 = Money.Round2(unitNet2 * 2);

        var unitNet3 = Money.Round2(10.005m);
        var lineTotal3 = Money.Round2(unitNet3 * 7);

        var total = lineTotal1 + lineTotal2 + lineTotal3;

        Assert.Equal(270.00m, lineTotal1);
        Assert.Equal(99.98m, lineTotal2);
        Assert.Equal(70.07m, lineTotal3);
        Assert.Equal(440.05m, total);
    }

    [Fact]
    public void PriceListEntry_Construction_RoundTripsAllFields()
    {
        var id = Guid.NewGuid();
        var priceListId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var effectiveFrom = new DateOnly(2026, 1, 1);

        var entry = new PriceListEntry(id, priceListId, presentationId, 150.00m, effectiveFrom);

        Assert.Equal(id, entry.Id);
        Assert.Equal(priceListId, entry.PriceListId);
        Assert.Equal(presentationId, entry.PresentationId);
        Assert.Equal(150.00m, entry.UnitPrice);
        Assert.Equal(effectiveFrom, entry.EffectiveFrom);
        Assert.Equal("Manual", entry.Source);
        Assert.Null(entry.ImportBatchId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PriceListEntry_Construction_RejectsNonPositiveUnitPrice(decimal invalidUnitPrice)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PriceListEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), invalidUnitPrice, DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Fact]
    public void PriceList_Construction_RoundTripsAllFields()
    {
        var id = Guid.NewGuid();
        var organizationId = Guid.NewGuid();

        var priceList = new PriceList(id, organizationId, "Default", isDefault: true);

        Assert.Equal(id, priceList.Id);
        Assert.Equal(organizationId, priceList.OrganizationId);
        Assert.Equal("Default", priceList.Name);
        Assert.True(priceList.IsDefault);
    }
}
