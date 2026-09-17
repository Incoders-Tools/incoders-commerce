using System.Globalization;
using System.Windows;
using Commerce.BranchNode;
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
    private readonly LocalInstallationStore _localInstallationStore;
    private readonly Guid _installationId;
    private DevicePairing _pairing;

    public MainWindow(
        BranchSyncStore store,
        BranchNodeService branchNodeService,
        CloudSyncClient syncClient,
        DevicePairingClient pairingClient,
        LocalInstallationStore localInstallationStore,
        LocalInstallationRecord identity)
    {
        InitializeComponent();

        _store = store;
        _branchNodeService = branchNodeService;
        _syncClient = syncClient;
        _pairingClient = pairingClient;
        _localInstallationStore = localInstallationStore;
        _installationId = identity.InstallationId;
        _pairing = identity.Pairing
            ?? throw new InvalidOperationException("MainWindow requires an already-paired identity; App.xaml.cs must pair first.");

        RefreshIdentityText();
        RefreshStatus();
    }

    private void RefreshIdentityText()
    {
        IdentityText.Text =
            $"Organization: {_pairing.OrganizationId}\n" +
            $"Branch: {_pairing.BranchName} ({_pairing.BranchId})\n" +
            $"Operator: {_pairing.OperatorEmail}\n" +
            $"Installation: {_installationId}";
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
            actorId: _installationId,
            saleId: Guid.NewGuid(),
            totalAmount: amount,
            operationId: Guid.NewGuid(),
            correlationId: Guid.NewGuid());

        SaleResultText.Text = result.WasNewlyCommitted
            ? $"Committed sale {result.Effect.SaleId} for {result.Effect.TotalAmount:C} to branch.db."
            : $"Sale {result.Effect.SaleId} was already committed (idempotent replay).";

        RefreshStatus();
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        SyncResultText.Text = "Syncing...";

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
}
