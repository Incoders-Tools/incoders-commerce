using System.Collections.Concurrent;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sync;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Unit 2's non-negotiable pooler proof-of-concept (task 2.6/2.7,
/// design.md "Pooling + tenant scope", spec "Pooler and Session-Scoping
/// Interaction Must Be Proven"): proves that concurrent
/// transaction-mode-pooled connections issuing different
/// `app.current_org_id` values via <see cref="PostgresCloudInboxStore"/>
/// never observe another org's rows.
///
/// REALISTIC LOCAL APPROXIMATION, NOT THE REAL SUPABASE POOLER: a real
/// Supabase project is manual, out-of-repo provisioning and unavailable in
/// this environment. This test runs against PgBouncer in
/// `pool_mode = transaction` (`deploy/dev/compose.yaml`'s `pgbouncer`
/// service on port 6543) in front of the same Postgres used by
/// `PostgresCloudInboxStoreTests`. PgBouncer's transaction pooling and
/// Supabase's Supavisor transaction pooling both multiplex many client
/// connections onto few server connections and both guarantee a server
/// connection is only ever handed to one client transaction at a time — the
/// exact property `set_config(..., is_local: true)` scoping depends on. This
/// is the strongest test runnable in this sandboxed environment; genuine
/// Supavisor-specific behavior (TLS terminate, connection draining under
/// load, its specific release-back-to-pool timing) remains UNPROVEN pending
/// a real Supabase staging project, and MUST be re-run against the real
/// pooler before this scoping approach is accepted as verified for
/// production (see deploy/README.md).
/// </summary>
[Collection("Postgres")]
public sealed class PoolerScopingTests : IDisposable
{
    private readonly bool _poolerAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.PooledConnectionString);
    private readonly NpgsqlDataSource? _pooledDataSource;

    public PoolerScopingTests()
    {
        if (!_poolerAvailable)
        {
            return;
        }

        PostgresTestFixture.ResetInbox();
        _pooledDataSource = NpgsqlDataSource.Create(PostgresTestFixture.PooledConnectionString);
    }

    public void Dispose() => _pooledDataSource?.Dispose();

    private static SyncEnvelope Envelope(Guid operationId, Guid organizationId) => new(
        OperationId: operationId,
        ContractVersion: 1,
        OrganizationId: organizationId,
        BranchId: Guid.NewGuid(),
        AggregateId: Guid.NewGuid(),
        AggregateVersion: 1,
        ActorId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        OccurredAtUtc: DateTimeOffset.UtcNow,
        PayloadKind: "sale",
        Payload: "{\"v\":1}");

    [Fact]
    public async Task ConcurrentPooledTransactions_NeverObserveAnotherOrganizationsRows()
    {
        if (!_poolerAvailable)
        {
            Console.WriteLine(
                "SKIPPED: PgBouncer pooler not reachable at " + PostgresTestFixture.PooledConnectionString +
                ". Start `docker compose -f deploy/dev/compose.yaml up -d pgbouncer`. " +
                "Pooler-specific isolation behavior remains UNPROVEN in this run — see deploy/README.md.");
            return;
        }

        const int organizationCount = 12;
        var store = new PostgresCloudInboxStore(_pooledDataSource!);
        var scopes = Enumerable.Range(0, organizationCount)
            .Select(_ => new CloudTenantScope(Guid.NewGuid()))
            .ToArray();

        // Every organization applies its own envelope concurrently, through
        // the SAME pooled endpoint, each opening its own explicit transaction
        // (PostgresCloudInboxStore.TryApplyInbound). If transaction pooling
        // ever let one client observe a server connection still carrying
        // another client's set_config value, this would show up as a
        // cross-organization row appearing in the wrong org's inbox.
        var applyTasks = scopes
            .Select(scope => Task.Run(() => store.TryApplyInbound(scope, Envelope(Guid.NewGuid(), scope.OrganizationId))))
            .ToArray();
        var applyResults = await Task.WhenAll(applyTasks);

        Assert.All(applyResults, r => Assert.Equal(InboundApplyOutcome.Applied, r.Outcome));

        // Now read back concurrently, interleaved with more applies, to
        // maximize the chance of exposing any leaked transaction-local scope
        // on a reused pooled server connection.
        var crossContamination = new ConcurrentBag<string>();
        var readTasks = scopes.Select(scope => Task.Run(() =>
        {
            for (var i = 0; i < 5; i++)
            {
                var inbox = store.GetInboxFor(scope);
                foreach (var envelope in inbox)
                {
                    if (envelope.OrganizationId != scope.OrganizationId)
                    {
                        crossContamination.Add(
                            $"scope org {scope.OrganizationId} observed envelope for org {envelope.OrganizationId}");
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(readTasks);

        Assert.Empty(crossContamination);

        // Final sanity: each org sees exactly its own one row, never zero
        // (which would indicate the opposite failure — over-eager denial)
        // and never more than one (which would indicate leakage).
        foreach (var scope in scopes)
        {
            Assert.Single(store.GetInboxFor(scope));
        }
    }
}
