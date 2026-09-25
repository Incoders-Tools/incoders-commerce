using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Pricing;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Live-Postgres coverage for commerce-price-composition slice 1's
/// persistence half: `PostgresRateComponentStore` against the real `0013` +
/// `0014` schema (`specs/price-list-management/spec.md` requirements "Price List
/// Rate Components", "Rate Components Are Scoped To The List, Not The
/// Organization", "Organization Default Rate Components", "Append-Only
/// Effective-Dated Rate Component History", "Organization-Scoped Component
/// Persistence With RLS").
///
/// `RateComponentTests` covers the pure domain rules; nothing here re-tests
/// construction validation. What is proven here is what only a real database
/// can prove: that the calculation base, order and percentage survive a
/// round trip, that inheritance resolves in SQL, that history is append-only,
/// and that a second organization sees none of it.
///
/// If Postgres is not reachable, these tests report the gap and return
/// without asserting, matching the existing fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class RateComponentStoreTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public RateComponentStoreTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        PostgresTestFixture.ApplyPricingMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();


    private static void SeedOrganization(Guid organizationId, string name = "Test Org")
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, $2)", owner);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(name);
        cmd.ExecuteNonQuery();
    }

    private async Task<Guid> SeedPriceListAsync(CloudTenantScope scope, string name, Guid actorId, bool isDefault)
    {
        var store = new PostgresPriceListStore(_dataSource!);
        var list = await store.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), name, isDefault, actorId), "org-user", actorId, CancellationToken.None);
        return list.Id;
    }

    private static RateComponent Component(string code, decimal percentage, RateCalculationBase calculationBase, int order) =>
        new(code, $"{code} ({percentage}%)", percentage, calculationBase, order);

    /// <summary>Vaca Verde's real delivery sheet: four rates, all on the base price.</summary>
    private static IReadOnlyList<RateComponent> VacaVerdeComponents() =>
    [
        Component("IVA", 10.5m, RateCalculationBase.Base, 1),
        Component("IB", 2.5m, RateCalculationBase.Base, 2),
        Component("FLETE", 7m, RateCalculationBase.Base, 3),
        Component("REMARCACION", 25m, RateCalculationBase.Base, 4),
    ];

    // --- Round trip --------------------------------------------------------

    /// <summary>
    /// Spec "Price List Rate Components": code, label, percentage,
    /// calculation base and order all persist and read back, in the declared
    /// order.
    /// </summary>
    [Fact]
    public async Task PublishSetAsync_ThenGetEffectiveSetAsync_RoundTripsEveryComponentField()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Reparto", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(Guid.NewGuid(), priceListId, new DateOnly(2026, 1, 1), VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None);

        var effective = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 3, 1), CancellationToken.None);

        Assert.NotNull(effective);
        Assert.Equal(priceListId, effective!.PriceListId);
        Assert.Equal(organizationId, effective.OrganizationId);
        Assert.Equal(new DateOnly(2026, 1, 1), effective.EffectiveFrom);
        Assert.Equal(["IVA", "IB", "FLETE", "REMARCACION"], effective.Components.Select(c => c.Code));
        Assert.Equal([1, 2, 3, 4], effective.Components.Select(c => c.Order));
        Assert.Equal([10.5m, 2.5m, 7m, 25m], effective.Components.Select(c => c.Percentage));
        Assert.All(effective.Components, c => Assert.Equal(RateCalculationBase.Base, c.CalculationBase));

        // Compared against the labels actually submitted, not a literal: the
        // helper renders the percentage with the ambient culture, and an
        // es-AR runner writes "IVA (10,5%)". Hard-coding the invariant
        // spelling would assert the test host's locale, not the round trip.
        Assert.Equal(VacaVerdeComponents().Select(c => c.Label), effective.Components.Select(c => c.Label));
    }

    /// <summary>
    /// The calculation base is not merely a stored string: the round-tripped
    /// set reproduces Vaca Verde's verified arithmetic. On `Subtotal` the same
    /// four percentages give a strictly greater number, so this asserts the
    /// persisted base is really `Base`.
    /// </summary>
    [Fact]
    public async Task PublishSetAsync_RoundTrippedSet_ComposesVacaVerdesVerifiedRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Reparto", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(Guid.NewGuid(), priceListId, new DateOnly(2026, 1, 1), VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None);

        var effective = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 1, 1), CancellationToken.None);

        Assert.NotNull(effective);
        Assert.Equal(15_370m, effective!.Compose(10_600m));
    }

    /// <summary>
    /// R3-publish-returns-unpersisted-precision. `PublishSetAsync` returns a
    /// set rebuilt from the caller's IN-MEMORY components, never from the rows
    /// Postgres actually wrote. That is only honest while the two are the same
    /// number, and `numeric(9,4)` silently rounds anything finer.
    ///
    /// This is the durable guard for the domain-edge rejection in
    /// <see cref="RateComponent"/>: it pins the FINEST percentage the column
    /// stores exactly and asserts the returned set and every later read compose
    /// bit-for-bit the same price. If the column's scale is ever narrowed
    /// without narrowing the edge, this fails instead of quietly diverging.
    /// </summary>
    [Fact]
    public async Task PublishSetAsync_ReturnedSet_ComposesExactlyWhatEveryLaterReadComposes()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Reparto", actorId, isDefault: true);

        // 10.5005 % — four decimal places, the exact edge of numeric(9,4).
        var finest = new List<RateComponent> { Component("IVA", 10.5005m, RateCalculationBase.Base, 1) };

        var store = new PostgresRateComponentStore(_dataSource!);
        var returned = await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(Guid.NewGuid(), priceListId, new DateOnly(2026, 1, 1), finest, actorId),
            "org-user", actorId, CancellationToken.None);

        var reread = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 1, 1), CancellationToken.None);

        Assert.NotNull(reread);
        Assert.Equal(10.5005m, Assert.Single(reread!.Components).Percentage);
        Assert.Equal(returned.Compose(10_600m), reread.Compose(10_600m));
        Assert.Equal(11_713.053m, reread.Compose(10_600m));
    }

    /// <summary>
    /// A `Subtotal` component round-trips as `Subtotal` and chains: 100 -> 110
    /// -> 121, not 120. Without this, `PublishSetAsync` could write a constant
    /// 'Base' and every other test here would still pass.
    /// </summary>
    [Fact]
    public async Task PublishSetAsync_SubtotalComponent_RoundTripsAndChains()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Compounding", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(
                Guid.NewGuid(), priceListId, new DateOnly(2026, 1, 1),
                [
                    Component("FIRST", 10m, RateCalculationBase.Subtotal, 1),
                    Component("SECOND", 10m, RateCalculationBase.Subtotal, 2),
                ],
                actorId),
            "org-user", actorId, CancellationToken.None);

        var effective = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 1, 1), CancellationToken.None);

        Assert.NotNull(effective);
        Assert.All(effective!.Components, c => Assert.Equal(RateCalculationBase.Subtotal, c.CalculationBase));
        Assert.Equal(121m, effective.Compose(100m));
    }

    // --- Append-only effective dating --------------------------------------

    /// <summary>
    /// Spec "Append-Only Effective-Dated Rate Component History": publishing a
    /// new set leaves the prior one readable, and resolution picks the latest
    /// set at or before the requested date.
    /// </summary>
    [Fact]
    public async Task PublishSetAsync_SecondDatedSet_LeavesHistoryIntactAndResolvesLatest()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Reparto", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(
                Guid.NewGuid(), priceListId, new DateOnly(2026, 1, 1),
                [Component("IVA", 10.5m, RateCalculationBase.Base, 1)], actorId),
            "org-user", actorId, CancellationToken.None);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(
                Guid.NewGuid(), priceListId, new DateOnly(2026, 6, 1),
                [Component("IVA", 21m, RateCalculationBase.Base, 1)], actorId),
            "org-user", actorId, CancellationToken.None);

        var beforeChange = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 5, 31), CancellationToken.None);
        var afterChange = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 7, 1), CancellationToken.None);
        var history = await store.ListHistoryAsync(scope, priceListId, CancellationToken.None);

        Assert.Equal(10.5m, beforeChange!.Components.Single().Percentage);
        Assert.Equal(21m, afterChange!.Components.Single().Percentage);

        // Both sets survive: a rate change is an INSERT, never an overwrite.
        Assert.Equal(2, history.Count);
        Assert.Equal([new DateOnly(2026, 6, 1), new DateOnly(2026, 1, 1)], history.Select(s => s.EffectiveFrom));
    }

    /// <summary>
    /// Before the first set's `EffectiveFrom` there is no effective set at
    /// all — exactly zero rows, not the earliest set and not an error
    /// (`price_list_entries`' "no gap between ranges" shape).
    /// </summary>
    [Fact]
    public async Task GetEffectiveSetAsync_BeforeTheFirstSetsDate_ReturnsNull()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Reparto", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(
                Guid.NewGuid(), priceListId, new DateOnly(2026, 6, 1),
                [Component("IVA", 21m, RateCalculationBase.Base, 1)], actorId),
            "org-user", actorId, CancellationToken.None);

        var effective = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 5, 31), CancellationToken.None);

        Assert.Null(effective);
    }

    /// <summary>
    /// A same-day double-publish for one list is a `23505`
    /// (`rate_component_sets_list_day_uk`) for the endpoint to translate into
    /// a 409 — never a silent coin flip over which set is "the" one for that
    /// date.
    /// </summary>
    [Fact]
    public async Task PublishSetAsync_TwiceOnTheSameDayForOneList_Throws23505()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Reparto", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        var set = new NewRateComponentSet(
            Guid.NewGuid(), priceListId, new DateOnly(2026, 1, 1),
            [Component("IVA", 10.5m, RateCalculationBase.Base, 1)], actorId);
        await store.PublishSetAsync(scope, set, "org-user", actorId, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => store.PublishSetAsync(
            scope,
            set with { Id = Guid.NewGuid() },
            "org-user", actorId, CancellationToken.None));
        Assert.Equal("23505", ex.SqlState);
    }

    /// <summary>
    /// The same NULL-aware guard for the organization default set: two NULLs
    /// never compare equal in SQL, so without the partial index on
    /// `(organization_id, effective_from) WHERE price_list_id IS NULL` an
    /// organization could publish unlimited same-day defaults.
    /// </summary>
    [Fact]
    public async Task PublishSetAsync_TwoSameDayOrganizationDefaults_Throws23505()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();

        var store = new PostgresRateComponentStore(_dataSource!);
        var set = new NewRateComponentSet(
            Guid.NewGuid(), null, new DateOnly(2026, 1, 1),
            [Component("IVA", 10.5m, RateCalculationBase.Base, 1)], actorId);
        await store.PublishSetAsync(scope, set, "org-user", actorId, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => store.PublishSetAsync(
            scope,
            set with { Id = Guid.NewGuid() },
            "org-user", actorId, CancellationToken.None));
        Assert.Equal("23505", ex.SqlState);
    }

    // --- Organization default inheritance ----------------------------------

    /// <summary>
    /// Spec "Organization Default Rate Components": a list declaring no set of
    /// its own uses the organization's effective default.
    /// </summary>
    [Fact]
    public async Task GetEffectiveSetAsync_ListWithoutItsOwnSet_InheritsTheOrganizationDefault()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Mostrador", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(Guid.NewGuid(), null, new DateOnly(2026, 1, 1), VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None);

        var effective = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 3, 1), CancellationToken.None);

        Assert.NotNull(effective);
        Assert.True(effective!.IsOrganizationDefault);
        Assert.Equal(["IVA", "IB", "FLETE", "REMARCACION"], effective.Components.Select(c => c.Code));
    }

    /// <summary>
    /// Spec: a list's own set is used IN FULL and MUST NOT be merged with the
    /// organization's defaults. The org default carries `FLETE`; the list's
    /// own set does not, and `FLETE` must not reappear.
    /// </summary>
    [Fact]
    public async Task GetEffectiveSetAsync_ListWithItsOwnSet_FullyOverridesTheOrganizationDefault()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Mostrador", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(Guid.NewGuid(), null, new DateOnly(2026, 1, 1), VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(
                Guid.NewGuid(), priceListId, new DateOnly(2026, 1, 1),
                [Component("IVA", 10.5m, RateCalculationBase.Base, 1)], actorId),
            "org-user", actorId, CancellationToken.None);

        var effective = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 3, 1), CancellationToken.None);

        Assert.NotNull(effective);
        Assert.Equal(priceListId, effective!.PriceListId);
        Assert.Equal(["IVA"], effective.Components.Select(c => c.Code));
        Assert.DoesNotContain(effective.Components, c => c.Code == "FLETE");
    }

    /// <summary>
    /// Spec "Rate Components Are Scoped To The List, Not The Organization":
    /// the delivery list's `FLETE` does not leak onto the counter list, which
    /// keeps its own set.
    /// </summary>
    [Fact]
    public async Task GetEffectiveSetAsync_TwoListsInOneOrganization_KeepSeparateSets()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var deliveryListId = await SeedPriceListAsync(scope, "Reparto", actorId, isDefault: true);
        var counterListId = await SeedPriceListAsync(scope, "Mostrador", actorId, isDefault: false);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(
                Guid.NewGuid(), deliveryListId, new DateOnly(2026, 1, 1),
                [
                    Component("IVA", 10.5m, RateCalculationBase.Base, 1),
                    Component("FLETE", 7m, RateCalculationBase.Base, 2),
                ], actorId),
            "org-user", actorId, CancellationToken.None);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(
                Guid.NewGuid(), counterListId, new DateOnly(2026, 1, 1),
                [Component("IVA", 10.5m, RateCalculationBase.Base, 1)], actorId),
            "org-user", actorId, CancellationToken.None);

        var delivery = await store.GetEffectiveSetAsync(scope, deliveryListId, new DateOnly(2026, 3, 1), CancellationToken.None);
        var counter = await store.GetEffectiveSetAsync(scope, counterListId, new DateOnly(2026, 3, 1), CancellationToken.None);

        Assert.Equal(["IVA", "FLETE"], delivery!.Components.Select(c => c.Code));
        Assert.Equal(["IVA"], counter!.Components.Select(c => c.Code));
    }

    /// <summary>
    /// Spec "No set anywhere is an empty composition, not an error". `null`
    /// is the store's way of saying "exactly zero sets"; the caller composes
    /// it as the identity, leaving the base price unchanged.
    /// </summary>
    [Fact]
    public async Task GetEffectiveSetAsync_NoSetOnTheListOrTheOrganization_ReturnsNullWithoutError()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Mostrador", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        var effective = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 3, 1), CancellationToken.None);

        Assert.Null(effective);
    }

    /// <summary>
    /// Spec "A list may carry no components": an EMPTY set is a declaration,
    /// not an absence. It overrides the organization default (the list has
    /// declared its own set) and composes to the base price unchanged — the
    /// property that makes reinterpreting `UnitPrice` as a base price a
    /// zero-data-rewrite change.
    /// </summary>
    [Fact]
    public async Task PublishSetAsync_EmptySet_OverridesTheOrganizationDefaultAndComposesToTheBasePrice()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceListId = await SeedPriceListAsync(scope, "Sin recargos", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(Guid.NewGuid(), null, new DateOnly(2026, 1, 1), VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None);
        await store.PublishSetAsync(
            scope,
            new NewRateComponentSet(Guid.NewGuid(), priceListId, new DateOnly(2026, 1, 1), [], actorId),
            "org-user", actorId, CancellationToken.None);

        var effective = await store.GetEffectiveSetAsync(scope, priceListId, new DateOnly(2026, 3, 1), CancellationToken.None);

        Assert.NotNull(effective);
        Assert.Equal(priceListId, effective!.PriceListId);
        Assert.Empty(effective.Components);
        Assert.Equal(15_370m, effective.Compose(15_370m));
    }

    // --- Cross-tenant references (0014) -------------------------------------

    /// <summary>
    /// R1-latent-cross-tenant-price-list-id. RLS hides another organization's
    /// `price_lists` row from a SELECT, but a foreign-key CHECK is not a
    /// SELECT: it runs as the referential-integrity trigger, outside the
    /// policy. Before `0014`, Organization A could therefore publish a rate
    /// component set naming Organization B's `price_list_id` — a tenant
    /// boundary crossed by a plain INSERT — and, because
    /// `rate_component_sets_list_day_uk` keyed only `(price_list_id,
    /// effective_from)`, that row also PRE-EMPTED B's own publication for the
    /// same day, denying B a write it is entitled to.
    ///
    /// `0014` makes the reference composite —
    /// `(organization_id, price_list_id) -> price_lists (organization_id, id)`
    /// — so the database itself refuses the cross-tenant row. MATCH SIMPLE
    /// leaves the organization-default case (`price_list_id IS NULL`)
    /// unchecked, which is exactly right: it references no list.
    ///
    /// MUTATION-CHECKED: with the composite constraint dropped this publish
    /// succeeds and the assertion fails.
    /// </summary>
    [Fact]
    public async Task PublishSetAsync_NamingAnotherOrganizationsPriceList_IsRefusedByTheDatabase()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        SeedOrganization(orgAId, "Org A");
        SeedOrganization(orgBId, "Org B");
        var scopeA = new CloudTenantScope(orgAId);
        var scopeB = new CloudTenantScope(orgBId);
        var actorId = Guid.NewGuid();

        var listInB = await SeedPriceListAsync(scopeB, "Reparto de B", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);

        var error = await Assert.ThrowsAsync<PostgresException>(() => store.PublishSetAsync(
            scopeA,
            new NewRateComponentSet(Guid.NewGuid(), listInB, new DateOnly(2026, 1, 1), VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None));

        // 23503 = foreign_key_violation. Not 23505: the point is that the row
        // is refused as a reference, not merely as a duplicate.
        Assert.Equal("23503", error.SqlState);

        // And B keeps the day it was nearly pre-empted out of.
        await store.PublishSetAsync(
            scopeB,
            new NewRateComponentSet(Guid.NewGuid(), listInB, new DateOnly(2026, 1, 1), VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None);
        Assert.NotNull(await store.GetEffectiveSetAsync(scopeB, listInB, new DateOnly(2026, 1, 1), CancellationToken.None));
    }

    /// <summary>
    /// The same class of gap one level down: `rate_components.set_id`
    /// referenced `rate_component_sets (id)` alone, so a component row scoped
    /// to Organization A could be attached to Organization B's set. `0014`
    /// makes that reference composite too.
    /// </summary>
    [Fact]
    public async Task RateComponentRow_AttachedToAnotherOrganizationsSet_IsRefusedByTheDatabase()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        SeedOrganization(orgAId, "Org A");
        SeedOrganization(orgBId, "Org B");
        var actorId = Guid.NewGuid();

        var setInB = Guid.NewGuid();
        var listInB = await SeedPriceListAsync(new CloudTenantScope(orgBId), "Reparto de B", actorId, isDefault: true);
        await new PostgresRateComponentStore(_dataSource!).PublishSetAsync(
            new CloudTenantScope(orgBId),
            new NewRateComponentSet(setInB, listInB, new DateOnly(2026, 1, 1), VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None);

        // Written as the OWNER, deliberately: RLS is not the control under
        // test here, the referential constraint is. `app_runtime` would be
        // stopped by the policy first and prove nothing about the FK.
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            """
            INSERT INTO rate_components
                (id, organization_id, set_id, code, label, percentage, calculation_base, component_order)
            VALUES ($1, $2, $3, 'SMUGGLED', 'Smuggled', 99, 'Base', 99)
            """, owner);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(orgAId);
        cmd.Parameters.AddWithValue(setInB);

        var error = Assert.Throws<PostgresException>(() => cmd.ExecuteNonQuery());
        Assert.Equal("23503", error.SqlState);
    }

    // --- Code identity (0014) -----------------------------------------------

    /// <summary>
    /// R3-case-insensitive-dup-read-failure. `RateComponentSet` rejects
    /// duplicate codes with `OrdinalIgnoreCase`, but `0013`'s
    /// `UNIQUE (set_id, code)` is case-SENSITIVE. The two disagreeing is worse
    /// than either rule alone: a `IVA`/`iva` pair written by any path that is
    /// not the domain constructor persists happily, and from then on EVERY
    /// read of that set throws while rebuilding it — the components become
    /// permanently unreadable, and the set cannot be deleted either, because
    /// the tables are append-only by grant.
    ///
    /// `0014` aligns the database with the domain: unique on
    /// `(set_id, lower(code))`.
    /// </summary>
    [Fact]
    public void RateComponents_TwoCodesDifferingOnlyInCase_AreRefusedByTheDatabase()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var setId = Guid.NewGuid();

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        using (var setCmd = new NpgsqlCommand(
            """
            INSERT INTO rate_component_sets (id, organization_id, price_list_id, effective_from, created_by_user_id)
            VALUES ($1, $2, NULL, DATE '2026-01-01', $3)
            """, owner))
        {
            setCmd.Parameters.AddWithValue(setId);
            setCmd.Parameters.AddWithValue(organizationId);
            setCmd.Parameters.AddWithValue(Guid.NewGuid());
            setCmd.ExecuteNonQuery();
        }

        void InsertComponent(string code, int order)
        {
            using var cmd = new NpgsqlCommand(
                """
                INSERT INTO rate_components
                    (id, organization_id, set_id, code, label, percentage, calculation_base, component_order)
                VALUES ($1, $2, $3, $4, $4, 10.5, 'Base', $5)
                """, owner);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(organizationId);
            cmd.Parameters.AddWithValue(setId);
            cmd.Parameters.AddWithValue(code);
            cmd.Parameters.AddWithValue(order);
            cmd.ExecuteNonQuery();
        }

        InsertComponent("IVA", 1);

        var error = Assert.Throws<PostgresException>(() => InsertComponent("iva", 2));
        Assert.Equal("23505", error.SqlState);
    }

    // --- RLS through the store ---------------------------------------------

    /// <summary>
    /// Spec "Organization-Scoped Component Persistence With RLS", proven
    /// through the real store rather than raw SQL: Organization B reads none
    /// of Organization A's sets, neither the list-owned one nor the
    /// inheritable default.
    ///
    /// MUTATION-CHECKED alongside
    /// `MigrationRlsTests.RateComponents_CrossOrganizationRead_ReturnsZeroRows`:
    /// with both `0013` policies relaxed to `USING (true)` this assertion
    /// fails, so the nulls below are isolation and not an empty database. The
    /// org-A readback in the same test is what rules out vacuous seeding.
    /// </summary>
    [Fact]
    public async Task GetEffectiveSetAsync_FromAnotherOrganization_SeesNeitherTheListSetNorTheDefault()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        SeedOrganization(orgAId, "Org A");
        SeedOrganization(orgBId, "Org B");
        var scopeA = new CloudTenantScope(orgAId);
        var scopeB = new CloudTenantScope(orgBId);
        var actorId = Guid.NewGuid();

        var priceListId = await SeedPriceListAsync(scopeA, "Reparto", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scopeA,
            new NewRateComponentSet(Guid.NewGuid(), priceListId, new DateOnly(2026, 1, 1), VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None);
        await store.PublishSetAsync(
            scopeA,
            new NewRateComponentSet(
                Guid.NewGuid(), null, new DateOnly(2026, 1, 1),
                [Component("IVA", 10.5m, RateCalculationBase.Base, 1)], actorId),
            "org-user", actorId, CancellationToken.None);

        // The control: scoped to Org A the very same reads DO return the data,
        // so the nulls below cannot come from a failed seed.
        var visibleToA = await store.GetEffectiveSetAsync(scopeA, priceListId, new DateOnly(2026, 3, 1), CancellationToken.None);
        var historyForA = await store.ListHistoryAsync(scopeA, priceListId, CancellationToken.None);
        Assert.NotNull(visibleToA);
        Assert.Single(historyForA);

        var visibleToB = await store.GetEffectiveSetAsync(scopeB, priceListId, new DateOnly(2026, 3, 1), CancellationToken.None);
        var historyForB = await store.ListHistoryAsync(scopeB, priceListId, CancellationToken.None);
        var defaultsForB = await store.ListHistoryAsync(scopeB, null, CancellationToken.None);

        Assert.Null(visibleToB);
        Assert.Empty(historyForB);
        Assert.Empty(defaultsForB);
    }

    /// <summary>
    /// Org B's own default set must not be handed to Org A's list through the
    /// inheritance fallback — the one path where a missing `organization_id`
    /// filter would silently cross tenants while every direct read still
    /// looked correct.
    /// </summary>
    [Fact]
    public async Task GetEffectiveSetAsync_DoesNotInheritAnotherOrganizationsDefaultSet()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        SeedOrganization(orgAId, "Org A");
        SeedOrganization(orgBId, "Org B");
        var scopeA = new CloudTenantScope(orgAId);
        var scopeB = new CloudTenantScope(orgBId);
        var actorId = Guid.NewGuid();

        var listInA = await SeedPriceListAsync(scopeA, "Reparto", actorId, isDefault: true);

        var store = new PostgresRateComponentStore(_dataSource!);
        await store.PublishSetAsync(
            scopeB,
            new NewRateComponentSet(Guid.NewGuid(), null, new DateOnly(2026, 1, 1), VacaVerdeComponents(), actorId),
            "org-user", actorId, CancellationToken.None);

        var effective = await store.GetEffectiveSetAsync(scopeA, listInA, new DateOnly(2026, 3, 1), CancellationToken.None);

        Assert.Null(effective);
    }
}
