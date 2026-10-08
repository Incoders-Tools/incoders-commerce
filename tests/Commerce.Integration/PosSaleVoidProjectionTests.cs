using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Discounts;
using Commerce.Domain.Identity;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// branch -> cloud sale voids (0044): a <c>sale.voided</c> envelope is recorded in <c>pos_sale_voids</c>, audited, and
/// the stock its sale took out comes back as <c>Reversal</c> movements (source PosSaleVoid). Redelivery changes nothing,
/// and a void ingested before its sale leaves that sale without stock movements. Real Postgres, as app_runtime.
/// </summary>
[Collection("Postgres")]
public sealed class PosSaleVoidProjectionTests : IDisposable
{
    private static readonly DiscountAuthorization Pin = new(DiscountAuthorization.BranchPin, Guid.NewGuid(), 7);

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PosSaleVoidProjectionTests()
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

    private async Task<(Guid Org, Guid Branch)> SeedAsync()
    {
        var userStore = new PostgresUserAccountStore(_dataSource!);
        var orgStore = new PostgresOrganizationStore(_dataSource!, userStore);
        var (orgId, branchId) = (Guid.NewGuid(), Guid.NewGuid());
        var outcome = await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(orgId), new NewOrganization(orgId, "Void Org"), new NewBranch(branchId, "Main"),
            new NewUserAccount(Guid.NewGuid(), $"void-{orgId}@example.com", "hash", [branchId], [new RoleDto("cashier", Permission.OperatePos)]),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, outcome);
        return (orgId, branchId);
    }

    private static SyncEnvelope SaleEnvelope(Guid org, Guid branch, Guid sale, Guid presentation, decimal kilos)
    {
        var line = new SaleLine(sale, 1, presentation, null, "Vacío", "Kg", kilos, 9000m, kilos * 9000m);
        var payload = new SalePayloadV1(sale, line.LineTotal, "Scanned", DateTimeOffset.UtcNow, [line]);
        return new SyncEnvelope(
            Guid.NewGuid(), 1, org, branch, sale, 1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, SalePayloadKinds.Sale,
            SyncPayloadCodec.Serialize(payload));
    }

    private static SyncEnvelope VoidEnvelope(Guid org, Guid branch, Guid sale, decimal total, Guid? operationId = null)
    {
        var payload = new SaleVoidedPayloadV1(
            sale, DateTimeOffset.UtcNow, Guid.NewGuid(), Pin, "Cobro duplicado", total, SaleTenderRules.Card(), Guid.NewGuid());
        return new SyncEnvelope(
            operationId ?? Guid.NewGuid(), 1, org, branch, sale, 2, payload.VoidedByOperatorId, Guid.NewGuid(), payload.VoidedAtUtc,
            SalePayloadKinds.Voided, SyncPayloadCodec.Serialize(payload));
    }

    private static decimal OnHand(Guid presentation)
    {
        using var owner = OpenOwner();
        return Scalar<decimal>(owner, "SELECT COALESCE(SUM(quantity), 0) FROM stock_movements WHERE presentation_id = $1", presentation);
    }

    [Fact]
    public async Task AVoid_IsRecordedAndAudited_AndPutsTheSalesStockBack_Once()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch) = await SeedAsync();
        var presentation = Presentation(org, branch);
        var sale = Guid.NewGuid();
        var store = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(org);
        store.TryApplyInbound(scope, SaleEnvelope(org, branch, sale, presentation, 2.5m));
        Assert.Equal(-2.5m, OnHand(presentation));

        var voidEnvelope = VoidEnvelope(org, branch, sale, 22_500m);
        Assert.Equal(InboundApplyOutcome.Applied, store.TryApplyInbound(scope, voidEnvelope).Outcome);
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, store.TryApplyInbound(scope, voidEnvelope).Outcome);
        // The same void under another operation id is a no-op too.
        Assert.Equal(InboundApplyOutcome.Applied, store.TryApplyInbound(scope, VoidEnvelope(org, branch, sale, 22_500m)).Outcome);

        Assert.Equal(0m, OnHand(presentation));
        using var owner = OpenOwner();
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM stock_movements WHERE kind = 'Reversal' AND source_type = 'PosSaleVoid' AND source_id = $1", sale));
        Assert.Equal("Cobro duplicado", Scalar<string>(owner, "SELECT reason FROM pos_sale_voids WHERE sale_id = $1", sale));
        Assert.Equal(Pin.OperatorId, Scalar<Guid>(owner, "SELECT authorized_by FROM pos_sale_voids WHERE sale_id = $1", sale));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'sale.voided' AND entity_id = $1", sale));
    }

    [Fact]
    public async Task AVoidIngestedBeforeItsSale_LeavesTheSaleWithoutStockMovements()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch) = await SeedAsync();
        var presentation = Presentation(org, branch);
        var sale = Guid.NewGuid();
        var store = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(org);

        Assert.Equal(InboundApplyOutcome.Applied, store.TryApplyInbound(scope, VoidEnvelope(org, branch, sale, 9000m)).Outcome);
        Assert.Equal(InboundApplyOutcome.Applied, store.TryApplyInbound(scope, SaleEnvelope(org, branch, sale, presentation, 1m)).Outcome);

        Assert.Equal(0m, OnHand(presentation));
        using var owner = OpenOwner();
        Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM stock_movements WHERE presentation_id = $1", presentation));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM pos_sale_voids WHERE sale_id = $1", sale));
    }

    [Fact]
    public void TheMigration_IsMirroredVerbatimInTheDevInitScript()
    {
        var root = PostgresTestFixture.RepoRoot();
        var migration = File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", "0044_pos_sale_voids.sql")).Replace("\r\n", "\n");
        var init = File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")).Replace("\r\n", "\n");

        Assert.Contains(migration, init);
    }
}
