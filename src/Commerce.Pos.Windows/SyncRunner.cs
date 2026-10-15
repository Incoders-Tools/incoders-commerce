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
    private readonly DiscountPinReplicaClient? _discountPinReplicaClient;
    private readonly StockReplicaClient? _stockReplicaClient;
    private readonly PriceListsReplicaClient? _priceListsReplicaClient;
    private readonly OrganizationSettingsReplicaClient? _organizationSettingsReplicaClient;
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
        Func<DevicePairing> pairingAccessor,
        DiscountPinReplicaClient? discountPinReplicaClient = null,
        StockReplicaClient? stockReplicaClient = null,
        PriceListsReplicaClient? priceListsReplicaClient = null,
        OrganizationSettingsReplicaClient? organizationSettingsReplicaClient = null)
    {
        _priceListsReplicaClient = priceListsReplicaClient;
        _organizationSettingsReplicaClient = organizationSettingsReplicaClient;
        _discountPinReplicaClient = discountPinReplicaClient;
        _stockReplicaClient = stockReplicaClient;
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
            await PullDiscountPinAsync(pairing);
            await PullStockAsync(pairing);
            await PullPriceListsAsync(pairing);
            await PullOrganizationSettingsAsync(pairing);

            var pending = _store.GetPendingOutbox(pairing.BranchId);
            if (pending.Count == 0)
            {
                return new SyncRunResult(PosMessages.SyncNothingPending, CredentialRejected: false);
            }

            var succeeded = 0;
            var failed = 0;
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
                    PosLog.Warning("Sync", $"Operation {envelope.OperationId} was not sent; it stays pending.", ex);
                    failed++;
                    continue;
                }

                if (pushResult.Success)
                {
                    _branchNodeService.Acknowledge(envelope.OperationId);
                    succeeded++;
                }
                else
                {
                    PosLog.Warning("Sync", $"Operation {envelope.OperationId} was rejected: {pushResult.Error}; it stays pending.");
                    failed++;
                    credentialRejected |= pushResult.CredentialWasRejected;
                    _store.RecordAttemptFailure(envelope.OperationId, pushResult.Error ?? "unknown error");
                }
            }

            var summary = failed == 0
                ? PosMessages.SyncSucceeded(succeeded)
                : PosMessages.SyncPartiallyFailed(succeeded, failed);

            if (credentialRejected)
            {
                summary += $"\n\n{PosMessages.TerminalNotRecognized}";
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
                row.IdentificationCode, row.QuantityBehavior, row.UnitId, row.UnitPrice, row.EffectiveFrom, row.UpdatedAtUtc,
                row.CategoryId, row.CategoryName, row.CategoryIconKey))
            .ToList();

        _store.ApplyCatalogPriceSync(replicaItems, outcome.RemovedPresentationIds, outcome.ServerTimeUtc.Value);
    }

    /// <summary>
    /// Refreshes the stock replica (cursor channel `stock`). A failed pull leaves the replica and cursor byte-identical:
    /// the last known stock stays usable offline, labelled with the time of its last successful refresh.
    /// </summary>
    /// <summary>
    /// customer-price-lists T4: replaces the price list replica with the cloud snapshot (every list's prices and rate
    /// components, the lists, each customer's list and discount). A failed pull leaves the replica and cursor as they were, so a
    /// stale replica keeps pricing offline.
    /// </summary>
    private async Task PullPriceListsAsync(DevicePairing pairing)
    {
        if (_priceListsReplicaClient is null)
        {
            return;
        }

        var outcome = await _priceListsReplicaClient.PullAsync(pairing.DeviceToken);
        if (!outcome.Success || outcome.Snapshot is not { } snapshot)
        {
            return;
        }

        _store.ApplyPriceListsSync(
            new PriceListsReplicaSnapshot(
                pairing.OrganizationId, snapshot.Lists, snapshot.Entries, snapshot.RateSets, snapshot.CustomerPriceLists,
                snapshot.OrganizationDefaultCustomerPriceListId, snapshot.CustomerDiscounts, snapshot.CustomerBalances,
                snapshot.CustomerPaymentTerms, snapshot.DefaultCustomerPaymentTermsDays, snapshot.Categories),
            snapshot.ServerTimeUtc);
    }

    private async Task PullStockAsync(DevicePairing pairing)
    {
        if (_stockReplicaClient is null)
        {
            return;
        }

        var since = _store.GetStockCursor() ?? DateTimeOffset.MinValue;
        var outcome = await _stockReplicaClient.PullAsync(since, pairing.DeviceToken);
        if (!outcome.Success || outcome.Items is null || outcome.ServerTimeUtc is null)
        {
            return;
        }

        _store.ApplyStockSync(
            outcome.Items.Select(row => new StockReplicaItem(row.PresentationId, row.OnHand)).ToList(),
            outcome.ServerTimeUtc.Value);
    }

    /// <summary>
    /// operator-ux-adjustments T5: refreshes the organization's quantity decimal separator, and (organization-account-
    /// standing T8) its account standing inputs. A failed pull (or an unknown value) leaves the last known ones stored, so
    /// quantities keep their format and the notice keeps counting offline.
    /// </summary>
    private async Task PullOrganizationSettingsAsync(DevicePairing pairing)
    {
        if (_organizationSettingsReplicaClient is null)
        {
            return;
        }

        var outcome = await _organizationSettingsReplicaClient.PullAsync(pairing.DeviceToken);
        if (outcome is { Success: true, QuantityDecimalSeparator: { } separator })
        {
            _store.ApplyOrganizationSettings(separator);
        }

        // organization-account-standing T8: the standing inputs ride on the same answer; absent (older server) or a
        // failed pull leaves the last known ones, so the footer notice keeps counting offline.
        if (outcome is { Success: true, AccountStanding: { } standing })
        {
            _store.ApplyAccountStanding(standing);
        }
    }

    /// <summary>
    /// Refreshes the cached branch discount PIN verifier. A failed pull leaves
    /// the cache exactly as it was, so discounts keep verifying offline; a
    /// successful answer replaces it (or clears it when the branch has none).
    /// </summary>
    private async Task PullDiscountPinAsync(DevicePairing pairing)
    {
        if (_discountPinReplicaClient is null)
        {
            return;
        }

        var outcome = await _discountPinReplicaClient.PullAsync(pairing.DeviceToken);
        if (outcome.Success)
        {
            _store.ApplyDiscountPin(pairing.BranchId, outcome.ToReplica(pairing.BranchId));
        }
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
