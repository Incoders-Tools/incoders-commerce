using Commerce.BranchNode;
using Commerce.Domain.Identity;

namespace Commerce.Pos.Windows;

/// <summary>
/// The result of one <see cref="SyncRunner.RunAsync"/> call. <c>null</c> from
/// <see cref="SyncRunner.RunAsync"/> itself means the call was reentrant (a
/// sweep was already in flight) and was a no-op — distinct from a completed
/// run with zero pending operations.
/// </summary>
public sealed record SyncRunResult(string Summary, bool CredentialRejected);

/// <summary>
/// Task 4.2/4.6 (commerce-sync-ownership design.md "Retry: where and how"):
/// <c>SyncButton_Click</c>'s body, extracted verbatim in behavior (reconcile
/// operators -> pull customers -> pull catalog/prices -> push pending outbox)
/// into ONE method every trigger shares — startup, the 60s
/// <see cref="SyncScheduler"/> sweep, a post-sale nudge, and the manual
/// button — with a reentrancy guard so a sweep never overlaps another.
///
/// Kept as a plain, non-<c>Window</c> class (a deliberate, reasoned deviation
/// from placing this body directly on <c>MainWindow.xaml.cs</c>): this repo's
/// test suite has no STA/WPF `Window` test host, so a testable seam is
/// required for <c>RunSyncAsyncTests</c>/<c>SyncSchedulerTests</c> to exercise
/// reentrancy and trigger-gated behavior without one. <c>MainWindow</c>
/// still owns `SyncButton_Click` and every UI update — this class contains
/// zero UI code and is never itself the operator surface.
/// </summary>
public sealed class SyncRunner
{
    private readonly BranchSyncStore _store;
    private readonly BranchNodeService _branchNodeService;
    private readonly CloudSyncClient _syncClient;
    private readonly CustomerReplicaClient _customerReplicaClient;
    private readonly CatalogPriceReplicaClient _catalogPriceReplicaClient;
    private readonly OperatorProvisioningClient _operatorProvisioningClient;
    private readonly LocalOperatorStore _localOperatorStore;
    private readonly Func<DevicePairing> _pairingAccessor;
    private int _running;

    public SyncRunner(
        BranchSyncStore store,
        BranchNodeService branchNodeService,
        CloudSyncClient syncClient,
        CustomerReplicaClient customerReplicaClient,
        CatalogPriceReplicaClient catalogPriceReplicaClient,
        OperatorProvisioningClient operatorProvisioningClient,
        LocalOperatorStore localOperatorStore,
        Func<DevicePairing> pairingAccessor)
    {
        _store = store;
        _branchNodeService = branchNodeService;
        _syncClient = syncClient;
        _customerReplicaClient = customerReplicaClient;
        _catalogPriceReplicaClient = catalogPriceReplicaClient;
        _operatorProvisioningClient = operatorProvisioningClient;
        _localOperatorStore = localOperatorStore;
        _pairingAccessor = pairingAccessor;
    }

    /// <summary>
    /// Task 4.1 (RED-proven): reentrancy-guarded via a single
    /// <see cref="Interlocked"/> flag shared by every trigger. Returns
    /// <c>null</c> when a sweep is already in flight — the caller (any
    /// trigger) treats that as a no-op, never a failure. Task 4.3: a throwing
    /// push is caught per-envelope and recorded durably
    /// (<see cref="BranchSyncStore.RecordAttemptFailure"/>); it never leaves
    /// this method as an unhandled exception, so a non-Button caller (the
    /// scheduler, the post-sale nudge) never surfaces it.
    /// </summary>
    public async Task<SyncRunResult?> RunAsync(SyncTrigger trigger)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            var pairing = _pairingAccessor();

            await ReconcileOperatorsAsync(pairing);
            await PullCustomersAsync(pairing);
            await PullCatalogPricesAsync(pairing);

            var pending = _store.GetPendingOutbox(pairing.BranchId);
            if (pending.Count == 0)
            {
                return new SyncRunResult("Nothing pending to sync.", CredentialRejected: false);
            }

            var succeeded = 0;
            var failures = new List<string>();
            var credentialRejected = false;

            foreach (var envelope in pending)
            {
                SyncPushResult pushResult;
                try
                {
                    pushResult = await _syncClient.PushAsync(envelope, pairing.DeviceToken);
                }
                catch (Exception ex)
                {
                    _store.RecordAttemptFailure(envelope.OperationId, ex.Message);
                    failures.Add($"{envelope.OperationId}: {ex.Message}");
                    continue;
                }

                if (pushResult.Success)
                {
                    _branchNodeService.Acknowledge(envelope.OperationId);
                    succeeded++;
                }
                else
                {
                    failures.Add($"{envelope.OperationId}: {pushResult.Error}");
                    credentialRejected |= pushResult.CredentialWasRejected;
                    _store.RecordAttemptFailure(envelope.OperationId, pushResult.Error ?? "unknown error");
                }
            }

            var summary = failures.Count == 0
                ? $"Synced {succeeded} operation(s) successfully."
                : $"Synced {succeeded} operation(s); {failures.Count} failed:\n{string.Join("\n", failures)}";

            if (credentialRejected)
            {
                summary += "\n\nDevice credential rejected — click \"Re-pair terminal\" to continue syncing.";
            }

            return new SyncRunResult(summary, credentialRejected);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task PullCustomersAsync(DevicePairing pairing)
    {
        var since = _store.GetCustomersCursor() ?? DateTimeOffset.MinValue;
        var outcome = await _customerReplicaClient.PullAsync(since, pairing.DeviceToken);
        if (!outcome.Success || outcome.Customers is null || outcome.DisabledIds is null || outcome.ServerTimeUtc is null)
        {
            return;
        }

        var replicaRows = outcome.Customers
            .Select(row => new CustomerReplica(
                row.CustomerId, pairing.OrganizationId, row.DisplayName, row.CustomerKind,
                row.TaxId, row.Phone, row.Locality, row.UpdatedAtUtc))
            .ToList();

        _store.ApplyCustomerSync(replicaRows, outcome.DisabledIds, outcome.ServerTimeUtc.Value);
    }

    private async Task PullCatalogPricesAsync(DevicePairing pairing)
    {
        var since = _store.GetCatalogPricesCursor() ?? DateTimeOffset.MinValue;
        var outcome = await _catalogPriceReplicaClient.PullAsync(since, pairing.DeviceToken);
        if (!outcome.Success || outcome.Items is null || outcome.RemovedPresentationIds is null || outcome.ServerTimeUtc is null)
        {
            return;
        }

        var replicaItems = outcome.Items
            .Select(row => new CatalogPriceReplicaItem(
                row.PresentationId, pairing.OrganizationId, row.ProductId, row.ProductName, row.PresentationName,
                row.IdentificationCode, row.QuantityBehavior, row.UnitId, row.UnitPrice, row.EffectiveFrom, row.UpdatedAtUtc))
            .ToList();

        _store.ApplyCatalogPriceSync(replicaItems, outcome.RemovedPresentationIds, outcome.ServerTimeUtc.Value);
    }

    private async Task ReconcileOperatorsAsync(DevicePairing pairing)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var cachedOperator in _localOperatorStore.Load())
        {
            var status = await _operatorProvisioningClient.GetStatusAsync(cachedOperator.UserId, pairing.DeviceToken);
            switch (status)
            {
                case OperatorStatusOutcome.Active:
                    _localOperatorStore.TouchVerified(cachedOperator.UserId, now);
                    break;
                case OperatorStatusOutcome.Inactive:
                    _localOperatorStore.Remove(cachedOperator.UserId);
                    break;
                case OperatorStatusOutcome.Unreachable:
                default:
                    break;
            }
        }
    }
}
