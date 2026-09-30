using System.Windows;

namespace Commerce.Pos.Windows;

/// <summary>
/// "Personal" area for the operators of THIS terminal: lists who can sign in
/// with a PIN, adds an operator (<see cref="ProvisionOperatorWindow"/>) and
/// removes one from the terminal. Removing only forgets the local PIN
/// verifier; it does not touch the user's cloud account. The host reconciles the
/// active operator afterwards (<see cref="OperatorSessionActions.Reconcile"/>).
/// </summary>
public partial class TerminalOperatorsWindow : Window
{
    private readonly LocalOperatorStore _operatorStore;
    private readonly OperatorProvisioningClient _provisioningClient;
    private readonly string _deviceToken;

    public TerminalOperatorsWindow(LocalOperatorStore operatorStore, OperatorProvisioningClient provisioningClient, string deviceToken)
    {
        InitializeComponent();
        _operatorStore = operatorStore;
        _provisioningClient = provisioningClient;
        _deviceToken = deviceToken;
        Refresh();
    }

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = _operatorStore.Load().OrderBy(op => op.Email).Select(op => new TerminalOperatorRow(op, now)).ToList();
        OperatorsListBox.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveOperatorButton.IsEnabled = rows.Count > 0;
    }

    private void AddOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new ProvisionOperatorWindow(_provisioningClient, _operatorStore, _deviceToken) { Owner = this };
        window.ShowDialog();
        Refresh();
    }

    private void RemoveOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        if (OperatorsListBox.SelectedItem is not TerminalOperatorRow row)
        {
            MessageBox.Show(this, PosMessages.SelectOperatorFirst, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"¿Quitar a {row.Email} de esta terminal? Ya no podrá ingresar con su PIN hasta que lo agregues de nuevo.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _operatorStore.Remove(row.UserId);
        Refresh();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>One line of the list: the email, the access level and whether the PIN needs refreshing.</summary>
    private sealed class TerminalOperatorRow(CachedOperator source, DateTimeOffset now)
    {
        public Guid UserId { get; } = source.UserId;

        public string Email { get; } = source.Email;

        public string Detail { get; } = source.IsStale(now)
            ? $"{OperatorMenuPresenter.RoleSummary(source.Permissions)} · Vencido: agregalo de nuevo para renovarlo"
            : OperatorMenuPresenter.RoleSummary(source.Permissions);
    }
}
