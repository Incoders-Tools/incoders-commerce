using System.Windows;

namespace Commerce.Pos.Windows;

/// <summary>
/// The operator PIN picker: offline sign-in for operators already cached on
/// this terminal (pos-operator-session "Offline Operator Switching After
/// Provisioning"). It never provisions: adding an operator is done from
/// Personal (<see cref="TerminalOperatorsWindow"/>), and first-run setup uses
/// <see cref="ProvisionOperatorWindow"/> (see <see cref="OperatorSignInFlow"/>).
/// </summary>
public partial class OperatorLoginWindow : Window
{
    private readonly LocalOperatorStore _operatorStore;

    public CachedOperator? ActiveOperator { get; private set; }

    public OperatorLoginWindow(LocalOperatorStore operatorStore)
    {
        InitializeComponent();
        _operatorStore = operatorStore;

        Loaded += (_, _) => LoadOperators();
    }

    private void LoadOperators()
    {
        var now = DateTimeOffset.UtcNow;
        var operators = _operatorStore.Load().Where(op => !op.IsStale(now)).ToList();

        if (operators.Count == 0)
        {
            OperatorPickerPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = PosMessages.NoOperatorsOnTerminal;
            return;
        }

        OperatorListBox.ItemsSource = operators;
        if (operators.Count == 1)
        {
            // Auto-select-when-one, mirroring /device/pair's auto-select-single-branch convention.
            OperatorListBox.SelectedItem = operators[0];
            OperatorListBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            OperatorListBox.SelectedIndex = 0;
        }
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;

        if (OperatorListBox.SelectedItem is not CachedOperator selected)
        {
            StatusText.Text = PosMessages.SelectOperatorFirst;
            return;
        }

        if (!OperatorPinCredential.Verify(PinBox.Password, selected.Salt, selected.Subkey))
        {
            StatusText.Text = PosMessages.IncorrectPin;
            return;
        }

        ActiveOperator = selected;
        DialogResult = true;
        Close();
    }

    private void ContinueWithoutOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        ActiveOperator = null;
        DialogResult = false;
        Close();
    }
}
