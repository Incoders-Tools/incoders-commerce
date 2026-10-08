using System.Windows;
using System.Windows.Controls;

namespace Commerce.Pos.Windows;

/// <summary>
/// The Personal section of the shell (cross-layer parity with the web's Personal and Usuarios): two tabs over the shared
/// <see cref="ManagementConnection"/>.
/// <list type="bullet">
/// <item>"Empleados" (<see cref="EmployeesView"/>, the default): the staff file, advances and accounts, as the web's Personal.</item>
/// <item>"Usuarios y acceso" (<see cref="StaffView"/>): the system users, their role and password, and this terminal's operators.</item>
/// </list>
/// A tab is built the first time it is opened and then kept, so switching never drops a request in flight; the
/// section is busy while either tab is, and the shell's teardown waits for both.
/// </summary>
public partial class PersonalView : UserControl, ISectionView
{
    private readonly Func<StaffView> _createUsers;
    private readonly EmployeesView _employees;
    private StaffView? _users;

    public PersonalView(EmployeesView employees, Func<StaffView> createUsers)
    {
        InitializeComponent();
        _employees = employees;
        _createUsers = createUsers;
        _employees.Idle += OnChildIdle;
        TabHost.Content = _employees;
    }

    /// <summary>Raised after an operator was removed from this terminal (Usuarios y acceso): the host reconciles the active operator.</summary>
    public event EventHandler? OperatorsChanged;

    public event Action? Idle;

    public bool IsBusy => _employees.IsBusy || _users?.IsBusy == true;

    public void CancelPending()
    {
        _employees.CancelPending();
        _users?.CancelPending();
    }

    public void Dispose()
    {
        _employees.Dispose();
        _users?.Dispose();
    }

    private void EmployeesTab_Checked(object sender, RoutedEventArgs e)
    {
        // Raised once while InitializeComponent runs (IsChecked="True"), before the constructor set the tab.
        if (_employees is not null)
        {
            TabHost.Content = _employees;
        }
    }

    private void UsersTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_users is null)
        {
            _users = _createUsers();
            _users.Idle += OnChildIdle;
            _users.OperatorsChanged += (_, args) => OperatorsChanged?.Invoke(this, args);
        }

        TabHost.Content = _users;
    }

    /// <summary>The section is idle only when neither tab has a request in flight.</summary>
    private void OnChildIdle()
    {
        if (!IsBusy)
        {
            Idle?.Invoke();
        }
    }
}
