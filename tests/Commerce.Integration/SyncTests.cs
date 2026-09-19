using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Cloud.Api;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Sync;

namespace Commerce.Integration;

/// <summary>
/// Covers `openspec/changes/commerce-foundation/specs/branch-offline-sync/spec.md`:
/// atomic local commit, retryable idempotent delivery, freshness/audit
/// visibility, and conflict review — plus a PostgreSQL-RLS-equivalent
/// default-deny/cross-org-denial fixture for the cloud inbox (see
/// `CloudInboxStore` for why this is an in-memory equivalent rather than a
/// live PostgreSQL RLS integration in this environment).
/// </summary>
public sealed class SyncTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

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

    private static SyncEnvelope SaleEnvelope(
        Guid operationId,
        Guid organizationId,
        Guid branchId,
        Guid saleId,
        Guid actorId,
        long aggregateVersion = 1) => new(
            OperationId: operationId,
            ContractVersion: 1,
            OrganizationId: organizationId,
            BranchId: branchId,
            AggregateId: saleId,
            AggregateVersion: aggregateVersion,
            ActorId: actorId,
            CorrelationId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            PayloadKind: "sale",
            Payload: "{}");

    // --- Atomic sale / outbox --------------------------------------------

    [Fact]
    public void OfflineSale_CommitsEffectAndOutboxAtomically()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        var result = store.CommitSaleAtomically(
            SaleEnvelope(operationId, organizationId, branchId, saleId, actorId),
            new SaleEffect(saleId, branchId, 42.50m, DateTimeOffset.UtcNow));

        Assert.True(result.WasNewlyCommitted);
        Assert.Equal(saleId, result.Effect.SaleId);

        var pending = store.GetPendingOutbox(branchId);
        var outboxEntry = Assert.Single(pending);
        Assert.Equal(operationId, outboxEntry.OperationId);
    }

    [Fact]
    public void InterruptedCommit_NeverPersistsPartialSaleAcrossRestart()
    {
        var branchId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.SimulateInterruptedCommit(
                SaleEnvelope(operationId, organizationId, branchId, saleId, actorId),
                new SaleEffect(saleId, branchId, 10m, DateTimeOffset.UtcNow));
        }

        // Branch restarts: reopen the same SQLite file as a fresh store.
        using var restarted = new BranchSyncStore(ConnectionString);
        var pending = restarted.GetPendingOutbox(branchId);
        Assert.Empty(pending);
    }

    // --- Duplicate delivery / lost ACK ------------------------------------

    [Fact]
    public void DuplicateSaleCommit_AppliesNoSecondBusinessEffect()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var envelope = SaleEnvelope(operationId, organizationId, branchId, saleId, actorId);
        var effect = new SaleEffect(saleId, branchId, 15m, DateTimeOffset.UtcNow);

        var first = store.CommitSaleAtomically(envelope, effect);
        var second = store.CommitSaleAtomically(envelope, effect);

        Assert.True(first.WasNewlyCommitted);
        Assert.False(second.WasNewlyCommitted);
        Assert.Single(store.GetPendingOutbox(branchId));
    }

    [Fact]
    public void LostAck_RetriedAcknowledgement_ConvergesToOneAcknowledgedState()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        store.CommitSaleAtomically(
            SaleEnvelope(operationId, Guid.NewGuid(), branchId, Guid.NewGuid(), Guid.NewGuid()),
            new SaleEffect(Guid.NewGuid(), branchId, 5m, DateTimeOffset.UtcNow));

        var firstAck = store.Acknowledge(operationId);
        var retriedAck = store.Acknowledge(operationId);

        Assert.True(firstAck);
        Assert.True(retriedAck);
        Assert.Empty(store.GetPendingOutbox(branchId));
    }

    [Fact]
    public void DuplicateInboundDelivery_IsIgnoredAfterFirstApply()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var envelope = SaleEnvelope(Guid.NewGuid(), Guid.NewGuid(), branchId, Guid.NewGuid(), Guid.NewGuid());

        var first = store.ApplyInbound(envelope);
        var second = store.ApplyInbound(envelope);

        Assert.Equal(InboundApplyOutcome.Applied, first.Outcome);
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, second.Outcome);
    }

    // --- Freshness ----------------------------------------------------------

    [Fact]
    public void StaleBranchView_ExposesLastAcknowledgedTimeAndPendingState()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        store.CommitSaleAtomically(
            SaleEnvelope(operationId, Guid.NewGuid(), branchId, Guid.NewGuid(), Guid.NewGuid()),
            new SaleEffect(Guid.NewGuid(), branchId, 8m, DateTimeOffset.UtcNow));

        var stale = store.GetStatus(branchId, isOffline: true);
        Assert.Null(stale.LastAcknowledgedUtc);
        Assert.Equal(1, stale.PendingOperationCount);
        Assert.True(stale.IsOffline);

        store.Acknowledge(operationId);
        var fresh = store.GetStatus(branchId, isOffline: false);
        Assert.NotNull(fresh.LastAcknowledgedUtc);
        Assert.Equal(0, fresh.PendingOperationCount);
        Assert.False(fresh.IsOffline);
    }

    // --- Authority / conflict review ----------------------------------------

    [Fact]
    public void ConflictingNonCommutingEdits_RetainBothHistoriesUntilAuthorizedReview()
    {
        var auditSink = new InMemoryAuditSink();
        var authService = new TenantAuthorizationService(auditSink);
        using var store = new BranchSyncStore(ConnectionString);
        var service = new BranchNodeService(store, authService, auditSink);

        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var local = SaleEnvelope(Guid.NewGuid(), organizationId, branchId, aggregateId, Guid.NewGuid(), aggregateVersion: 2);
        var remote = local with { OperationId = Guid.NewGuid(), ActorId = Guid.NewGuid(), AggregateVersion = 2 };

        var conflict = service.DetectConflict(local, remote);

        Assert.Null(conflict.Resolution);
        Assert.Equal(local, conflict.Local);
        Assert.Equal(remote, conflict.Remote);

        var reviewer = new UserAccount(
            Guid.NewGuid(),
            organizationId,
            new[] { branchId },
            new[] { new Role("reviewer", Permission.ManageBranchSettings) });
        var correlationId = Guid.NewGuid();

        var resolved = service.ReviewConflict(conflict, reviewer, decision: "keep-local", correlationId);

        Assert.True(resolved);
        Assert.NotNull(conflict.Resolution);
        Assert.Equal("keep-local", conflict.Resolution!.Decision);
        // Both histories remain traceable even after resolution — no silent overwrite.
        Assert.Equal(local, conflict.Local);
        Assert.Equal(remote, conflict.Remote);

        var entry = Assert.Single(auditSink.Entries);
        Assert.Equal("review-sync-conflict", entry.Action);
        Assert.Equal("allowed", entry.Outcome);
        Assert.Equal(correlationId, entry.CorrelationId);
    }

    [Fact]
    public void ConflictReview_WithoutRequiredPermission_IsDenied_AndConflictStaysUnresolved()
    {
        var auditSink = new InMemoryAuditSink();
        var authService = new TenantAuthorizationService(auditSink);
        using var store = new BranchSyncStore(ConnectionString);
        var service = new BranchNodeService(store, authService, auditSink);

        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var local = SaleEnvelope(Guid.NewGuid(), organizationId, branchId, aggregateId, Guid.NewGuid(), aggregateVersion: 3);
        var remote = local with { OperationId = Guid.NewGuid() };
        var conflict = service.DetectConflict(local, remote);

        var unprivilegedReviewer = new UserAccount(
            Guid.NewGuid(),
            organizationId,
            new[] { branchId },
            new[] { new Role("no-permissions", Permission.None) });

        var resolved = service.ReviewConflict(conflict, unprivilegedReviewer, decision: "keep-local", Guid.NewGuid());

        Assert.False(resolved);
        Assert.Null(conflict.Resolution);
    }

    // --- PostgreSQL-RLS-equivalent default-deny / cross-org denial ---------

    [Fact]
    public void CloudInbox_DefaultDenies_CrossOrganizationEnvelope()
    {
        var store = new CloudInboxStore();
        var orgAScope = new CloudTenantScope(Guid.NewGuid());
        var orgBEnvelope = SaleEnvelope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var result = store.TryApplyInbound(orgAScope, orgBEnvelope);

        Assert.Equal(InboundApplyOutcome.Denied, result.Outcome);
        Assert.Empty(store.GetInboxFor(orgAScope));
    }

    [Fact]
    public void CloudInbox_NonOwnerRole_NeverSeesAnotherOrganizationsRows()
    {
        var store = new CloudInboxStore();
        var orgAScope = new CloudTenantScope(Guid.NewGuid());
        var orgBScope = new CloudTenantScope(Guid.NewGuid());
        var orgAOperation = Guid.NewGuid();
        var orgAEnvelope = SaleEnvelope(orgAOperation, orgAScope.OrganizationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var applied = store.TryApplyInbound(orgAScope, orgAEnvelope);
        Assert.Equal(InboundApplyOutcome.Applied, applied.Outcome);

        // Org B's runtime-role scope must never see or acknowledge Org A's row.
        Assert.Empty(store.GetInboxFor(orgBScope));
        Assert.Null(store.GetStatus(orgBScope, orgAOperation));
        Assert.False(store.Acknowledge(orgBScope, orgAOperation));

        // Org A's own scope still sees and can acknowledge it.
        Assert.Single(store.GetInboxFor(orgAScope));
        Assert.True(store.Acknowledge(orgAScope, orgAOperation));
    }

    [Fact]
    public void CloudSyncReceiver_DuplicateDelivery_AcknowledgesExistingResult_WithoutSecondEffect()
    {
        var store = new CloudInboxStore();
        var receiver = new CloudSyncReceiver(store);
        var scope = new CloudTenantScope(Guid.NewGuid());
        var envelope = SaleEnvelope(Guid.NewGuid(), scope.OrganizationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var first = receiver.Receive(scope, envelope);
        var second = receiver.Receive(scope, envelope);

        Assert.Equal(InboundApplyOutcome.Applied, first.Outcome);
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, second.Outcome);
        Assert.Single(store.GetInboxFor(scope));
    }
}
