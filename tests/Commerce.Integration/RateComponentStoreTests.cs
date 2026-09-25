using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Pricing;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Live-Postgres coverage for commerce-price-composition slice 1's
/// persistence half: `PostgresRateComponentStore` against the real `0013`
/// schema (`specs/price-list-management/spec.md` requirements "Price List
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

        ApplyMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var repoRoot = RepoRoot();

        void Apply(string file, string? placeholder = null, string? replacement = null)
        {
            var sql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", file));
            if (placeholder is not null)
            {
                sql = sql.Replace(placeholder, replacement);
            }
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        Apply("0001_init_rls.sql", "__APP_RUNTIME_PASSWORD__", "dev-only-password");
        Apply("0002_users.sql");
        Apply("0003_organizations_branches.sql");
        Apply("0009_catalog_and_pricing.sql");
        Apply("0013_rate_components.sql");

        using var resetCmd = new NpgsqlCommand(
            """
            TRUNCATE TABLE rate_components, rate_component_sets, price_list_entries, price_lists,
                           presentations, products, branches, organizations CASCADE
            """, owner);
        resetCmd.ExecuteNonQuery();
    }

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
