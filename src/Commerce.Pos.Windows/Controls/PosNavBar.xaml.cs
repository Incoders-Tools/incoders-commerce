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
    public static readonly RoutedEvent CustomersRequestedEvent = Register(nameof(CustomersRequested));
    public static readonly RoutedEvent StaffRequestedEvent = Register(nameof(StaffRequested));
    public static readonly RoutedEvent SyncRequestedEvent = Register(nameof(SyncRequested));
    public static readonly RoutedEvent SettingsRequestedEvent = Register(nameof(SettingsRequested));
    public static readonly RoutedEvent SwitchOperatorRequestedEvent = Register(nameof(SwitchOperatorRequested));

    public PosNavBar() => InitializeComponent();

    public event RoutedEventHandler CustomersRequested { add => AddHandler(CustomersRequestedEvent, value); remove => RemoveHandler(CustomersRequestedEvent, value); }

    public event RoutedEventHandler StaffRequested { add => AddHandler(StaffRequestedEvent, value); remove => RemoveHandler(StaffRequestedEvent, value); }

    public event RoutedEventHandler SyncRequested { add => AddHandler(SyncRequestedEvent, value); remove => RemoveHandler(SyncRequestedEvent, value); }

    public event RoutedEventHandler SettingsRequested { add => AddHandler(SettingsRequestedEvent, value); remove => RemoveHandler(SettingsRequestedEvent, value); }

    public event RoutedEventHandler SwitchOperatorRequested { add => AddHandler(SwitchOperatorRequestedEvent, value); remove => RemoveHandler(SwitchOperatorRequestedEvent, value); }

    public string OperatorDisplayText
    {
        get => OperatorText.Text;
        set
        {
            OperatorText.Text = value;
            OperatorButton.ToolTip = $"Cambiar operador ({value})";
        }
    }

    /// <summary>Shows the customers and staff entries (operators with the ManageUsers permission).</summary>
    public bool AdminEntriesVisible
    {
        get => CustomersButton.Visibility == Visibility.Visible;
        set => CustomersButton.Visibility = StaffButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private static RoutedEvent Register(string name) =>
        EventManager.RegisterRoutedEvent(name, RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PosNavBar));

    private void CustomersButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CustomersRequestedEvent, this));

    private void StaffButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(StaffRequestedEvent, this));

    private void SyncButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(SyncRequestedEvent, this));

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(SettingsRequestedEvent, this));

    private void OperatorButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(SwitchOperatorRequestedEvent, this));
}
