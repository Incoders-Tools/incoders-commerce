using System.Windows;

namespace Commerce.Pos.Windows;

/// <summary>
/// Handles both first-run pairing and re-pairing (design.md "File Changes").
/// A collapsed branch picker is revealed only when the server responds
/// `branch-selection-required`; step 2 re-posts the SAME credentials plus
/// the chosen branch id — there is no pairing-ticket registry, so re-posting
/// full credentials is the deliberate stateless design (design.md "Pairing
/// flow").
/// </summary>
public partial class PairingWindow : Window
{
    private readonly DevicePairingClient _pairingClient;
    private readonly LocalInstallationStore _localInstallationStore;
    private readonly Guid _installationId;

    private string? _pendingEmail;
    private string? _pendingPassword;

    public LocalInstallationRecord? PairedRecord { get; private set; }

    public PairingWindow(DevicePairingClient pairingClient, LocalInstallationStore localInstallationStore, Guid installationId)
    {
        InitializeComponent();
        _pairingClient = pairingClient;
        _localInstallationStore = localInstallationStore;
        _installationId = installationId;
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        await AttemptPairAsync(EmailTextBox.Text, PasswordBox.Password, branchId: null);
    }

    private async void PairButton_Click(object sender, RoutedEventArgs e)
    {
        if (BranchListBox.SelectedItem is not DeviceBranchOptionDto selected || _pendingEmail is null || _pendingPassword is null)
        {
            StatusText.Text = "Select a branch first.";
            return;
        }

        await AttemptPairAsync(_pendingEmail, _pendingPassword, selected.Id);
    }

    private async Task AttemptPairAsync(string email, string password, Guid? branchId)
    {
        StatusText.Text = string.Empty;
        var outcome = await _pairingClient.PairAsync(email, password, _installationId, branchId);

        switch (outcome.Kind)
        {
            case PairingOutcomeKind.Paired:
                var record = new LocalInstallationRecord(_installationId, outcome.Pairing);
                _localInstallationStore.Save(record);
                PairedRecord = record;
                DialogResult = true;
                Close();
                break;

            case PairingOutcomeKind.BranchSelectionRequired:
                _pendingEmail = email;
                _pendingPassword = password;
                BranchListBox.ItemsSource = outcome.Branches;
                BranchSelectionPanel.Visibility = Visibility.Visible;
                StatusText.Text = "Multiple branches found. Select one to continue.";
                break;

            case PairingOutcomeKind.InvalidCredentials:
                StatusText.Text = "Invalid email or password.";
                break;

            case PairingOutcomeKind.Failed:
            default:
                StatusText.Text = outcome.ErrorMessage ?? "Pairing failed.";
                break;
        }
    }
}
