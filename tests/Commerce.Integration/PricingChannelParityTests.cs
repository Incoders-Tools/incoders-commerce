using Commerce.Application.Pricing;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Pricing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine task 3.8 (RED) / 3.9 (GREEN): the
/// ADR-010 channel-parity requirement, proven for the two sources this Unit
/// actually ships — <see cref="PostgresEffectivePriceSource"/> (cloud) and a
/// minimal SQLite-backed <see cref="IEffectivePriceSource"/> stub standing
/// in for the POS's future replica-backed source (the full
/// <c>LocalEffectivePriceSource</c> over `BranchSyncStore` ships in Phase 7
/// — this test only needs a second, independently-implemented source
/// reading the SAME tuple to prove the resolution service treats both
/// identically). If Postgres is not reachable, this test reports the gap
/// clearly and returns without asserting pass/fail, matching the existing
/// fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class PricingChannelParityTests : IDisposable
{
    /// <summary>
    /// Test-only stand-in for the POS's SQLite-backed price source. Reads a
    /// single `price_replica`-shaped table by (presentation, effective date)
    /// using the SAME "latest effective_from on or before the date" rule as
    /// the Postgres source, over a temp on-disk SQLite database.
    /// </summary>
    private sealed class SqliteEffectivePriceSourceStub : IEffectivePriceSource, IDisposable
    {
        private readonly SqliteConnection _connection;

        public SqliteEffectivePriceSourceStub()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var create = _connection.CreateCommand();
            create.CommandText =
                "CREATE TABLE price_replica (presentation_id TEXT NOT NULL, unit_price TEXT NOT NULL, effective_from TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        public void Seed(Guid presentationId, decimal unitPrice, DateOnly effectiveFrom)
        {
            using var insert = _connection.CreateCommand();
            insert.CommandText = "INSERT INTO price_replica (presentation_id, unit_price, effective_from) VALUES ($p, $u, $e)";
            insert.Parameters.AddWithValue("$p", presentationId.ToString());
            insert.Parameters.AddWithValue("$u", unitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$e", effectiveFrom.ToString("yyyy-MM-dd"));
            insert.ExecuteNonQuery();
        }

        public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct)
        {
            using var select = _connection.CreateCommand();
            select.CommandText =
                """
                SELECT unit_price FROM price_replica
                WHERE presentation_id = $p AND effective_from <= $e
                ORDER BY effective_from DESC
                LIMIT 1
                """;
            select.Parameters.AddWithValue("$p", presentationId.ToString());
            select.Parameters.AddWithValue("$e", effectiveOn.ToString("yyyy-MM-dd"));

            var raw = select.ExecuteScalar();
            return Task.FromResult(raw is null
                ? (decimal?)null
                : decimal.Parse((string)raw, System.Globalization.CultureInfo.InvariantCulture));
        }

        public void Dispose() => _connection.Dispose();
    }

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PricingChannelParityTests()
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

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE price_list_entries, price_lists, presentations, products, branches, organizations CASCADE", owner);
        resetCmd.ExecuteNonQuery();
    }

    private static void SeedOrganization(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Test Org')", owner);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Spec scenario "Same tuple resolves identically across channels": the
    /// SAME `(presentation, quantity, discount, date)` tuple resolved
    /// through a Postgres-backed source and an independently-implemented
    /// SQLite-backed source produces a byte-identical <c>Resolved</c> value
    /// — there is no channel parameter for either call site to differ on.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_PostgresAndSqliteSources_SameTuple_ByteIdenticalResult()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();

        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var product = await catalogStore.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Product", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);
        var presentation = await catalogStore.CreatePresentationAsync(
            scope, new NewPresentation(Guid.NewGuid(), product.Id, "Presentation", QuantityBehavior.FixedQuantity, Guid.NewGuid(), null, actorId),
            "org-user", actorId, CancellationToken.None);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Default", true, actorId), "org-user", actorId, CancellationToken.None);

        var effectiveFrom = new DateOnly(2026, 1, 1);
        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentation.Id, 249.99m, effectiveFrom, "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);

        var postgresSource = new PostgresEffectivePriceSource(priceStore, scope, priceList.Id);
        using var sqliteSource = new SqliteEffectivePriceSourceStub();
        sqliteSource.Seed(presentation.Id, 249.99m, effectiveFrom);

        var postgresService = new PricingResolutionService(postgresSource);
        var sqliteService = new PricingResolutionService(sqliteSource);

        var resolveOn = new DateOnly(2026, 3, 1);
        var postgresOutcome = await postgresService.ResolveAsync(presentation.Id, 4m, 10m, resolveOn, CancellationToken.None);
        var sqliteOutcome = await sqliteService.ResolveAsync(presentation.Id, 4m, 10m, resolveOn, CancellationToken.None);

        var postgresResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(postgresOutcome);
        var sqliteResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(sqliteOutcome);

        Assert.Equal(postgresResolved, sqliteResolved);
        Assert.Equal(224.99m, postgresResolved.UnitNetPrice);
        Assert.Equal(899.96m, postgresResolved.LineTotal);
    }
}
