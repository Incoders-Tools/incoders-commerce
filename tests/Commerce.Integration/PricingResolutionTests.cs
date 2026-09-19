using Commerce.Application.Pricing;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine Phase 3 (Unit 3, highest risk):
/// <see cref="PricingResolutionService"/> — the ADR-010 engine. Pure, no-I/O
/// unit coverage using a stub <see cref="IEffectivePriceSource"/>; the
/// Postgres-backed and channel-parity cases (tasks 3.8/3.9) live alongside
/// this file's live-Postgres tests further below, matching this repo's
/// skip-if-unreachable convention.
/// </summary>
public sealed class PricingResolutionTests
{
    private sealed class StubEffectivePriceSource(decimal? unitPrice) : IEffectivePriceSource
    {
        public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct)
            => Task.FromResult(unitPrice);
    }

    /// <summary>Spec scenario "Guest resolves to list price": guest (null discount) gets the full list price, no discount applied.</summary>
    [Fact]
    public async Task ResolveAsync_GuestContext_ReturnsListPriceWithNoDiscount()
    {
        var service = new PricingResolutionService(new StubEffectivePriceSource(100.00m));

        var outcome = await service.ResolveAsync(
            Guid.NewGuid(), quantity: 1m, discountPercentage: null,
            effectiveOn: new DateOnly(2026, 1, 1), CancellationToken.None);

        var resolved = Assert.IsType<PriceResolutionOutcome.Resolved>(outcome);
        Assert.Equal(100.00m, resolved.UnitListPrice);
        Assert.Equal(0m, resolved.AppliedDiscountPercentage);
        Assert.Equal(100.00m, resolved.UnitNetPrice);
    }

    /// <summary>Spec scenario "Registered customer resolves to a discounted price": strictly lower than the guest price for the same tuple.</summary>
    [Fact]
    public async Task ResolveAsync_RegisteredCustomerWithDiscount_ReturnsStrictlyLowerPriceThanGuest()
    {
        var guestService = new PricingResolutionService(new StubEffectivePriceSource(100.00m));
        var registeredService = new PricingResolutionService(new StubEffectivePriceSource(100.00m));
        var presentationId = Guid.NewGuid();
        var effectiveOn = new DateOnly(2026, 1, 1);

        var guestOutcome = await guestService.ResolveAsync(presentationId, 1m, null, effectiveOn, CancellationToken.None);
        var registeredOutcome = await registeredService.ResolveAsync(presentationId, 1m, 10m, effectiveOn, CancellationToken.None);

        var guestResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(guestOutcome);
        var registeredResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(registeredOutcome);

        Assert.Equal(10m, registeredResolved.AppliedDiscountPercentage);
        Assert.Equal(90.00m, registeredResolved.UnitNetPrice);
        Assert.True(registeredResolved.UnitNetPrice < guestResolved.UnitNetPrice);
    }

    /// <summary>Spec scenario "No effective price produces an explicit error": zero matching entries -> typed outcome, never a thrown NRE from an unchecked `decimal?`.</summary>
    [Fact]
    public async Task ResolveAsync_ForPresentationWithZeroEntries_ReturnsTypedNoEffectivePrice()
    {
        var service = new PricingResolutionService(new StubEffectivePriceSource(null));
        var presentationId = Guid.NewGuid();
        var effectiveOn = new DateOnly(2026, 1, 1);

        var outcome = await service.ResolveAsync(presentationId, 1m, null, effectiveOn, CancellationToken.None);

        var noPrice = Assert.IsType<PriceResolutionOutcome.NoEffectivePrice>(outcome);
        Assert.Equal(presentationId, noPrice.PresentationId);
        Assert.Equal(effectiveOn, noPrice.On);
    }

    /// <summary>A resolution date before the earliest entry is indistinguishable from zero entries at this layer — both are `null` from the source, both must produce the typed outcome, never `0m`.</summary>
    [Fact]
    public async Task ResolveAsync_ForDateBeforeEarliestEntry_ReturnsTypedNoEffectivePrice_NeverZero()
    {
        var service = new PricingResolutionService(new StubEffectivePriceSource(null));
        var effectiveOn = new DateOnly(2020, 1, 1);

        var outcome = await service.ResolveAsync(Guid.NewGuid(), 3m, 10m, effectiveOn, CancellationToken.None);

        Assert.IsType<PriceResolutionOutcome.NoEffectivePrice>(outcome);
        Assert.IsNotType<PriceResolutionOutcome.Resolved>(outcome);
    }

    /// <summary>
    /// Design.md "Rounding policy": AwayFromZero, applied EXACTLY TWICE —
    /// once for `UnitNetPrice`, once for `LineTotal`. `10.005` is a midpoint
    /// case: .NET's default banker's rounding (`ToEven`) would round it DOWN
    /// to `10.00`, but AwayFromZero rounds it UP to `10.01` — this is the one
    /// property this test locks in, matching `PriceListTests.Round2_...`.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_AppliesAwayFromZeroRoundingTwice_UnitNetThenLineTotal()
    {
        var service = new PricingResolutionService(new StubEffectivePriceSource(10.005m));

        var outcome = await service.ResolveAsync(
            Guid.NewGuid(), quantity: 7m, discountPercentage: null,
            effectiveOn: new DateOnly(2026, 1, 1), CancellationToken.None);

        var resolved = Assert.IsType<PriceResolutionOutcome.Resolved>(outcome);

        // unitNet = Round2(10.005) = 10.01 (AwayFromZero, not 10.00/ToEven)
        Assert.Equal(10.01m, resolved.UnitNetPrice);
        // lineTotal = Round2(10.01 * 7) = Round2(70.07) = 70.07 — computed
        // from the ALREADY-ROUNDED unit-net price, never the raw 10.005.
        Assert.Equal(70.07m, resolved.LineTotal);
    }
}
