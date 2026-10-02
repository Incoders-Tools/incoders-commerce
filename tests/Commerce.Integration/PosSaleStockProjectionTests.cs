using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// branch -> cloud stock sync (purchases-receptions-and-stock T4): when a `sale` envelope is projected, every sale line
/// also lands as ONE negative `Sale` stock movement (source PosSale, line key derived from sale id + line number,
/// occurred_at = the sale time) in the same inbox transaction. Redelivery decrements once; a presentation the branch
/// does not know is skipped with a warning and never fails the envelope. Real Postgres, as app_runtime.
/// </summary>
[Collection("Postgres")]
public sealed class PosSaleStockProjectionTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PosSaleStockProjectionTests()
    {
        if (!_postgresAvailable) return;
        using (var owner = OpenOwner())
        {
            var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
            {
                PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
            }
        }
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<PostgresCloudInboxStore>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private async Task<(Guid Org, Guid Branch)> SeedAsync()
    {
        var userStore = new PostgresUserAccountStore(_dataSource!);
        var orgStore = new PostgresOrganizationStore(_dataSource!, userStore);
        var (orgId, branchId) = (Guid.NewGuid(), Guid.NewGuid());
        var outcome = await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(orgId), new NewOrganization(orgId, "Stock Sales Org"), new NewBranch(branchId, "Main"),
            new NewUserAccount(Guid.NewGuid(), $"stock-sales-{orgId}@example.com", "hash", [branchId], [new RoleDto("cashier", Permission.OperatePos)]),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, outcome);
        return (orgId, branchId);
    }

    private static SaleLine Line(Guid sale, int number, Guid presentation, decimal quantity) =>
        new(sale, number, presentation, null, "Media res", "Kg", quantity, 5000m, quantity * 5000m);

    private static SyncEnvelope Envelope(
        Guid org, Guid branch, Guid sale, SaleLine[] lines, DateTimeOffset? at = null, Guid? operationId = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        var payload = new SalePayloadV1(sale, lines.Sum(l => l.LineTotal), "Manual", when, lines);
        return new SyncEnvelope(
            operationId ?? Guid.NewGuid(), 1, org, branch, sale, 1, Guid.NewGuid(), Guid.NewGuid(), when, "sale",
            SyncPayloadCodec.Serialize(payload));
    }

    private static (decimal OnHand, long Movements) Stock(Guid presentation)
    {
        using var owner = OpenOwner();
        return (
            Scalar<decimal>(owner, "SELECT COALESCE(SUM(quantity), 0) FROM stock_movements WHERE presentation_id = $1", presentation),
            Scalar<long>(owner, "SELECT count(*) FROM stock_movements WHERE presentation_id = $1", presentation));
    }

    private static void Receive(Guid org, Guid branch, Guid presentation, decimal quantity)
    {
        using var owner = OpenOwner();
        Exec(owner,
            """
            INSERT INTO stock_movements (id, organization_id, branch_id, presentation_id, quantity, kind, occurred_at_utc)
            VALUES ($1, $2, $3, $4, $5, 'Opening', now())
            """, Guid.NewGuid(), org, branch, presentation, quantity);
    }

    [Fact]
    public async Task AWeightedSaleLine_LowersStockByTheExactKilos_AndARedeliveredEnvelopeDecrementsOnce()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch) = await SeedAsync();
        var presentation = Presentation(org, branch);
        Receive(org, branch, presentation, 120m);
        var sale = Guid.NewGuid();
        var envelope = Envelope(org, branch, sale, [Line(sale, 1, presentation, 2.5m)]);
        var store = new PostgresCloudInboxStore(_dataSource!);

        Assert.Equal(InboundApplyOutcome.Applied, store.TryApplyInbound(new CloudTenantScope(org), envelope).Outcome);
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, store.TryApplyInbound(new CloudTenantScope(org), envelope).Outcome);

        Assert.Equal((117.5m, 2L), Stock(presentation));
        using var owner = OpenOwner();
        Assert.Equal(-2.5m, Scalar<decimal>(owner, "SELECT quantity FROM stock_movements WHERE kind = 'Sale' AND presentation_id = $1", presentation));
        Assert.Equal("PosSale", Scalar<string>(owner, "SELECT source_type FROM stock_movements WHERE kind = 'Sale' AND presentation_id = $1", presentation));
        Assert.Equal(sale, Scalar<Guid>(owner, "SELECT source_id FROM stock_movements WHERE kind = 'Sale' AND presentation_id = $1", presentation));
    }

    [Fact]
    public async Task TheSameSaleUnderAnotherOperationId_StillDecrementsOnce_BecauseTheLineKeyIsDeterministic()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch) = await SeedAsync();
        var presentation = Presentation(org, branch);
        var sale = Guid.NewGuid();
        var lines = new[] { Line(sale, 1, presentation, 4m) };
        var store = new PostgresCloudInboxStore(_dataSource!);

        Assert.Equal(InboundApplyOutcome.Applied, store.TryApplyInbound(new CloudTenantScope(org), Envelope(org, branch, sale, lines)).Outcome);
        Assert.Equal(InboundApplyOutcome.Applied, store.TryApplyInbound(new CloudTenantScope(org), Envelope(org, branch, sale, lines)).Outcome);

        Assert.Equal((-4m, 1L), Stock(presentation));
    }

    [Fact]
    public async Task AMultiLineSale_WritesOneMovementPerLine_WithTheSaleTime()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch) = await SeedAsync();
        var meat = Presentation(org, branch, "Media res", "Kg");
        var sausage = Presentation(org, branch, "Chorizo", "Unidad", "FixedQuantity");
        var sale = Guid.NewGuid();
        var soldAt = new DateTimeOffset(2026, 9, 30, 14, 5, 0, TimeSpan.Zero);
        var envelope = Envelope(org, branch, sale, [Line(sale, 1, meat, 1.25m), Line(sale, 2, sausage, 6m), Line(sale, 3, meat, 0.75m)], soldAt);

        new PostgresCloudInboxStore(_dataSource!).TryApplyInbound(new CloudTenantScope(org), envelope);

        Assert.Equal((-2m, 2L), Stock(meat));
        Assert.Equal((-6m, 1L), Stock(sausage));
        using var owner = OpenOwner();
        Assert.Equal(soldAt.UtcDateTime, Scalar<DateTime>(owner, "SELECT max(occurred_at_utc) FROM stock_movements WHERE presentation_id = $1", sausage));
    }

    [Fact]
    public async Task APresentationUnknownToTheBranch_IsSkippedWithAWarning_AndNeverFailsTheEnvelope()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch) = await SeedAsync();
        var known = Presentation(org, branch);
        var unknown = Guid.NewGuid();
        var sale = Guid.NewGuid();
        var logger = new CapturingLogger();
        var envelope = Envelope(org, branch, sale, [Line(sale, 1, unknown, 3m), Line(sale, 2, known, 2m)]);

        var result = new PostgresCloudInboxStore(_dataSource!, logger).TryApplyInbound(new CloudTenantScope(org), envelope);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.Equal((-2m, 1L), Stock(known));
        Assert.Equal((0m, 0L), Stock(unknown));
        Assert.Contains(logger.Warnings, w => w.Contains(unknown.ToString()) && w.Contains(sale.ToString()));
        using var owner = OpenOwner();
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM pos_sales WHERE sale_id = $1", sale)); // the sale itself is ingested
    }

    [Fact]
    public async Task AZeroOrNegativeQuantityLine_IsSkippedWithAWarning()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch) = await SeedAsync();
        var presentation = Presentation(org, branch);
        var sale = Guid.NewGuid();
        var logger = new CapturingLogger();
        var envelope = Envelope(org, branch, sale, [Line(sale, 1, presentation, 0m), Line(sale, 2, presentation, -1m)]);

        var result = new PostgresCloudInboxStore(_dataSource!, logger).TryApplyInbound(new CloudTenantScope(org), envelope);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.Equal((0m, 0L), Stock(presentation));
        Assert.Equal(2, logger.Warnings.Count(w => w.Contains("not a positive quantity")));
    }
}
