using System.Windows;

namespace Commerce.Pos.Windows;

/// <summary>
/// Serves all three operator-login flows (design.md "One window for three
/// flows"): initial online provisioning when no operator is cached yet,
/// offline PIN-entry picker when one or more operators are cached, and
/// auto-select when exactly one non-stale entry exists. Mirrors
/// <see cref="PairingWindow"/>'s exact shape (ctor-injected client + store,
/// <see cref="DialogResult"/>, <see cref="ActiveOperator"/>) and
/// <c>BranchSelectionPanel</c>'s collapsed-panel-reveal idiom for the
/// "Add another operator" panel.
/// </summary>
public partial class OperatorLoginWindow : Window
{
    private readonly OperatorProvisioningClient _provisioningClient;
    private readonly LocalOperatorStore _operatorStore;
    private readonly string _deviceToken;

    private IReadOnlyList<CachedOperator> _nonStaleOperators = [];

    public CachedOperator? ActiveOperator { get; private set; }

    public OperatorLoginWindow(OperatorProvisioningClient provisioningClient, LocalOperatorStore operatorStore, string deviceToken)
    {
        InitializeComponent();
        _provisioningClient = provisioningClient;
        _operatorStore = operatorStore;
        _deviceToken = deviceToken;

        Loaded += (_, _) => InitializePickerOrProvisioning();
    }

    private void InitializePickerOrProvisioning()
    {
        var now = DateTimeOffset.UtcNow;
        _nonStaleOperators = _operatorStore.Load().Where(op => !op.IsStale(now)).ToList();

        if (_nonStaleOperators.Count == 0)
        {
            // No cached operators at all: reveal provisioning, hide the picker.
            OperatorPickerPanel.Visibility = Visibility.Collapsed;
            AddOperatorPanel.Visibility = Visibility.Visible;
            AddOperatorToggleButton.Visibility = Visibility.Collapsed;
            return;
        }

        OperatorPickerPanel.Visibility = Visibility.Visible;
        OperatorListBox.ItemsSource = _nonStaleOperators;

        if (_nonStaleOperators.Count == 1)
        {
            // Auto-select-when-one, mirroring /device/pair's auto-select-single-branch convention.
            OperatorListBox.SelectedItem = _nonStaleOperators[0];
            OperatorListBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            OperatorListBox.SelectedIndex = 0;
        }
    }

    private void AddOperatorToggleButton_Click(object sender, RoutedEventArgs e)
    {
        AddOperatorPanel.Visibility = AddOperatorPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;

        if (OperatorListBox.SelectedItem is not CachedOperator selected)
        {
            StatusText.Text = "Select an operator first.";
            return;
        }

        if (!OperatorPinCredential.Verify(PinBox.Password, selected.Salt, selected.Subkey))
        {
            StatusText.Text = "Incorrect PIN.";
            return;
        }

        ActiveOperator = selected;
        DialogResult = true;
        Close();
    }

    private async void ProvisionButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;

        var email = EmailTextBox.Text;
        var password = PasswordBox.Password;
        var pin = NewPinBox.Password;
        var confirmPin = ConfirmPinBox.Password;

        if (!OperatorPinCredential.IsValidPin(pin))
        {
            StatusText.Text = "PIN must be exactly 6 digits and not a trivial pattern.";
            return;
        }

        if (pin != confirmPin)
        {
            StatusText.Text = "PIN and confirmation do not match.";
            return;
        }

        var outcome = await _provisioningClient.VerifyAsync(email, password, _deviceToken);

        switch (outcome.Kind)
        {
            case OperatorVerifyOutcomeKind.Verified:
                var (salt, subkey) = OperatorPinCredential.Derive(pin);
                var operatorRecord = new CachedOperator(
                    outcome.UserId!.Value, outcome.Email!, outcome.OrganizationId!.Value,
                    salt, subkey, DateTimeOffset.UtcNow, outcome.Permissions);

                _operatorStore.Upsert(operatorRecord);
                ActiveOperator = operatorRecord;
                DialogResult = true;
                Close();
                break;

            case OperatorVerifyOutcomeKind.InvalidCredentials:
                StatusText.Text = "Invalid email or password.";
                break;

            case OperatorVerifyOutcomeKind.Failed:
            default:
                StatusText.Text = outcome.ErrorMessage ?? "Provisioning failed.";
                break;
        }
    }

    private void ContinueWithoutOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        ActiveOperator = null;
        DialogResult = false;
        Close();
    }
}
