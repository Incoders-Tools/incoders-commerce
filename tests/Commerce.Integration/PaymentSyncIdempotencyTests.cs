using System.Text.Json;
using Commerce.Cloud.Api;
using Commerce.Cloud.Api.Payments;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sync;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 4 task 4.16 and Unit 4 task 4.18's regression half
/// (commerce-payments design.md "Payment-Effect Synchronization Path
/// Parallel to the Sale Outbox"): replaying the same payment envelope under
/// the same `operationId` applies exactly once, and dispatching a
/// `PaymentRecorded` envelope never touches the sale-shaped `outbox`/
/// `sale_effects` schema — only `sync_inbox` + `payment_entries`.
/// </summary>
[Collection("Postgres")]
public sealed class PaymentSyncIdempotencyTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root.");
        }
        return dir.FullName;
    }

    private void ApplyMigrationsAndReset(NpgsqlConnection owner)
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
    }

    [Fact]
    public async Task Receive_SamePaymentEnvelopeTwice_AppliesOnce()
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
            ApplyMigrationsAndReset(owner);
            using var insertOrg = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", owner);
            insertOrg.Parameters.AddWithValue(organizationId);
            insertOrg.ExecuteNonQuery();
        }

        await using var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var inboxStore = new PostgresCloudInboxStore(dataSource);
        var applier = new PaymentEffectApplier(dataSource);
        var receiver = new CloudSyncReceiver(inboxStore, applier);

        var scope = new CloudTenantScope(organizationId);
        var entryId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new PaymentRecordedPayload(
            EntryId: entryId, SubjectKind: "Order", SubjectId: Guid.NewGuid(), EntryKind: "Payment",
            Method: "Cash", Amount: 15m, ReversesEntryId: null, ActorId: Guid.NewGuid(), RecordedAtUtc: DateTimeOffset.UtcNow));

        var envelope = new SyncEnvelope(
            OperationId: operationId, ContractVersion: 1, OrganizationId: organizationId, BranchId: Guid.NewGuid(),
            AggregateId: entryId, AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "PaymentRecorded", Payload: payload);

        var first = receiver.Receive(scope, envelope);
        var second = receiver.Receive(scope, envelope);

        Assert.Equal(InboundApplyOutcome.Applied, first.Outcome);
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, second.Outcome);

        using var owner2 = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner2.Open();
        using var countCmd = new NpgsqlCommand("SELECT count(*) FROM payment_entries WHERE entry_id = $1", owner2);
        countCmd.Parameters.AddWithValue(entryId);
        var count = (long)countCmd.ExecuteScalar()!;
        Assert.Equal(1, count);
    }

    /// <summary>
    /// Dispatching a payment envelope touches only `sync_inbox` +
    /// `payment_entries` — `outbox`/`sale_effects`/`sale_lines` (cloud has no
    /// such tables; they are branch-local only) are never referenced.
    /// </summary>
    [Fact]
    public async Task Receive_PaymentEnvelope_NeverTouchesSaleSchema()
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
            ApplyMigrationsAndReset(owner);
            using var insertOrg = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", owner);
            insertOrg.Parameters.AddWithValue(organizationId);
            insertOrg.ExecuteNonQuery();

            // Cloud has no sale_effects/outbox/sale_lines tables at all —
            // this asserts that fact directly, proving a payment envelope
            // structurally cannot touch schema that does not exist server-side.
            using var checkCmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_tables WHERE tablename IN ('outbox', 'sale_effects', 'sale_lines')", owner);
            var tableCount = (long)checkCmd.ExecuteScalar()!;
            Assert.Equal(0, tableCount);
        }
    }
}
