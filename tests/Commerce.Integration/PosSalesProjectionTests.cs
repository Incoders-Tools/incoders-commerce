using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// branch-offline-sync "Sale Number Projection": the inbox transaction projects each sale
/// envelope into `pos_sales` and verifies the number the terminal claimed (its register must
/// be the one the registry assigned to the calling installation in that branch, the branch
/// code must match, the number must be unused). A rejected claim stores the sale WITHOUT a
/// number and leaves an audit row; ingestion is never blocked. Real Postgres, as app_runtime.
/// </summary>
[Collection("Postgres")]
public sealed class PosSalesProjectionTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PosSalesProjectionTests()
    {
        if (!_postgresAvailable) return;

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            foreach (var file in new[]
                     {
                         "0001_init_rls.sql", "0002_users.sql", "0003_organizations_branches.sql", "0021_branch_codes.sql",
                         "0004_device_credentials.sql", "0022_terminal_registers.sql", "0024_terminal_registers_assign_result.sql", "0023_pos_sales.sql",
                     })
            {
                PostgresTestFixture.ApplyMigration(owner, file);
            }
            using var reset = new NpgsqlCommand(
                "TRUNCATE TABLE pos_sales, sync_inbox, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
            reset.ExecuteNonQuery();
        }
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    /// <summary>The organization whose rows the owner-side readers below look at (FORCE RLS applies to them too).</summary>
    private Guid _readOrg;

    private NpgsqlConnection OpenOwner()
    {
        var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var set = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, false)", owner);
        set.Parameters.AddWithValue(_readOrg.ToString());
        set.ExecuteNonQuery();
        return owner;
    }

    private sealed record Terminal(CloudTenantScope Scope, Guid BranchId, Guid InstallationId, int Register);

    private async Task<(Guid OrgId, Guid BranchId, Guid UserId)> SeedAsync()
    {
        var userStore = new PostgresUserAccountStore(_dataSource!);
        var orgStore = new PostgresOrganizationStore(_dataSource!, userStore);
        var (orgId, branchId, userId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var outcome = await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(orgId), new NewOrganization(orgId, "Sales Org"), new NewBranch(branchId, "Main"),
            new NewUserAccount(userId, $"sales-{orgId}@example.com", "hash", [branchId], [new RoleDto("cashier", Permission.OperatePos)]),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, outcome);
        _readOrg = orgId;
        return (orgId, branchId, userId);
    }

    private async Task<Terminal> PairAsync(Guid orgId, Guid branchId, Guid userId)
    {
        var scope = new CloudTenantScope(orgId);
        var installationId = Guid.NewGuid();
        var issued = await new PostgresDeviceCredentialStore(_dataSource!)
            .IssueAsync(scope, installationId, branchId, userId, CancellationToken.None);
        return new Terminal(scope, branchId, installationId, issued.RegisterNumber);
    }

    private static SyncEnvelope SaleEnvelope(
        Guid orgId, Guid branchId, Guid? saleId = null, int? code = null, int? register = null, int? sequence = null, Guid? operationId = null)
    {
        var sale = saleId ?? Guid.NewGuid();
        var payload = new SalePayloadV1(
            sale, 855.5m, "Manual", DateTimeOffset.UtcNow, [], BranchCode: code, RegisterNumber: register, SaleSequence: sequence);
        return new SyncEnvelope(
            operationId ?? Guid.NewGuid(), 1, orgId, branchId, sale, 1, Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, "sale", SyncPayloadCodec.Serialize(payload));
    }

    private PostgresCloudInboxStore Store() => new(_dataSource!);

    private T Scalar<T>(string sql, params object[] args)
    {
        using var owner = OpenOwner();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return (T)cmd.ExecuteScalar()!;
    }

    private (short? Register, int? Sequence) NumberOf(Guid saleId)
    {
        using var owner = OpenOwner();
        using var cmd = new NpgsqlCommand("SELECT register_number, sale_sequence FROM pos_sales WHERE sale_id = $1", owner);
        cmd.Parameters.AddWithValue(saleId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "the sale was not projected into pos_sales");
        return (reader.IsDBNull(0) ? null : reader.GetInt16(0), reader.IsDBNull(1) ? null : reader.GetInt32(1));
    }

    private long Conflicts(Guid saleId) =>
        Scalar<long>("SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'sale.number_conflict'", saleId);

    private long Ingested(Guid operationId) =>
        Scalar<long>("SELECT count(*) FROM sync_inbox WHERE operation_id = $1", operationId);

    [Fact]
    public async Task ANumberedSaleFromTheRegisteredTerminal_IsProjectedWithItsNumber()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var envelope = SaleEnvelope(org, branch, code: 1, register: terminal.Register, sequence: 125);

        var result = Store().TryApplyInbound(terminal.Scope, envelope, terminal.InstallationId);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.Equal(((short?)terminal.Register, (int?)125), NumberOf(envelope.AggregateId));
        Assert.Equal(0, Conflicts(envelope.AggregateId));
        Assert.Equal(855.5m, Scalar<decimal>("SELECT total_amount FROM pos_sales WHERE sale_id = $1", envelope.AggregateId));
    }

    [Fact]
    public async Task ARedeliveredSale_IsIgnored_AndProjectedOnce()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var envelope = SaleEnvelope(org, branch, code: 1, register: terminal.Register, sequence: 1);

        var first = Store().TryApplyInbound(terminal.Scope, envelope, terminal.InstallationId);
        var second = Store().TryApplyInbound(terminal.Scope, envelope, terminal.InstallationId);

        Assert.Equal(InboundApplyOutcome.Applied, first.Outcome);
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, second.Outcome);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM pos_sales WHERE sale_id = $1", envelope.AggregateId));
    }

    [Fact]
    public async Task ASaleWithoutANumber_IsProjectedWithNulls_AndRaisesNoConflict()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var envelope = SaleEnvelope(org, branch);

        Store().TryApplyInbound(terminal.Scope, envelope, terminal.InstallationId);

        Assert.Equal(((short?)null, (int?)null), NumberOf(envelope.AggregateId));
        Assert.Equal(0, Conflicts(envelope.AggregateId));
    }

    [Fact]
    public async Task ManyUnnumberedSales_DoNotCollide()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(InboundApplyOutcome.Applied, Store().TryApplyInbound(terminal.Scope, SaleEnvelope(org, branch), terminal.InstallationId).Outcome);
        }

        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM pos_sales WHERE organization_id = $1 AND sale_sequence IS NULL", org));
    }

    [Fact]
    public async Task ARegisterThatIsNotTheCallersOwn_StillIngestsTheSale_ButWithoutANumber_AndAnAuditRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var mine = await PairAsync(org, branch, user);
        var other = await PairAsync(org, branch, user);
        var envelope = SaleEnvelope(org, branch, code: 1, register: other.Register, sequence: 9);

        var result = Store().TryApplyInbound(mine.Scope, envelope, mine.InstallationId);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.Equal(1, Ingested(envelope.OperationId));
        Assert.Equal(((short?)null, (int?)null), NumberOf(envelope.AggregateId));
        Assert.Equal(1, Conflicts(envelope.AggregateId));
    }

    [Fact]
    public async Task AWrongBranchCode_IsAConflict_NotABlockedSale()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var envelope = SaleEnvelope(org, branch, code: 7, register: terminal.Register, sequence: 1);

        var result = Store().TryApplyInbound(terminal.Scope, envelope, terminal.InstallationId);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.Equal(((short?)null, (int?)null), NumberOf(envelope.AggregateId));
        Assert.Equal(1, Conflicts(envelope.AggregateId));
    }

    [Fact]
    public async Task ANumberClaimedTwice_KeepsTheFirst_AndStoresTheSecondSaleUnnumberedWithAnAuditRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var first = SaleEnvelope(org, branch, code: 1, register: terminal.Register, sequence: 5);
        var second = SaleEnvelope(org, branch, code: 1, register: terminal.Register, sequence: 5);

        var r1 = Store().TryApplyInbound(terminal.Scope, first, terminal.InstallationId);
        var r2 = Store().TryApplyInbound(terminal.Scope, second, terminal.InstallationId);

        Assert.Equal(InboundApplyOutcome.Applied, r1.Outcome);
        Assert.Equal(InboundApplyOutcome.Applied, r2.Outcome);
        Assert.Equal(((short?)terminal.Register, (int?)5), NumberOf(first.AggregateId));
        Assert.Equal(((short?)null, (int?)null), NumberOf(second.AggregateId));
        Assert.Equal(0, Conflicts(first.AggregateId));
        Assert.Equal(1, Conflicts(second.AggregateId));
        Assert.Equal(1, Ingested(second.OperationId));
    }

    [Fact]
    public async Task WithoutKnowingTheCallingInstallation_ANumberCannotBeVerified_SoItIsNotStored()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var envelope = SaleEnvelope(org, branch, code: 1, register: terminal.Register, sequence: 1);

        Store().TryApplyInbound(terminal.Scope, envelope);

        Assert.Equal(((short?)null, (int?)null), NumberOf(envelope.AggregateId));
        Assert.Equal(1, Conflicts(envelope.AggregateId));
    }

    [Fact]
    public async Task AnOutOfRangeOrPartialClaim_IsAConflict()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var zeroSequence = SaleEnvelope(org, branch, code: 1, register: terminal.Register, sequence: 0);
        var partial = SaleEnvelope(org, branch, register: terminal.Register);

        Store().TryApplyInbound(terminal.Scope, zeroSequence, terminal.InstallationId);
        Store().TryApplyInbound(terminal.Scope, partial, terminal.InstallationId);

        Assert.Equal(((short?)null, (int?)null), NumberOf(zeroSequence.AggregateId));
        Assert.Equal(((short?)null, (int?)null), NumberOf(partial.AggregateId));
        Assert.Equal(1, Conflicts(zeroSequence.AggregateId));
        Assert.Equal(1, Conflicts(partial.AggregateId));
    }

    [Fact]
    public async Task ASaleSyncedAfterTheTerminalMovedToAnotherBranch_KeepsItsNumber()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branchA, user) = await SeedAsync();
        var branchB = Guid.NewGuid();
        await new PostgresOrganizationStore(_dataSource!, new PostgresUserAccountStore(_dataSource!))
            .CreateBranchAsync(new CloudTenantScope(org), new NewBranch(branchB, "Second"), CancellationToken.None);
        var installation = Guid.NewGuid();
        var credentials = new PostgresDeviceCredentialStore(_dataSource!);
        var scope = new CloudTenantScope(org);
        var inA = await credentials.IssueAsync(scope, installation, branchA, user, CancellationToken.None);
        await credentials.IssueAsync(scope, installation, branchB, user, CancellationToken.None);
        // The sale was made in branch A while the terminal still belonged there.
        var envelope = SaleEnvelope(org, branchA, code: 1, register: inA.RegisterNumber, sequence: 42);

        Store().TryApplyInbound(scope, envelope, installation);

        Assert.Equal(((short?)inA.RegisterNumber, (int?)42), NumberOf(envelope.AggregateId));
    }

    [Fact]
    public async Task AnUnreadableSalePayload_IsStillIngested_AndNotProjected()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var envelope = SaleEnvelope(org, branch) with { Payload = "{\"v\":1}" };

        var result = Store().TryApplyInbound(terminal.Scope, envelope, terminal.InstallationId);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.Equal(1, Ingested(envelope.OperationId));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM pos_sales WHERE operation_id = $1", envelope.OperationId));
    }

    [Fact]
    public async Task AnyProjectionFailure_NotOnlyAPostgresOne_StillIngestsTheSale_AndLeavesAnAuditRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var envelope = SaleEnvelope(org, branch, code: 1, register: terminal.Register, sequence: 1);

        var store = new PostgresCloudInboxStore(
            _dataSource!, projectionFault: _ => throw new InvalidCastException("injected driver failure"));

        var result = store.TryApplyInbound(terminal.Scope, envelope, terminal.InstallationId);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);

        Assert.Equal(1, Ingested(envelope.OperationId));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM pos_sales WHERE operation_id = $1", envelope.OperationId));
        Assert.Equal(1, Scalar<long>(
            "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = $2", envelope.AggregateId, PosSaleProjection.FailureAction));
    }

    [Fact]
    public async Task WhenThePosSalesTableIsMissing_TheSaleIsStillIngested()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var envelope = SaleEnvelope(org, branch, code: 1, register: terminal.Register, sequence: 1);
        void Rename(string from, string to)
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            using var cmd = new NpgsqlCommand($"ALTER TABLE {from} RENAME TO {to}", owner);
            cmd.ExecuteNonQuery();
        }

        // An API deployed ahead of migration 0023 must keep ingesting sales.
        Rename("pos_sales", "pos_sales_hidden");
        try
        {
            var result = Store().TryApplyInbound(terminal.Scope, envelope, terminal.InstallationId);

            Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
            Assert.Equal(1, Ingested(envelope.OperationId));
        }
        finally
        {
            Rename("pos_sales_hidden", "pos_sales");
        }
    }

    [Fact]
    public async Task PosSales_AreInvisibleToAnotherOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        Store().TryApplyInbound(terminal.Scope, SaleEnvelope(org, branch), terminal.InstallationId);

        await using var connection = await _dataSource!.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await TenantScopeSql.ApplyAsync(connection, tx, new CloudTenantScope(Guid.NewGuid()), CancellationToken.None);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pos_sales", connection, tx);

        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task TheTable_IsAppendOnlyForTheRuntimeRole()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, user) = await SeedAsync();
        var terminal = await PairAsync(org, branch, user);
        var envelope = SaleEnvelope(org, branch);
        Store().TryApplyInbound(terminal.Scope, envelope, terminal.InstallationId);

        await using var connection = await _dataSource!.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await TenantScopeSql.ApplyAsync(connection, tx, terminal.Scope, CancellationToken.None);
        await using var update = new NpgsqlCommand("UPDATE pos_sales SET total_amount = 1", connection, tx);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task TheDatabase_RejectsADuplicateNumber_ButNotDuplicateNulls()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (org, branch, _) = await SeedAsync();
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        void Insert(short? register, int? sequence)
        {
            using var set = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, false)", owner);
            set.Parameters.AddWithValue(org.ToString());
            set.ExecuteNonQuery();
            using var cmd = new NpgsqlCommand(
                "INSERT INTO pos_sales (organization_id, branch_id, sale_id, register_number, sale_sequence, operation_id, occurred_at_utc, total_amount) " +
                "VALUES ($1, $2, $3, $4, $5, $6, now(), 1)", owner);
            cmd.Parameters.AddWithValue(org);
            cmd.Parameters.AddWithValue(branch);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)register ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Smallint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)sequence ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.ExecuteNonQuery();
        }

        Insert(1, 1);
        Insert(null, null);
        Insert(null, null);
        var duplicate = Assert.Throws<PostgresException>(() => Insert(1, 1));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var half = Assert.Throws<PostgresException>(() => Insert(2, null));
        Assert.Equal(PostgresErrorCodes.CheckViolation, half.SqlState);
    }
}
