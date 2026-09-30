using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// Handles both first-run pairing and re-pairing (design.md "File Changes").
/// A collapsed branch picker is revealed only when the server responds
/// `branch-selection-required`; step 2 re-posts the SAME credentials plus
/// the chosen branch id — there is no pairing-ticket registry, so re-posting
/// full credentials is the deliberate stateless design (design.md "Pairing
/// flow").
///
/// While a request is in flight the whole form (<c>FormPanel</c>) is disabled,
/// a progress bar is shown, and the window cannot be closed; every failure is
/// logged by the client and shown as a friendly Spanish message
/// (<see cref="PosMessages"/>).
/// </summary>
public partial class PairingWindow : Window
{
    private readonly DevicePairingClient _pairingClient;
    private readonly LocalInstallationStore _localInstallationStore;
    private readonly Guid _installationId;
    private readonly BusyController _busy;

    private string? _pendingEmail;
    private string? _pendingPassword;
    private bool _completed;

    public LocalInstallationRecord? PairedRecord { get; private set; }

    public PairingWindow(DevicePairingClient pairingClient, LocalInstallationStore localInstallationStore, Guid installationId)
    {
        InitializeComponent();
        _pairingClient = pairingClient;
        _localInstallationStore = localInstallationStore;
        _installationId = installationId;
        _busy = new BusyController(ApplyBusy, nameof(PairingWindow), message => StatusText.Text = message);

        // A request in flight must finish before the window goes away, otherwise
        // its result would land on a closed window.
        Closing += (_, e) => e.Cancel = _busy.IsBusy && !_completed;
    }

    private void ApplyBusy(bool busy, string? text)
    {
        FormPanel.IsEnabled = !busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? string.Empty;
        Cursor = busy ? Cursors.Wait : null;
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailTextBox.Text;
        var password = PasswordBox.Password;
        await _busy.RunAsync(PosMessages.Pairing, () => AttemptPairAsync(email, password, branchId: null));
    }

    private async void PairButton_Click(object sender, RoutedEventArgs e)
    {
        if (BranchListBox.SelectedItem is not DeviceBranchOptionDto selected || _pendingEmail is null || _pendingPassword is null)
        {
            StatusText.Text = PosMessages.SelectBranchFirst;
            return;
        }

        var email = _pendingEmail;
        var password = _pendingPassword;
        await _busy.RunAsync(PosMessages.Pairing, () => AttemptPairAsync(email, password, selected.Id));
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
                _completed = true;
                DialogResult = true;
                Close();
                break;

            case PairingOutcomeKind.BranchSelectionRequired:
                _pendingEmail = email;
                _pendingPassword = password;
                BranchListBox.ItemsSource = outcome.Branches;
                BranchSelectionPanel.Visibility = Visibility.Visible;
                StatusText.Text = PosMessages.MultipleBranchesFound;
                break;

            case PairingOutcomeKind.InvalidCredentials:
                StatusText.Text = PosMessages.InvalidCredentials;
                break;

            case PairingOutcomeKind.Failed:
            default:
                StatusText.Text = outcome.ErrorMessage ?? PosMessages.PairingFailed;
                break;
        }
    }
}
