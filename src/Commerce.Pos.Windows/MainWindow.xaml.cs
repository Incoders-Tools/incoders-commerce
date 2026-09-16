using System.Globalization;
using System.Windows;
using Commerce.Application.Access;
using Commerce.BranchNode;
using Commerce.Domain.Sync;

namespace Commerce.Pos.Windows;

/// <summary>
/// Minimal but functionally real shell (design.md "POS Shell with In-Process
/// Branch Node"): proves the app launches, BranchNode initializes in-process,
/// an offline sale lands in the local SQLite branch.db, and sync against
/// Cloud.Api is visibly attempted and its outcome shown. Not production UX —
/// a walking-skeleton proof, matching commerce-foundation's own precedent.
/// </summary>
public partial class MainWindow : Window
{
    private readonly BranchSyncStore _store;
    private readonly BranchNodeService _branchNodeService;
    private readonly CloudSyncClient _syncClient;
    private readonly LocalInstallationRecord _identity;

    public MainWindow(
        BranchSyncStore store,
        BranchNodeService branchNodeService,
        CloudSyncClient syncClient,
        InstallationIdentityService installationIdentityService,
        LocalInstallationStore localInstallationStore)
    {
        InitializeComponent();

        _store = store;
        _branchNodeService = branchNodeService;
        _syncClient = syncClient;
        _identity = localInstallationStore.LoadOrCreate(installationIdentityService);

        IdentityText.Text =
            $"Organization: {_identity.OrganizationId}\n" +
            $"Branch: {_identity.BranchId}\n" +
            $"Installation: {_identity.InstallationId}";

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var status = _branchNodeService.GetStatus(_identity.BranchId, isOffline: true);
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

        var result = _branchNodeService.CompleteOfflineSale(
            organizationId: _identity.OrganizationId,
            branchId: _identity.BranchId,
            actorId: _identity.InstallationId,
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

        var pending = _store.GetPendingOutbox(_identity.BranchId);
        if (pending.Count == 0)
        {
            SyncResultText.Text = "Nothing pending to sync.";
            return;
        }

        var succeeded = 0;
        var failures = new List<string>();

        foreach (var envelope in pending)
        {
            var pushResult = await _syncClient.PushAsync(envelope, _identity.OrganizationId, _identity.InstallationId);
            if (pushResult.Success)
            {
                _branchNodeService.Acknowledge(envelope.OperationId);
                succeeded++;
            }
            else
            {
                failures.Add($"{envelope.OperationId}: {pushResult.Error}");
            }
        }

        SyncResultText.Text = failures.Count == 0
            ? $"Synced {succeeded} operation(s) successfully."
            : $"Synced {succeeded} operation(s); {failures.Count} failed:\n{string.Join("\n", failures)}";

        RefreshStatus();
    }
}
