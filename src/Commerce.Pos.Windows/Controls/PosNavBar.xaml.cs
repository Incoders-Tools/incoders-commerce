using System.Windows;
using System.Windows.Controls;

namespace Commerce.Pos.Windows.Controls;

/// <summary>
/// Top navigation of the sale screen. It owns no behavior: every action is a
/// routed event the host maps to its existing handler, and the operator label
/// and admin-only entries are driven through properties.
/// </summary>
public partial class PosNavBar : UserControl
{
    public static readonly RoutedEvent SaleRequestedEvent = Register(nameof(SaleRequested));
    public static readonly RoutedEvent SalesRequestedEvent = Register(nameof(SalesRequested));
    public static readonly RoutedEvent CustomersRequestedEvent = Register(nameof(CustomersRequested));
    public static readonly RoutedEvent StaffRequestedEvent = Register(nameof(StaffRequested));
    public static readonly RoutedEvent SyncRequestedEvent = Register(nameof(SyncRequested));
    public static readonly RoutedEvent SettingsRequestedEvent = Register(nameof(SettingsRequested));
    public static readonly RoutedEvent OperatorMenuRequestedEvent = Register(nameof(OperatorMenuRequested));
    public static readonly RoutedEvent SwitchOperatorRequestedEvent = Register(nameof(SwitchOperatorRequested));
    public static readonly RoutedEvent SignInRequestedEvent = Register(nameof(SignInRequested));
    public static readonly RoutedEvent SignOutRequestedEvent = Register(nameof(SignOutRequested));
    public static readonly RoutedEvent CloseCashRequestedEvent = Register(nameof(CloseCashRequested));

    public static readonly RoutedEvent CashMovementRequestedEvent = Register(nameof(CashMovementRequested));

    public PosNavBar() => InitializeComponent();

    public event RoutedEventHandler SaleRequested { add => AddHandler(SaleRequestedEvent, value); remove => RemoveHandler(SaleRequestedEvent, value); }

    public event RoutedEventHandler SalesRequested { add => AddHandler(SalesRequestedEvent, value); remove => RemoveHandler(SalesRequestedEvent, value); }

    public event RoutedEventHandler CustomersRequested { add => AddHandler(CustomersRequestedEvent, value); remove => RemoveHandler(CustomersRequestedEvent, value); }

    public event RoutedEventHandler StaffRequested { add => AddHandler(StaffRequestedEvent, value); remove => RemoveHandler(StaffRequestedEvent, value); }

    public event RoutedEventHandler SyncRequested { add => AddHandler(SyncRequestedEvent, value); remove => RemoveHandler(SyncRequestedEvent, value); }

    public event RoutedEventHandler SettingsRequested { add => AddHandler(SettingsRequestedEvent, value); remove => RemoveHandler(SettingsRequestedEvent, value); }

    /// <summary>The operator button was clicked: the host answers with <see cref="OpenOperatorMenu"/>.</summary>
    public event RoutedEventHandler OperatorMenuRequested { add => AddHandler(OperatorMenuRequestedEvent, value); remove => RemoveHandler(OperatorMenuRequestedEvent, value); }

    public event RoutedEventHandler SignInRequested { add => AddHandler(SignInRequestedEvent, value); remove => RemoveHandler(SignInRequestedEvent, value); }

    public event RoutedEventHandler SignOutRequested { add => AddHandler(SignOutRequestedEvent, value); remove => RemoveHandler(SignOutRequestedEvent, value); }

    public event RoutedEventHandler SwitchOperatorRequested { add => AddHandler(SwitchOperatorRequestedEvent, value); remove => RemoveHandler(SwitchOperatorRequestedEvent, value); }

    public event RoutedEventHandler CloseCashRequested { add => AddHandler(CloseCashRequestedEvent, value); remove => RemoveHandler(CloseCashRequestedEvent, value); }

    /// <summary>"Movimiento de caja": money out of or into the drawer outside a sale.</summary>
    public event RoutedEventHandler CashMovementRequested { add => AddHandler(CashMovementRequestedEvent, value); remove => RemoveHandler(CashMovementRequestedEvent, value); }

    public string OperatorLabel
    {
        get => OperatorDisplayText.Text;
        set
        {
            OperatorDisplayText.Text = value;
            SwitchOperatorButton.ToolTip = $"Operador y sesión ({value})";
        }
    }

    /// <summary>
    /// Shows the cash session state ("Caja abierta · 08:15" / "Caja cerrada") and
    /// enables "Cerrar Caja" only while a session is open.
    /// </summary>
    public void SetCashSession(string statusText, bool isOpen)
    {
        CashSessionStatusText.Text = statusText;
        CloseCashButton.IsEnabled = isOpen;
        CashMovementButton.IsEnabled = isOpen;
        CashSessionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, isOpen ? "AccentBrush" : "MutedTextBrush");
    }

    /// <summary>Shows the operator menu under the operator button with the entries the presenter decided.</summary>
    public void OpenOperatorMenu(OperatorMenuState state)
    {
        OperatorMenuTitle.Text = state.Title;
        OperatorMenuSubtitle.Text = state.Subtitle ?? string.Empty;
        OperatorMenuSubtitle.Visibility = state.Subtitle is null ? Visibility.Collapsed : Visibility.Visible;
        SwitchOperatorMenuButton.Visibility = VisibleWhen(state, OperatorMenuAction.SwitchOperator);
        SignInMenuButton.Visibility = VisibleWhen(state, OperatorMenuAction.SignIn);
        SignOutMenuButton.Visibility = VisibleWhen(state, OperatorMenuAction.SignOut);
        OperatorMenuPopup.IsOpen = true;
    }

    private static Visibility VisibleWhen(OperatorMenuState state, OperatorMenuAction action) =>
        state.Actions.Contains(action) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows the customers and staff entries (operators with the ManageUsers permission).</summary>
    public bool AdminEntriesVisible
    {
        get => ManageCustomersButton.Visibility == Visibility.Visible;
        set => ManageCustomersButton.Visibility = ManageStaffButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Highlights the entry of the section the shell shows (the nav style keys off Tag="Active").</summary>
    public void SetActiveSection(ShellSection section)
    {
        NewSaleButton.Tag = section == ShellSection.Sale ? "Active" : null;
        SalesHistoryButton.Tag = section == ShellSection.Sales ? "Active" : null;
        ManageCustomersButton.Tag = section == ShellSection.Customers ? "Active" : null;
        ManageStaffButton.Tag = section == ShellSection.Staff ? "Active" : null;
    }

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    private readonly PopupReopenGuard _operatorMenuGuard = new();

    private static RoutedEvent Register(string name) =>
        EventManager.RegisterRoutedEvent(name, RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PosNavBar));

    private void SaleButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(SaleRequestedEvent, this));

    private void SalesButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(SalesRequestedEvent, this));

    private void CustomersButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CustomersRequestedEvent, this));

    private void StaffButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(StaffRequestedEvent, this));

    private void SyncButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(SyncRequestedEvent, this));

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(SettingsRequestedEvent, this));

    private void OperatorButton_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _operatorMenuGuard.NotifyPressed(Clock.Elapsed);

    private void OperatorMenuPopup_Closed(object? sender, EventArgs e) => _operatorMenuGuard.NotifyClosed(Clock.Elapsed);

    private void OperatorButton_Click(object sender, RoutedEventArgs e)
    {
        // StaysOpen=False closes the menu on the press; the Click that follows must not reopen it.
        if (!OperatorMenuPopup.IsOpen && _operatorMenuGuard.ShouldOpenOnClick(Clock.Elapsed))
        {
            RaiseEvent(new RoutedEventArgs(OperatorMenuRequestedEvent, this));
        }
    }

    private void SwitchOperatorMenuButton_Click(object sender, RoutedEventArgs e) => RaiseMenuAction(SwitchOperatorRequestedEvent);

    private void SignInMenuButton_Click(object sender, RoutedEventArgs e) => RaiseMenuAction(SignInRequestedEvent);

    private void SignOutMenuButton_Click(object sender, RoutedEventArgs e) => RaiseMenuAction(SignOutRequestedEvent);

    private void RaiseMenuAction(RoutedEvent routedEvent)
    {
        OperatorMenuPopup.IsOpen = false;
        RaiseEvent(new RoutedEventArgs(routedEvent, this));
    }

    private void CloseCashButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CloseCashRequestedEvent, this));

    private void CashMovementButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CashMovementRequestedEvent, this));
}
