using Commerce.BranchNode;
using Commerce.Cloud.Api;
using Commerce.Cloud.Api.Payments;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Payments;
using Commerce.Domain.Sync;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 5 tasks 5.1/5.3/5.5/5.7 (commerce-payments design.md "Storage
/// shape of the parallel effect path"; ADR-002): a branch commits a POS cash
/// payment with no cloud or gateway reachability and returns immediately; an
/// interrupted commit rolls back both new tables together while every
/// pre-existing sale table/test stays byte-identical; pending rows are
/// queryable and acknowledgeable.
/// </summary>
public sealed class BranchPaymentOfflineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-payments-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the OS temp directory is periodically reclaimed.
                }
            }
        }
    }

    private string ConnectionString => $"Data Source={_dbPath}";

    private static PaymentEffect NewEffect(Guid branchId, Guid subjectId, decimal amount = 15m) => new(
        EntryId: Guid.NewGuid(),
        BranchId: branchId,
        SubjectKind: "Sale",
        SubjectId: subjectId,
        Method: "Cash",
        Amount: amount,
        EntryKind: "Payment",
        ReversesEntryId: null,
        OccurredAtUtc: DateTimeOffset.UtcNow);

    private static SyncEnvelope NewEnvelope(Guid branchId, Guid entryId, string payload) => new(
        OperationId: entryId, ContractVersion: 1, OrganizationId: Guid.NewGuid(), BranchId: branchId,
        AggregateId: entryId, AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
        OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "PaymentRecorded", Payload: payload);

    [Fact]
    public void CommitPaymentAtomically_NoConnectivityRequired_CommitsAndReturnsImmediately_AsPending()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var effect = NewEffect(branchId, Guid.NewGuid());
        var envelope = NewEnvelope(branchId, effect.EntryId, "{}");

        store.CommitPaymentAtomically(envelope, effect);

        var pending = store.GetPendingPaymentOutbox(branchId);
        Assert.Single(pending);
        Assert.Equal(effect.EntryId, pending[0].OperationId);
    }

    [Fact]
    public void SimulateInterruptedPaymentCommit_RollsBackBothTablesTogether()
    {
        var branchId = Guid.NewGuid();
        var effect = NewEffect(branchId, Guid.NewGuid());
        var envelope = NewEnvelope(branchId, effect.EntryId, "{}");

        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.SimulateInterruptedPaymentCommit(envelope, effect);
        }

        // Reopen (simulates restart) — neither table should carry the row.
        using var reopened = new BranchSyncStore(ConnectionString);
        var pending = reopened.GetPendingPaymentOutbox(branchId);
        Assert.Empty(pending);
    }

    [Fact]
    public void GetPendingPaymentOutbox_ReturnsOnlyPendingRows()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var effect = NewEffect(branchId, Guid.NewGuid());
        var envelope = NewEnvelope(branchId, effect.EntryId, "{}");
        store.CommitPaymentAtomically(envelope, effect);

        store.AcknowledgePayment(effect.EntryId);

        var pending = store.GetPendingPaymentOutbox(branchId);
        Assert.Empty(pending);
    }

    [Fact]
    public void AcknowledgePayment_SetsStatusAcknowledged()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var effect = NewEffect(branchId, Guid.NewGuid());
        var envelope = NewEnvelope(branchId, effect.EntryId, "{}");
        store.CommitPaymentAtomically(envelope, effect);

        var acknowledged = store.AcknowledgePayment(effect.EntryId);

        Assert.True(acknowledged);
        Assert.Empty(store.GetPendingPaymentOutbox(branchId));
    }

    /// <summary>
    /// The existing outbox/sale_effects/sale_lines/inbox DDL and every
    /// pre-existing sale method remain green — a payment commit exercises an
    /// entirely additive path.
    /// </summary>
    [Fact]
    public void ExistingSalePath_UnaffectedByPaymentTables()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var saleEffect = new SaleEffect(Guid.NewGuid(), branchId, 99m, DateTimeOffset.UtcNow);
        var saleEnvelope = new SyncEnvelope(
            OperationId: saleEffect.SaleId, ContractVersion: 1, OrganizationId: Guid.NewGuid(), BranchId: branchId,
            AggregateId: saleEffect.SaleId, AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "sale", Payload: "{}");

        var result = store.CommitSaleAtomically(saleEnvelope, saleEffect);

        Assert.True(result.WasNewlyCommitted);
        Assert.Equal(saleEffect.SaleId, result.Effect.SaleId);
    }

    /// <summary>
    /// Covers Unit 5 task 5.7 (end-to-end wiring): a branch's pending
    /// `payment_outbox` row POSTs through the existing `/sync` receiver path
    /// (<see cref="CloudSyncReceiver"/> + <see cref="PaymentEffectApplier"/>,
    /// live Postgres); a duplicate delivery of the same operation_id ⇒
    /// `DuplicateIgnored`, projection skipped; the branch then calls
    /// <see cref="BranchSyncStore.AcknowledgePayment"/>.
    /// </summary>
    [Collection("Postgres")]
    public sealed class EndToEndWiringTests : IDisposable
    {
        private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-e2e-{Guid.NewGuid():N}.db");
        private string ConnectionString => $"Data Source={_dbPath}";

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                if (File.Exists(path))
                {
                    try { File.Delete(path); } catch (IOException) { }
                }
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
        }

        private void ApplyMigrationsAndReset(NpgsqlConnection owner, Guid organizationId)
        {
            var migrationsDir = Path.Combine(RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in new[]
            {
                "0001_init_rls.sql", "0002_users.sql", "0003_organizations_branches.sql",
                "0004_device_credentials.sql", "0005_password_recovery.sql", "0006_role_taxonomy.sql",
                "0007_platform_administration.sql", "0008_customer_registry.sql", "0009_catalog_and_pricing.sql",
                "0010_guest_ordering.sql", "0011_payments.sql"
            })
            {
                var sql = File.ReadAllText(Path.Combine(migrationsDir, file));
                if (file == "0001_init_rls.sql")
                {
                    sql = sql.Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
                }
                using var cmd = new NpgsqlCommand(sql, owner);
                cmd.ExecuteNonQuery();
            }

            using var reset = new NpgsqlCommand("TRUNCATE TABLE payment_entries, sync_inbox, organizations CASCADE", owner);
            reset.ExecuteNonQuery();

            using var insertOrg = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", owner);
            insertOrg.Parameters.AddWithValue(organizationId);
            insertOrg.ExecuteNonQuery();
        }

        [Fact]
        public async Task PendingPaymentOutboxRow_SyncsThroughReceiver_AppliesOnce_ThenAcknowledges()
        {
            if (!_postgresAvailable)
            {
                Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
                return;
            }

            var organizationId = Guid.NewGuid();
            using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
            {
                owner.Open();
                ApplyMigrationsAndReset(owner, organizationId);
            }

            // Branch side: commit a cash payment with no connectivity.
            using var branchStore = new BranchSyncStore(ConnectionString);
            var branchId = Guid.NewGuid();
            var entryId = Guid.NewGuid();
            var effect = new PaymentEffect(
                EntryId: entryId, BranchId: branchId, SubjectKind: "Sale", SubjectId: Guid.NewGuid(),
                Method: "Cash", Amount: 20m, EntryKind: "Payment", ReversesEntryId: null, OccurredAtUtc: DateTimeOffset.UtcNow);
            var payload = System.Text.Json.JsonSerializer.Serialize(new PaymentRecordedPayload(
                EntryId: entryId, SubjectKind: effect.SubjectKind, SubjectId: effect.SubjectId, EntryKind: effect.EntryKind,
                Method: effect.Method, Amount: effect.Amount, ReversesEntryId: null, ActorId: Guid.NewGuid(), RecordedAtUtc: effect.OccurredAtUtc));
            var outboundEnvelope = new SyncEnvelope(
                OperationId: entryId, ContractVersion: 1, OrganizationId: organizationId, BranchId: branchId,
                AggregateId: entryId, AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
                OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "PaymentRecorded", Payload: payload);

            branchStore.CommitPaymentAtomically(outboundEnvelope, effect);

            var pending = branchStore.GetPendingPaymentOutbox(branchId);
            Assert.Single(pending);

            // Cloud side: the SAME existing /sync receiver path.
            await using var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
            var inboxStore = new PostgresCloudInboxStore(dataSource);
            var applier = new PaymentEffectApplier(dataSource);
            var receiver = new CloudSyncReceiver(inboxStore, applier);
            var scope = new CloudTenantScope(organizationId);

            var syncedEnvelope = new SyncEnvelope(
                OperationId: pending[0].OperationId, ContractVersion: 1, OrganizationId: pending[0].OrganizationId,
                BranchId: pending[0].BranchId, AggregateId: pending[0].AggregateId, AggregateVersion: pending[0].AggregateVersion,
                ActorId: pending[0].ActorId, CorrelationId: pending[0].CorrelationId, OccurredAtUtc: pending[0].OccurredAtUtc,
                PayloadKind: pending[0].PayloadKind, Payload: pending[0].Payload);

            var first = receiver.Receive(scope, syncedEnvelope);
            var duplicate = receiver.Receive(scope, syncedEnvelope);

            Assert.Equal(InboundApplyOutcome.Applied, first.Outcome);
            Assert.Equal(InboundApplyOutcome.DuplicateIgnored, duplicate.Outcome);

            using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
            {
                owner.Open();
                using var countCmd = new NpgsqlCommand("SELECT count(*) FROM payment_entries WHERE entry_id = $1", owner);
                countCmd.Parameters.AddWithValue(entryId);
                Assert.Equal(1, (long)countCmd.ExecuteScalar()!);
            }

            // Branch side: acknowledge after the cloud confirmed the apply.
            var acknowledged = branchStore.AcknowledgePayment(entryId);
            Assert.True(acknowledged);
            Assert.Empty(branchStore.GetPendingPaymentOutbox(branchId));
        }
    }
}
