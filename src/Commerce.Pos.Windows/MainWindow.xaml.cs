using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Domain.Identity;
using Commerce.Domain.Sync;

namespace Commerce.Pos.Windows;

/// <summary>
/// Minimal but functionally real shell (design.md "Data Flow"): proves the
/// app launches, BranchNode initializes in-process, an offline sale lands in
/// the local SQLite branch.db, and sync against Cloud.Api is visibly
/// attempted and its outcome shown.
///
/// Sync-blocked-not-sales-blocked (structural, design.md): `DeviceToken` is
/// read in exactly ONE expression — the `PushAsync` call inside
/// <see cref="SyncButton_Click"/>. <see cref="CommitSaleButton_Click"/> never
/// reads it and never calls <see cref="CloudSyncClient"/> — a revoked
/// credential therefore cannot reach the local-write path even in principle.
/// </summary>
public partial class MainWindow : Window
{
    private readonly BranchSyncStore _store;
    private readonly BranchNodeService _branchNodeService;
    private readonly CloudSyncClient _syncClient;
    private readonly DevicePairingClient _pairingClient;
    private readonly OperatorProvisioningClient _operatorProvisioningClient;
    private readonly LocalInstallationStore _localInstallationStore;
    private readonly LocalOperatorStore _localOperatorStore;
    private readonly CurrentOperator _currentOperator;
    private readonly CustomerReplicaClient _customerReplicaClient;
    private readonly CatalogPriceReplicaClient _catalogPriceReplicaClient;
    private readonly PricingResolutionService _pricingResolutionService;
    private readonly Func<CustomerAdminClient> _customerAdminClientFactory;
    private readonly Guid _installationId;
    private readonly ObservableCollection<ScannedSaleLineViewModel> _scannedLines = new();
    private DevicePairing _pairing;

    public MainWindow(
        BranchSyncStore store,
        BranchNodeService branchNodeService,
        CloudSyncClient syncClient,
        DevicePairingClient pairingClient,
        OperatorProvisioningClient operatorProvisioningClient,
        LocalInstallationStore localInstallationStore,
        LocalOperatorStore localOperatorStore,
        CurrentOperator currentOperator,
        CustomerReplicaClient customerReplicaClient,
        CatalogPriceReplicaClient catalogPriceReplicaClient,
        PricingResolutionService pricingResolutionService,
        Func<CustomerAdminClient> customerAdminClientFactory,
        LocalInstallationRecord identity)
    {
        InitializeComponent();

        _store = store;
        _branchNodeService = branchNodeService;
        _syncClient = syncClient;
        _pairingClient = pairingClient;
        _operatorProvisioningClient = operatorProvisioningClient;
        _localInstallationStore = localInstallationStore;
        _localOperatorStore = localOperatorStore;
        _currentOperator = currentOperator;
        _customerReplicaClient = customerReplicaClient;
        _catalogPriceReplicaClient = catalogPriceReplicaClient;
        _pricingResolutionService = pricingResolutionService;
        _customerAdminClientFactory = customerAdminClientFactory;
        _installationId = identity.InstallationId;
        _pairing = identity.Pairing
            ?? throw new InvalidOperationException("MainWindow requires an already-paired identity; App.xaml.cs must pair first.");

        ScannedLinesListView.ItemsSource = _scannedLines;

        RefreshIdentityText();
        RefreshStatus();
        RefreshCustomerPicker();
        RefreshScannedTotal();
        RefreshCatalogFreshness();
    }

    private void RefreshIdentityText()
    {
        var operatorLine = _currentOperator.Value is { } op
            ? $"Current operator: {op.Email}"
            : "Current operator: none";

        IdentityText.Text =
            $"Organization: {_pairing.OrganizationId}\n" +
            $"Branch: {_pairing.BranchName} ({_pairing.BranchId})\n" +
            $"Operator: {_pairing.OperatorEmail}\n" +
            $"Installation: {_installationId}\n" +
            $"{operatorLine}";

        // pos-operator-session spec "Admin-Only Customer Management Screen
        // Gated by Current Operator Role": no operator identified, or an
        // operator without ManageUsers, hides the button entirely. This is a
        // UX affordance only — the server re-checks ManageUsers on every
        // /customers call regardless (design.md "Desktop authorization for
        // customer create/edit").
        ManageCustomersButton.Visibility =
            _currentOperator.Value is { } current && ((Permission)current.Permissions).HasFlag(Permission.ManageUsers)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void RefreshStatus()
    {
        var status = _branchNodeService.GetStatus(_pairing.BranchId, isOffline: true);
        StatusText.Text =
            $"Pending outbox operations: {status.PendingOperationCount}\n" +
            $"Last acknowledged: {(status.LastAcknowledgedUtc?.ToString("O") ?? "never")}";
    }

    private void CommitSaleButton_Click(object sender, RoutedEventArgs e)
    {
        // Task 7.5: mutual exclusion (design.md "POS: two explicit buttons,
        // not a mode toggle") — a mixed sale would need per-line provenance,
        // which is out of scope, so committing manually while scanned lines
        // are pending is blocked with an explicit message rather than
        // silently mixing the two flows in one transaction.
        if (_scannedLines.Count > 0)
        {
            SaleResultText.Text = "Cannot commit a manual sale while scanned lines are pending — commit the scanned sale or clear the scan list first.";
            return;
        }

        if (!decimal.TryParse(AmountTextBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            SaleResultText.Text = "Invalid amount.";
            return;
        }

        // Zero references to DeviceToken, zero HTTP, zero credential validity
        // check — a fully revoked device credential never reaches this path.
        var result = _branchNodeService.CompleteOfflineSale(
            organizationId: _pairing.OrganizationId,
            branchId: _pairing.BranchId,
            actorId: _currentOperator.ResolveActorId(_installationId),
            saleId: Guid.NewGuid(),
            totalAmount: amount,
            operationId: Guid.NewGuid(),
            correlationId: Guid.NewGuid());

        SaleResultText.Text = result.WasNewlyCommitted
            ? $"Committed sale {result.Effect.SaleId} for {result.Effect.TotalAmount:C} to branch.db."
            : $"Sale {result.Effect.SaleId} was already committed (idempotent replay).";

        RefreshStatus();

        // commerce-customer-identity follow-up: the picker is optional and
        // resets to walk-in after every commit — anonymous counter sale stays
        // the fastest, zero-friction default for the NEXT sale too.
        CustomerPickerComboBox.SelectedIndex = 0;
    }

    /// <summary>
    /// Optional wholesale/delivery customer attribution (commerce-customer-
    /// identity follow-up, verify-report CRITICAL: "Customer becomes
    /// selectable on POS after sync"). Defaults to
    /// <see cref="SaleCustomerPicker.WalkInLabel"/> at index 0 — anonymous
    /// walk-in retail stays the default, zero-friction path;
    /// <see cref="CommitSaleButton_Click"/> never requires a selection.
    /// </summary>
    private void RefreshCustomerPicker()
    {
        var selectedCustomerId = (CustomerPickerComboBox.SelectedItem as SaleCustomerPickerItem)?.CustomerId;

        var items = SaleCustomerPicker.BuildItems(_store.ListCustomers());
        CustomerPickerComboBox.ItemsSource = items;
        CustomerPickerComboBox.SelectedIndex = selectedCustomerId is null
            ? 0
            : Math.Max(0, items.ToList().FindIndex(i => i.CustomerId == selectedCustomerId));
    }

    /// <summary>
    /// Task 7.4/7.6: keyboard-wedge scan handler (design.md "POS scan-to-sell"
    /// — `Enter` -> lookup). Looks up the code against the local
    /// `catalog_replica`/`price_replica` cache; an unknown code or a known
    /// code with no locally effective price produces an explicit, visible,
    /// non-appending message — never a zero-priced line substitute.
    /// Re-scanning an already-added code merges into that line by
    /// re-resolving at the accumulated quantity, so rounding always comes
    /// from <see cref="PricingResolutionService"/>, never hand computed here.
    /// </summary>
    private async void ScanCodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var code = ScanCodeTextBox.Text.Trim();
        ScanCodeTextBox.Clear();
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var item = _store.FindByIdentificationCode(_pairing.OrganizationId, code);
        if (item is null)
        {
            ScanMessageText.Text = $"Code {code} is not in this terminal's catalog.";
            ScanCodeTextBox.Focus();
            return;
        }

        var existingIndex = _scannedLines.ToList().FindIndex(l => l.PresentationId == item.PresentationId);
        var newQuantity = (existingIndex >= 0 ? _scannedLines[existingIndex].Quantity : 0m) + 1m;
        var effectiveOn = DateOnly.FromDateTime(DateTime.UtcNow);

        var outcome = await _pricingResolutionService.ResolveAsync(
            item.PresentationId, newQuantity, discountPercentage: null, effectiveOn, CancellationToken.None);

        if (outcome is not PriceResolutionOutcome.Resolved resolved)
        {
            ScanMessageText.Text = $"No effective price for {item.PresentationName} on {effectiveOn:yyyy-MM-dd} — use the manual path or sync.";
            ScanCodeTextBox.Focus();
            return;
        }

        ScanMessageText.Text = string.Empty;
        var line = new ScannedSaleLineViewModel(
            item.PresentationId, item.IdentificationCode, item.ProductName, item.PresentationName,
            newQuantity, resolved.UnitNetPrice, resolved.LineTotal);

        if (existingIndex >= 0)
        {
            _scannedLines[existingIndex] = line;
        }
        else
        {
            _scannedLines.Add(line);
        }

        RefreshScannedTotal();
        ScanCodeTextBox.Focus();
    }

    private void RefreshScannedTotal()
    {
        var total = _scannedLines.Sum(l => l.LineTotal);
        ScannedTotalText.Text = total.ToString("C", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Task 7.4: "Commit scanned sale" — writes `sale_effects(sale_kind=
    /// 'Scanned') + sale_lines` atomically via
    /// <see cref="BranchNodeService.CompleteScannedSale"/>, distinct from
    /// <see cref="CommitSaleButton_Click"/>'s manual-total path.
    /// </summary>
    private void CommitScannedSaleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scannedLines.Count == 0)
        {
            ScanMessageText.Text = "Scan at least one item before committing.";
            return;
        }

        var saleId = Guid.NewGuid();
        var lines = _scannedLines
            .Select((vm, index) => new SaleLine(
                saleId, index + 1, vm.PresentationId, vm.IdentificationCode, vm.ProductName, vm.PresentationName,
                vm.Quantity, vm.UnitPrice, vm.LineTotal))
            .ToList();
        var total = lines.Sum(l => l.LineTotal);

        var result = _branchNodeService.CompleteScannedSale(
            organizationId: _pairing.OrganizationId,
            branchId: _pairing.BranchId,
            actorId: _currentOperator.ResolveActorId(_installationId),
            saleId: saleId,
            lines: lines,
            totalAmount: total,
            operationId: Guid.NewGuid(),
            correlationId: Guid.NewGuid());

        SaleResultText.Text = result.WasNewlyCommitted
            ? $"Committed scanned sale {result.Effect.SaleId} for {result.Effect.TotalAmount:C} to branch.db."
            : $"Sale {result.Effect.SaleId} was already committed (idempotent replay).";

        _scannedLines.Clear();
        RefreshScannedTotal();
        RefreshStatus();
        CustomerPickerComboBox.SelectedIndex = 0;
    }

    /// <summary>
    /// Task 7.7: ADR-002's EXISTING freshness policy, verbatim — reuses
    /// <see cref="CachedOperator.Ttl"/> (14 days), the one freshness
    /// threshold already implemented in this app for cached admin data, read
    /// against the `catalog-prices` sync cursor age. No new TTL constant, no
    /// timer, no second mechanism. A stale cache never blocks a sale (ADR-002
    /// keeps the branch authoritative for its own sale) — this only makes
    /// staleness visible.
    /// </summary>
    private void RefreshCatalogFreshness()
    {
        var cursor = _store.GetCatalogPricesCursor();
        if (cursor is null || DateTimeOffset.UtcNow - cursor.Value <= CachedOperator.Ttl)
        {
            StaleCatalogBanner.Visibility = Visibility.Collapsed;
            return;
        }

        StaleCatalogBannerText.Text =
            $"Catalog/price cache last synced {cursor.Value:O} — older than the {CachedOperator.Ttl.TotalDays:0}-day freshness window. " +
            "Prices may be stale; sales are not blocked.";
        StaleCatalogBanner.Visibility = Visibility.Visible;
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        SyncResultText.Text = "Syncing...";

        // Reconciliation runs FIRST, before the pending.Count == 0 early
        // return (design.md "Staleness TTL and reconciliation trigger"), so
        // pressing Sync with an empty outbox still reconciles operator
        // staleness.
        await ReconcileOperatorsAsync();

        // Minimum-viable cloud->local customer pull (design.md "BranchNode
        // cloud->local customer replication"), also placed BEFORE the
        // pending.Count == 0 early return so pressing Sync with an empty
        // outbox still refreshes customer availability for offline selection.
        await PullCustomersAsync();
        RefreshCustomerPicker();

        // Minimum-viable cloud->local catalog+price pull (commerce-pricing-
        // engine design.md "BranchNode replication"), same position and same
        // shape as PullCustomersAsync above, and still BEFORE the
        // pending.Count == 0 early return so pressing Sync with an empty
        // outbox still refreshes the offline scan catalog.
        await PullCatalogPricesAsync();
        RefreshCatalogFreshness();

        var pending = _store.GetPendingOutbox(_pairing.BranchId);
        if (pending.Count == 0)
        {
            SyncResultText.Text = "Nothing pending to sync.";
            return;
        }

        var succeeded = 0;
        var failures = new List<string>();
        var credentialRejected = false;

        foreach (var envelope in pending)
        {
            var pushResult = await _syncClient.PushAsync(envelope, _pairing.DeviceToken);
            if (pushResult.Success)
            {
                _branchNodeService.Acknowledge(envelope.OperationId);
                succeeded++;
            }
            else
            {
                failures.Add($"{envelope.OperationId}: {pushResult.Error}");
                credentialRejected |= pushResult.CredentialWasRejected;
            }
        }

        var summary = failures.Count == 0
            ? $"Synced {succeeded} operation(s) successfully."
            : $"Synced {succeeded} operation(s); {failures.Count} failed:\n{string.Join("\n", failures)}";

        if (credentialRejected)
        {
            summary += "\n\nDevice credential rejected — click \"Re-pair terminal\" to continue syncing.";
        }

        SyncResultText.Text = summary;
        RefreshStatus();
    }

    /// <summary>
    /// Always-visible button (design.md "Re-pairing": NOT an automatic
    /// interrupt-the-sale modal) opening <see cref="PairingWindow"/> modally
    /// and reloading identity on success. Never blocks
    /// <see cref="CommitSaleButton_Click"/> — the operator chooses when to
    /// re-pair.
    /// </summary>
    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        var pairingWindow = new PairingWindow(_pairingClient, _localInstallationStore, _installationId)
        {
            Owner = this
        };

        var result = pairingWindow.ShowDialog();
        if (result == true && pairingWindow.PairedRecord?.Pairing is not null)
        {
            _pairing = pairingWindow.PairedRecord.Pairing;
            RefreshIdentityText();
            RefreshStatus();
            SyncResultText.Text = "Re-paired. Previously pending outbox items will flush on the next sync.";
        }
    }

    /// <summary>
    /// Same shape as <see cref="RepairButton_Click"/> (design.md "One window
    /// for three flows"): always visible, explicit action, never an
    /// interrupt, never a precondition of <see cref="CommitSaleButton_Click"/>.
    /// Reopens <see cref="OperatorLoginWindow"/> for provisioning a new
    /// operator or PIN-entry for an already-cached different operator.
    /// </summary>
    private void SwitchOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        var operatorLoginWindow = new OperatorLoginWindow(_operatorProvisioningClient, _localOperatorStore, _pairing.DeviceToken)
        {
            Owner = this
        };

        var result = operatorLoginWindow.ShowDialog();
        if (result == true && operatorLoginWindow.ActiveOperator is not null)
        {
            _currentOperator.Set(operatorLoginWindow.ActiveOperator);
            RefreshIdentityText();
        }
    }

    /// <summary>
    /// Opens <see cref="CustomersWindow"/> modally with a FRESH
    /// <see cref="CustomerAdminClient"/> (design.md "Desktop authorization for
    /// customer create/edit"): its cookie is scoped to this one window
    /// instance and is discarded here, never persisted, never reused across
    /// opens. Button visibility is UX-only (see
    /// <see cref="RefreshIdentityText"/>) — the server re-checks
    /// <c>ManageUsers</c> on every call the window makes.
    /// </summary>
    private void ManageCustomersButton_Click(object sender, RoutedEventArgs e)
    {
        using var adminClient = _customerAdminClientFactory();
        var customersWindow = new CustomersWindow(adminClient)
        {
            Owner = this
        };
        customersWindow.ShowDialog();
    }

    /// <summary>
    /// Minimum-viable cloud->local customer pull (design.md "BranchNode
    /// cloud->local customer replication"): pulls changes since the last
    /// cursor, then applies upserts, disabled-id removals, and the cursor
    /// advance in ONE atomic <see cref="BranchSyncStore.ApplyCustomerSync"/>
    /// transaction. A failure (unreachable, non-2xx, empty body) is
    /// non-fatal: the replica and cursor stay byte-identical and the sale
    /// path is never blocked (the SyncButton failure precedent).
    /// </summary>
    private async Task PullCustomersAsync()
    {
        var since = _store.GetCustomersCursor() ?? DateTimeOffset.MinValue;
        var outcome = await _customerReplicaClient.PullAsync(since, _pairing.DeviceToken);
        if (!outcome.Success || outcome.Customers is null || outcome.DisabledIds is null || outcome.ServerTimeUtc is null)
        {
            return;
        }

        var replicaRows = outcome.Customers
            .Select(row => new CustomerReplica(
                row.CustomerId, _pairing.OrganizationId, row.DisplayName, row.CustomerKind,
                row.TaxId, row.Phone, row.Locality, row.UpdatedAtUtc))
            .ToList();

        _store.ApplyCustomerSync(replicaRows, outcome.DisabledIds, outcome.ServerTimeUtc.Value);
    }

    /// <summary>
    /// Minimum-viable cloud->local catalog+price pull (commerce-pricing-engine
    /// design.md "BranchNode replication: one channel, not two"): pulls
    /// changes since the last cursor, then applies upserts, removed-id
    /// deletions, and the cursor advance in ONE atomic
    /// <see cref="BranchSyncStore.ApplyCatalogPriceSync"/> transaction. A
    /// failure (unreachable, non-2xx, empty body) is non-fatal: the replica
    /// and cursor stay byte-identical and the sale path is never blocked
    /// (the <see cref="PullCustomersAsync"/> precedent).
    /// </summary>
    private async Task PullCatalogPricesAsync()
    {
        var since = _store.GetCatalogPricesCursor() ?? DateTimeOffset.MinValue;
        var outcome = await _catalogPriceReplicaClient.PullAsync(since, _pairing.DeviceToken);
        if (!outcome.Success || outcome.Items is null || outcome.RemovedPresentationIds is null || outcome.ServerTimeUtc is null)
        {
            return;
        }

        var replicaItems = outcome.Items
            .Select(row => new CatalogPriceReplicaItem(
                row.PresentationId, _pairing.OrganizationId, row.ProductId, row.ProductName, row.PresentationName,
                row.IdentificationCode, row.QuantityBehavior, row.UnitId, row.UnitPrice, row.EffectiveFrom, row.UpdatedAtUtc))
            .ToList();

        _store.ApplyCatalogPriceSync(replicaItems, outcome.RemovedPresentationIds, outcome.ServerTimeUtc.Value);
    }

    /// <summary>
    /// Reconciles every cached operator against server-side status
    /// (design.md "Staleness TTL and reconciliation trigger"): "active"
    /// stamps <c>LastVerifiedUtc</c> so a stale entry revives without
    /// re-entering the server password; "inactive" removes the entry
    /// (revocation beats the timer); unreachable/401 leaves the entry
    /// untouched so the 14-day TTL still applies.
    /// </summary>
    private async Task ReconcileOperatorsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var cachedOperator in _localOperatorStore.Load())
        {
            var status = await _operatorProvisioningClient.GetStatusAsync(cachedOperator.UserId, _pairing.DeviceToken);
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
