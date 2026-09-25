using Commerce.Application.Pricing;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Pricing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Pricing;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-price-composition slice 2 — the whole
/// `specs/pricing-resolution/spec.md` delta: composition happens inside
/// <see cref="PricingResolutionService"/>, from the effective entry's BASE
/// price, using the components effective on the RESOLUTION date, and strictly
/// BEFORE the customer's `DiscountPercentage`.
///
/// Most cases are pure (stub sources, no I/O). The live-Postgres case at the
/// bottom proves the same thing end to end through the real
/// <see cref="PostgresRateComponentSource"/> over migration `0013`, and is
/// skipped-with-a-message when Postgres is unreachable, matching the fixture
/// convention in this project.
/// </summary>
[Collection("Postgres")]
public sealed class PricingCompositionTests : IDisposable
{
    // --- Stubs -------------------------------------------------------------

    private sealed class StubEffectivePriceSource(decimal? unitPrice) : IEffectivePriceSource
    {
        public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct)
            => Task.FromResult(unitPrice);
    }

    /// <summary>
    /// Holds dated sets and applies the SAME "latest EffectiveFrom at or
    /// before the date" rule the Postgres source applies in SQL, so the
    /// effective-dating scenarios are exercised against real selection logic
    /// rather than a hard-coded answer.
    /// </summary>
    private sealed class StubRateComponentSource(params RateComponentSet[] sets) : IEffectiveRateComponentSource
    {
        public Task<RateComponentSet?> GetEffectiveSetAsync(DateOnly effectiveOn, CancellationToken ct)
            => Task.FromResult(sets
                .Where(s => s.EffectiveFrom <= effectiveOn)
                .OrderByDescending(s => s.EffectiveFrom)
                .FirstOrDefault());
    }

    private static RateComponent Component(string code, decimal percentage, RateCalculationBase calculationBase, int order) =>
        new(code, $"{code} ({percentage}%)", percentage, calculationBase, order);

    /// <summary>Vaca Verde's real delivery sheet: four rates, ALL on the base price, summing to x1.45.</summary>
    private static IReadOnlyList<RateComponent> VacaVerdeComponents(RateCalculationBase calculationBase = RateCalculationBase.Base) =>
    [
        Component("IVA", 10.5m, calculationBase, 1),
        Component("IB", 2.5m, calculationBase, 2),
        Component("FLETE", 7m, calculationBase, 3),
        Component("REMARCACION", 25m, calculationBase, 4),
    ];

    private static RateComponentSet VacaVerdeSet(
        DateOnly effectiveFrom, RateCalculationBase calculationBase = RateCalculationBase.Base) =>
        RateComponentSet.ForPriceList(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), effectiveFrom, VacaVerdeComponents(calculationBase));

    private static readonly DateOnly Published = new(2026, 1, 1);
    private static readonly DateOnly ResolveOn = new(2026, 3, 1);

    private static PricingResolutionService ServiceFor(decimal? basePrice, params RateComponentSet[] sets) =>
        new(new StubEffectivePriceSource(basePrice), new StubRateComponentSource(sets));

    // --- Requirement: Vaca Verde Delivery List Composition -----------------

    /// <summary>
    /// Spec scenarios "Asado completo / Bola de lomo / Entraña composes to its
    /// sheet value": the three rows verified against the real spreadsheet. All
    /// four components declare `Base`, so the composition is base x 1.45 and
    /// the results are exact — no rounding tolerance is needed or allowed.
    /// </summary>
    [Theory]
    [InlineData(10600, 15370)]
    [InlineData(11400, 16530)]
    [InlineData(22500, 32625)]
    public async Task ResolveAsync_VacaVerdeDeliveryList_GuestContext_ComposesTheSheetValue(int basePrice, int expected)
    {
        var service = ServiceFor(basePrice, VacaVerdeSet(Published));

        var outcome = await service.ResolveAsync(
            Guid.NewGuid(), quantity: 1m, discountPercentage: null, ResolveOn, CancellationToken.None);

        var resolved = Assert.IsType<PriceResolutionOutcome.Resolved>(outcome);
        Assert.Equal((decimal)expected, resolved.UnitListPrice);
        Assert.Equal((decimal)expected, resolved.UnitNetPrice);
    }

    /// <summary>
    /// Spec scenario "Components do not compound": the SAME four percentages
    /// declared with `Subtotal` instead of `Base` compound, producing a
    /// strictly greater result. This is the negative case that makes the
    /// explicit calculation base load-bearing: if `Compose` ignored the
    /// declared base and always used one of the two, one of these two tests
    /// would fail.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_SameFourPercentagesOnSubtotal_ComposesStrictlyAboveTheBaseCalculatedResult()
    {
        var onBase = ServiceFor(10600m, VacaVerdeSet(Published, RateCalculationBase.Base));
        var onSubtotal = ServiceFor(10600m, VacaVerdeSet(Published, RateCalculationBase.Subtotal));

        var baseOutcome = await onBase.ResolveAsync(Guid.NewGuid(), 1m, null, ResolveOn, CancellationToken.None);
        var subtotalOutcome = await onSubtotal.ResolveAsync(Guid.NewGuid(), 1m, null, ResolveOn, CancellationToken.None);

        var baseResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(baseOutcome);
        var subtotalResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(subtotalOutcome);

        Assert.Equal(15370m, baseResolved.UnitListPrice);
        Assert.True(
            subtotalResolved.UnitListPrice > 15370m,
            $"compounding on the subtotal must exceed 15,370; got {subtotalResolved.UnitListPrice}.");
        // 10,600 x 1.105 x 1.025 x 1.07 x 1.25 — pinned exactly so a future
        // change to the chaining rule cannot hide behind the inequality above.
        Assert.Equal(16057.7909375m, subtotalResolved.UnitListPrice);
    }

    // --- Requirement: Resolution Composes Rate Components Before The Customer Discount ---

    /// <summary>
    /// Spec scenario "Composition precedes the discount".
    ///
    /// MUTATION-CHECK NOTE, and the reason this test asserts
    /// <c>UnitListPrice</c> and not only <c>UnitNetPrice</c>: composition and
    /// the discount are BOTH multiplications, so they commute — discounting
    /// 10,600 first and composing after yields the same 13,833 net. The order
    /// is therefore only observable in the LIST price, which the spec defines
    /// as the COMPOSED price the discount is applied to. Inverting the order
    /// in `PricingResolutionService` makes `UnitListPrice` come back as
    /// 10,600 (or 9,540), and this assertion is what catches it.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_RegisteredCustomer_AppliesTheDiscountToTheComposedListPrice_NotToTheBase()
    {
        var service = ServiceFor(10600m, VacaVerdeSet(Published));

        var outcome = await service.ResolveAsync(
            Guid.NewGuid(), quantity: 1m, discountPercentage: 10m, ResolveOn, CancellationToken.None);

        var resolved = Assert.IsType<PriceResolutionOutcome.Resolved>(outcome);
        // The list price IS the composed price — never the stored base.
        Assert.Equal(15370m, resolved.UnitListPrice);
        Assert.NotEqual(10600m, resolved.UnitListPrice);
        Assert.Equal(10m, resolved.AppliedDiscountPercentage);
        Assert.Equal(13833m, resolved.UnitNetPrice);
    }

    /// <summary>
    /// Spec scenario "Components are applied in their declared order": with a
    /// `Subtotal` component present, order becomes observable. `FLETE` on the
    /// subtotal AFTER a 10.5% base VAT sees 11,713; placed FIRST it would see
    /// 10,600 and the composed price would be lower.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_SubtotalComponent_UsesTheSubtotalProducedByTheComponentsOrderedBeforeIt()
    {
        var late = RateComponentSet.ForPriceList(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Published,
            [
                Component("IVA", 10.5m, RateCalculationBase.Base, 1),
                Component("FLETE", 7m, RateCalculationBase.Subtotal, 2),
            ]);
        var early = RateComponentSet.ForPriceList(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Published,
            [
                Component("FLETE", 7m, RateCalculationBase.Subtotal, 1),
                Component("IVA", 10.5m, RateCalculationBase.Base, 2),
            ]);

        var lateOutcome = await ServiceFor(10600m, late)
            .ResolveAsync(Guid.NewGuid(), 1m, null, ResolveOn, CancellationToken.None);
        var earlyOutcome = await ServiceFor(10600m, early)
            .ResolveAsync(Guid.NewGuid(), 1m, null, ResolveOn, CancellationToken.None);

        var lateResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(lateOutcome);
        var earlyResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(earlyOutcome);

        // 10,600 + 1,113.00 (IVA on base) + 819.91 (FLETE on 11,713)
        Assert.Equal(12532.91m, lateResolved.UnitListPrice);
        // 10,600 + 742.00 (FLETE on 10,600) + 1,113.00 (IVA on base)
        Assert.Equal(12455m, earlyResolved.UnitListPrice);
        Assert.True(earlyResolved.UnitListPrice < lateResolved.UnitListPrice);
    }

    // --- Requirement: Empty Composition Resolves To The Base Price ---------

    /// <summary>
    /// Spec scenario "No component set yields the base price unchanged". This
    /// is also the property the whole `UnitPrice` reinterpretation rests on:
    /// turning slice 2 on changes NO existing resolved price, because every
    /// list that has published no components composes to the identity.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WithNoEffectiveComponentSet_ReturnsTheStoredBasePriceUnchanged()
    {
        var service = ServiceFor(15370m);

        var outcome = await service.ResolveAsync(
            Guid.NewGuid(), 1m, null, ResolveOn, CancellationToken.None);

        var resolved = Assert.IsType<PriceResolutionOutcome.Resolved>(outcome);
        Assert.Equal(15370m, resolved.UnitListPrice);
        Assert.Equal(15370m, resolved.UnitNetPrice);
    }

    /// <summary>
    /// The same identity must hold for a host that supplies NO component
    /// source at all (the POS today: its replica carries prices, not rate
    /// components). An absent source is an empty composition, never an error
    /// and never a zero.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WithNoRateComponentSourceAtAll_ReturnsTheStoredBasePriceUnchanged()
    {
        var service = new PricingResolutionService(new StubEffectivePriceSource(15370m));

        var outcome = await service.ResolveAsync(
            Guid.NewGuid(), 1m, null, ResolveOn, CancellationToken.None);

        var resolved = Assert.IsType<PriceResolutionOutcome.Resolved>(outcome);
        Assert.Equal(15370m, resolved.UnitListPrice);
    }

    /// <summary>
    /// An empty PUBLISHED set is distinct from no set: the spec's
    /// all-or-nothing inheritance means a list that declares an empty set gets
    /// an empty composition, not a fallback. Either way the result is the base.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WithAnExplicitlyEmptyComponentSet_ReturnsTheStoredBasePriceUnchanged()
    {
        var empty = RateComponentSet.ForPriceList(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Published, []);
        var service = ServiceFor(15370m, empty);

        var outcome = await service.ResolveAsync(
            Guid.NewGuid(), 1m, null, ResolveOn, CancellationToken.None);

        var resolved = Assert.IsType<PriceResolutionOutcome.Resolved>(outcome);
        Assert.Equal(15370m, resolved.UnitListPrice);
    }

    /// <summary>
    /// Spec scenario "Absent components and absent prices are treated
    /// differently": an absent PRICE keeps failing loudly with the typed
    /// outcome even when the composition is also empty. Composition must never
    /// turn a missing price into a resolvable zero.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WithNoEffectivePriceAndNoComponents_StillReturnsTypedNoEffectivePrice()
    {
        var presentationId = Guid.NewGuid();
        var service = ServiceFor(null);

        var outcome = await service.ResolveAsync(presentationId, 1m, null, ResolveOn, CancellationToken.None);

        var noPrice = Assert.IsType<PriceResolutionOutcome.NoEffectivePrice>(outcome);
        Assert.Equal(presentationId, noPrice.PresentationId);
        Assert.Equal(ResolveOn, noPrice.On);
    }

    // --- Requirement: Composition Uses The Rates Effective On The Resolution Date ---

    /// <summary>
    /// Spec scenario "A later rate change does not alter an earlier
    /// resolution": IVA 10.5% from 2026-01-01, IVA 21% from 2026-06-01,
    /// resolving on 2026-03-01 must use 10.5%.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WithALaterRateSetPublished_UsesTheSetEffectiveOnTheResolutionDate()
    {
        var older = RateComponentSet.ForPriceList(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1),
            [Component("IVA", 10.5m, RateCalculationBase.Base, 1)]);
        var newer = RateComponentSet.ForPriceList(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 6, 1),
            [Component("IVA", 21m, RateCalculationBase.Base, 1)]);

        var service = ServiceFor(10000m, older, newer);

        var before = await service.ResolveAsync(Guid.NewGuid(), 1m, null, new DateOnly(2026, 3, 1), CancellationToken.None);
        var after = await service.ResolveAsync(Guid.NewGuid(), 1m, null, new DateOnly(2026, 7, 1), CancellationToken.None);

        Assert.Equal(11050m, Assert.IsType<PriceResolutionOutcome.Resolved>(before).UnitListPrice);
        Assert.Equal(12100m, Assert.IsType<PriceResolutionOutcome.Resolved>(after).UnitListPrice);
    }

    /// <summary>
    /// Spec scenario "Entry and component effective dates are selected
    /// independently": an entry effective 2026-01-01 composed on 2026-07-01
    /// uses the component set effective 2026-06-01, even though no new entry
    /// was published on that date.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_EntryAndComponentSetDates_AreSelectedIndependently()
    {
        // The price source is date-insensitive here on purpose: it stands for
        // the single 2026-01-01 entry, which is effective on every later date.
        var componentsFromJune = RateComponentSet.ForPriceList(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 6, 1), VacaVerdeComponents());
        var service = ServiceFor(10600m, componentsFromJune);

        var beforeComponents = await service.ResolveAsync(
            Guid.NewGuid(), 1m, null, new DateOnly(2026, 3, 1), CancellationToken.None);
        var afterComponents = await service.ResolveAsync(
            Guid.NewGuid(), 1m, null, new DateOnly(2026, 7, 1), CancellationToken.None);

        // Same entry, two dates: bare base before the set exists, composed after.
        Assert.Equal(10600m, Assert.IsType<PriceResolutionOutcome.Resolved>(beforeComponents).UnitListPrice);
        Assert.Equal(15370m, Assert.IsType<PriceResolutionOutcome.Resolved>(afterComponents).UnitListPrice);
    }

    // --- Requirement: Guest And Registered Divergence Over The Composed Price ---

    /// <summary>
    /// Spec scenarios "Guest resolves to the composed list price" and
    /// "Registered customer resolves below the composed list price": both
    /// contexts share ONE composed list price, and the divergence is the
    /// discount alone.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_GuestAndRegistered_ShareTheComposedListPriceAndDivergeOnlyByTheDiscount()
    {
        var presentationId = Guid.NewGuid();
        var guest = ServiceFor(10600m, VacaVerdeSet(Published));
        var registered = ServiceFor(10600m, VacaVerdeSet(Published));

        var guestOutcome = await guest.ResolveAsync(presentationId, 1m, null, ResolveOn, CancellationToken.None);
        var registeredOutcome = await registered.ResolveAsync(presentationId, 1m, 10m, ResolveOn, CancellationToken.None);

        var guestResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(guestOutcome);
        var registeredResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(registeredOutcome);

        Assert.Equal(15370m, guestResolved.UnitListPrice);
        Assert.Equal(0m, guestResolved.AppliedDiscountPercentage);
        Assert.Equal(15370m, guestResolved.UnitNetPrice);

        Assert.Equal(guestResolved.UnitListPrice, registeredResolved.UnitListPrice);
        Assert.True(registeredResolved.UnitNetPrice < guestResolved.UnitNetPrice);
        Assert.Equal(13833m, registeredResolved.UnitNetPrice);
    }

    // --- Requirement: Composition Is Part Of The Single Resolution Authority ---

    /// <summary>
    /// The port carries no channel or caller-identity parameter and no
    /// caller-supplied component set, so "a client composes its own price" is
    /// structurally unexpressible rather than merely forbidden — the same
    /// argument `IEffectivePriceSource` already makes for prices. Asserted by
    /// reflection so adding such a parameter breaks this test rather than
    /// quietly widening the authority.
    /// </summary>
    [Fact]
    public void ResolveAsync_Signature_AcceptsNoCallerSuppliedPriceOrComponentSet()
    {
        var parameters = typeof(PricingResolutionService)
            .GetMethod(nameof(PricingResolutionService.ResolveAsync))!
            .GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(RateComponentSet));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IReadOnlyList<RateComponent>));
        Assert.Equal(
            ["presentationId", "quantity", "discountPercentage", "effectiveOn", "ct"],
            parameters.Select(p => p.Name!).ToArray());
    }

    // --- Live Postgres: the same thing, end to end -------------------------

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PricingCompositionTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        PostgresTestFixture.ApplyPricingMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();


    private static void SeedOrganization(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Vaca Verde')", owner);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The whole slice through real infrastructure: a base-priced entry and
    /// Vaca Verde's four components, both persisted, resolved by the real
    /// <see cref="PostgresEffectivePriceSource"/> +
    /// <see cref="PostgresRateComponentSource"/> pair. Proves the wiring, not
    /// just the arithmetic — a stub can't show that the store's effective-set
    /// query is the one resolution calls.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_OverLivePostgres_ComposesTheStoredBaseWithTheStoredComponents()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();

        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var product = await catalogStore.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Asado completo", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);
        var presentation = await catalogStore.CreatePresentationAsync(
            scope, new NewPresentation(Guid.NewGuid(), product.Id, "Kg", QuantityBehavior.FixedQuantity, Guid.NewGuid(), null, actorId),
            "org-user", actorId, CancellationToken.None);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Reparto", true, actorId), "org-user", actorId, CancellationToken.None);

        // The cutover shape the design mandates: the BASE-priced entry and the
        // component set published with the SAME EffectiveFrom, as one dated event.
        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentation.Id, 10600m, Published, "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);

        var componentStore = new PostgresRateComponentStore(_dataSource!);
        await componentStore.PublishSetAsync(
            scope,
            new NewRateComponentSet(Guid.NewGuid(), priceList.Id, Published, VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None);

        var service = new PricingResolutionService(
            new PostgresEffectivePriceSource(priceStore, scope, priceList.Id),
            new PostgresRateComponentSource(componentStore, scope, priceList.Id));

        var guest = await service.ResolveAsync(presentation.Id, 2m, null, ResolveOn, CancellationToken.None);
        var registered = await service.ResolveAsync(presentation.Id, 2m, 10m, ResolveOn, CancellationToken.None);

        var guestResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(guest);
        var registeredResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(registered);

        Assert.Equal(15370m, guestResolved.UnitListPrice);
        Assert.Equal(15370m, guestResolved.UnitNetPrice);
        Assert.Equal(30740m, guestResolved.LineTotal);

        Assert.Equal(15370m, registeredResolved.UnitListPrice);
        Assert.Equal(13833m, registeredResolved.UnitNetPrice);
        Assert.Equal(27666m, registeredResolved.LineTotal);
    }

    /// <summary>
    /// The pre-slice-2 world, proven against a real database: a price list
    /// that has published NO component set resolves to exactly the number it
    /// resolved to before this slice existed. This is the regression guard for
    /// the reinterpretation of `PriceListEntry.UnitPrice` — if it ever fails,
    /// every already-imported catalog silently repriced.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_OverLivePostgres_WithNoPublishedComponents_ResolvesToTheStoredAmountUnchanged()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();

        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var product = await catalogStore.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Legacy product", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);
        var presentation = await catalogStore.CreatePresentationAsync(
            scope, new NewPresentation(Guid.NewGuid(), product.Id, "Kg", QuantityBehavior.FixedQuantity, Guid.NewGuid(), null, actorId),
            "org-user", actorId, CancellationToken.None);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Legacy list", true, actorId), "org-user", actorId, CancellationToken.None);

        // A row imported BEFORE this change: it holds a fully-loaded final price.
        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentation.Id, 15370m, Published, "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);

        var service = new PricingResolutionService(
            new PostgresEffectivePriceSource(priceStore, scope, priceList.Id),
            new PostgresRateComponentSource(new PostgresRateComponentStore(_dataSource!), scope, priceList.Id));

        var outcome = await service.ResolveAsync(presentation.Id, 1m, null, ResolveOn, CancellationToken.None);

        var resolved = Assert.IsType<PriceResolutionOutcome.Resolved>(outcome);
        Assert.Equal(15370m, resolved.UnitListPrice);
        Assert.Equal(15370m, resolved.UnitNetPrice);
    }

    /// <summary>
    /// R4-per-line-set-fetch. `PostgresRateComponentSource` now memoizes the
    /// effective set per instance, keyed by DATE, so an N-line submission makes
    /// one lookup per distinct resolution date instead of N connection
    /// acquisitions, transactions and header queries for the same row.
    ///
    /// The risk a cache introduces is staleness, and the only staleness that
    /// could matter here is across dates — so that is what this pins: the same
    /// instance, asked for two different dates, must return the two different
    /// sets, and asking again for the first date must still return the first
    /// set rather than the newer one. A single-slot cache, the obvious wrong
    /// implementation, fails both halves.
    /// </summary>
    [Fact]
    public async Task PostgresRateComponentSource_CachesPerDate_AndStillHonoursEffectiveDating()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Reparto", true, actorId), "org-user", actorId, CancellationToken.None);

        var componentStore = new PostgresRateComponentStore(_dataSource!);
        await componentStore.PublishSetAsync(
            scope,
            new NewRateComponentSet(
                Guid.NewGuid(), priceList.Id, new DateOnly(2026, 1, 1),
                [Component("REMARCACION", 25m, RateCalculationBase.Base, 1)], actorId),
            "org-user", actorId, CancellationToken.None);
        await componentStore.PublishSetAsync(
            scope,
            new NewRateComponentSet(
                Guid.NewGuid(), priceList.Id, new DateOnly(2026, 6, 1),
                [Component("REMARCACION", 45m, RateCalculationBase.Base, 1)], actorId),
            "org-user", actorId, CancellationToken.None);

        var source = new PostgresRateComponentSource(componentStore, scope, priceList.Id);

        var january = await source.GetEffectiveSetAsync(new DateOnly(2026, 3, 1), CancellationToken.None);
        var july = await source.GetEffectiveSetAsync(new DateOnly(2026, 7, 1), CancellationToken.None);
        // The repeat: served from the cache, and it must still be January's.
        var januaryAgain = await source.GetEffectiveSetAsync(new DateOnly(2026, 3, 1), CancellationToken.None);

        Assert.Equal(12_500m, january!.Compose(10_000m));
        Assert.Equal(14_500m, july!.Compose(10_000m));
        Assert.Equal(12_500m, januaryAgain!.Compose(10_000m));
        Assert.Same(january, januaryAgain);
    }

    /// <summary>
    /// The `null` answer — "no set effective for this date" — is cached too.
    /// It is a real answer, composed as the identity, and it is the case an
    /// unpublished list hits on EVERY line of EVERY order, so a cache that
    /// only remembered non-null sets would leave the most common path
    /// re-querying per line.
    /// </summary>
    [Fact]
    public async Task PostgresRateComponentSource_CachesTheAbsenceOfASetToo()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Sin recargos", true, actorId), "org-user", actorId, CancellationToken.None);

        var componentStore = new PostgresRateComponentStore(_dataSource!);
        var source = new PostgresRateComponentSource(componentStore, scope, priceList.Id);

        Assert.Null(await source.GetEffectiveSetAsync(new DateOnly(2026, 3, 1), CancellationToken.None));

        // Published AFTER the first lookup. A request-scoped source is
        // deliberately allowed to keep its answer for its own lifetime; what
        // must never happen is a NEW source seeing the stale one.
        await componentStore.PublishSetAsync(
            scope,
            new NewRateComponentSet(
                Guid.NewGuid(), priceList.Id, new DateOnly(2026, 1, 1),
                [Component("REMARCACION", 45m, RateCalculationBase.Base, 1)], actorId),
            "org-user", actorId, CancellationToken.None);

        Assert.Null(await source.GetEffectiveSetAsync(new DateOnly(2026, 3, 1), CancellationToken.None));

        var fresh = new PostgresRateComponentSource(componentStore, scope, priceList.Id);
        Assert.NotNull(await fresh.GetEffectiveSetAsync(new DateOnly(2026, 3, 1), CancellationToken.None));
    }
}
