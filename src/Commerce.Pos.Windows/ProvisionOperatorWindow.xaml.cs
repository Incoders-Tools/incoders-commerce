using System.Windows;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// Adds an operator to this terminal: verifies the email and password online
/// once, then stores the PIN verifier locally (pos-operator-session "One-Time
/// Online Provisioning Per Terminal-Operator Pair"). Reached from Personal, and
/// at startup when the terminal has no usable operator yet.
///
/// Network work runs through <see cref="BusyController"/>: the whole form
/// (<c>FormPanel</c>) is disabled with a progress bar while the request is in
/// flight, re-entry is ignored, and failures are logged by the client and shown
/// as friendly Spanish text from <see cref="PosMessages"/>.
/// </summary>
public partial class ProvisionOperatorWindow : Window
{
    private readonly OperatorProvisioningClient _provisioningClient;
    private readonly LocalOperatorStore _operatorStore;
    private readonly string _deviceToken;
    private readonly BusyController _busy;
    private bool _completed;

    /// <summary>The operator that was saved, when the dialog returned true.</summary>
    public CachedOperator? ProvisionedOperator { get; private set; }

    /// <param name="allowContinueWithoutOperator">True at startup, where the terminal may go on without an operator; false from Personal, where the button only cancels.</param>
    public ProvisionOperatorWindow(
        OperatorProvisioningClient provisioningClient, LocalOperatorStore operatorStore, string deviceToken,
        bool allowContinueWithoutOperator = false)
    {
        InitializeComponent();
        _provisioningClient = provisioningClient;
        _operatorStore = operatorStore;
        _deviceToken = deviceToken;
        _busy = new BusyController(ApplyBusy, nameof(ProvisionOperatorWindow), message => StatusText.Text = message);
        ContinueWithoutOperatorButton.Content = allowContinueWithoutOperator ? "Continuar sin operador" : "Cancelar";

        // A request in flight must finish before the window goes away.
        Closing += (_, e) => e.Cancel = _busy.IsBusy && !_completed;
    }

    private void ApplyBusy(bool busy, string? text)
    {
        FormPanel.IsEnabled = !busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? string.Empty;
        Cursor = busy ? Cursors.Wait : null;
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
            StatusText.Text = PosMessages.InvalidPinFormat;
            return;
        }

        if (pin != confirmPin)
        {
            StatusText.Text = PosMessages.PinMismatch;
            return;
        }

        await _busy.RunAsync(PosMessages.Verifying, () => ProvisionAsync(email, password, pin));
    }

    private async Task ProvisionAsync(string email, string password, string pin)
    {
        var outcome = await _provisioningClient.VerifyAsync(email, password, _deviceToken);

        switch (outcome.Kind)
        {
            case OperatorVerifyOutcomeKind.Verified:
                var (salt, subkey) = OperatorPinCredential.Derive(pin);
                var operatorRecord = new CachedOperator(
                    outcome.UserId!.Value, outcome.Email!, outcome.OrganizationId!.Value,
                    salt, subkey, DateTimeOffset.UtcNow, outcome.Permissions);

                _operatorStore.Upsert(operatorRecord);
                ProvisionedOperator = operatorRecord;
                _completed = true;
                DialogResult = true;
                Close();
                break;

            case OperatorVerifyOutcomeKind.InvalidCredentials:
                StatusText.Text = PosMessages.InvalidCredentials;
                break;

            case OperatorVerifyOutcomeKind.TerminalNotRecognized:
            case OperatorVerifyOutcomeKind.Failed:
            default:
                StatusText.Text = outcome.ErrorMessage ?? PosMessages.ProvisioningFailed;
                break;
        }
    }

    private void ContinueWithoutOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        ProvisionedOperator = null;
        DialogResult = false;
        Close();
    }
}
