using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// Personal section of the main window: administrator staff management over the
/// shared <see cref="ManagementConnection"/> (device credential + the signed-in
/// operator, no password prompt; the server authorizes the operator on every
/// call). The admin creates staff (email, initial password, role, this
/// terminal's branch), sees the staff list with its status, deactivates or
/// reactivates people after an inline confirmation, and resets a password.
///
/// "Operadores de esta terminal" lists who can sign in with a PIN here and lets
/// the admin remove one (local only: the user's cloud account is untouched).
/// Adding an operator is NOT done here: a new person signs in with email and
/// password from the lock/login flow after the current operator signs out.
/// Status and errors render above the scroll area so they are never hidden.
/// </summary>
public partial class StaffView : UserControl, ISectionView
{
    private readonly UserAdminClient _client;
    private readonly LocalOperatorStore _operatorStore;
    private readonly Guid _branchId;
    private readonly Guid _callerUserId;
    private readonly BusyController _busy;
    private IReadOnlyList<UserAdminRecordDto> _users = [];
    private Guid? _pendingStatusUserId;
    private Guid? _pendingOperatorRemovalUserId;
    private Guid? _resetUserId;

    /// <param name="callerUserId">The signed-in operator's user id: their own row never offers deactivation.</param>
    public StaffView(UserAdminClient client, LocalOperatorStore operatorStore, Guid branchId, Guid callerUserId)
    {
        InitializeComponent();
        _client = client;
        _operatorStore = operatorStore;
        _branchId = branchId;
        _callerUserId = callerUserId;
        _busy = new BusyController(ApplyBusy, nameof(StaffView), message => ShowStatus(message, isError: true));

        RoleComboBox.ItemsSource = StaffRoleOptions.All;
        RoleComboBox.SelectedValue = StaffRoleOptions.Default.Name;
        RenderOperators();
        Loaded += async (_, _) => await _busy.RunAsync(PosMessages.Loading, LoadUsersAsync);
    }

    /// <summary>Raised after an operator was removed from this terminal: the host reconciles the active operator.</summary>
    public event EventHandler? OperatorsChanged;

    public bool IsBusy => _busy.IsBusy;

    public event Action? Idle
    {
        add => _busy.Idle += value;
        remove => _busy.Idle -= value;
    }

    public void CancelPending() => _busy.Cancel();

    /// <summary>The client is the shared management connection's: only the request in flight is cancelled.</summary>
    public void Dispose() => _busy.Cancel();

    private void ApplyBusy(bool busy, string? text)
    {
        FormPanel.IsEnabled = !busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? string.Empty;
        Cursor = busy ? Cursors.Wait : null;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "DangerBrush" : "SuccessBrush");
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadUsersAsync()
    {
        var users = await _client.ListUsersAsync(_busy.Token);
        if (users is null)
        {
            ShowStatus(PosMessages.UsersLoadFailed, isError: true);
            return;
        }

        _users = users;
        RenderStaff();
    }

    private void RenderStaff()
    {
        var rows = StaffRowPresenter.Build(_users, _callerUserId, _branchId, _pendingStatusUserId);
        StaffItemsControl.ItemsSource = rows;
        StaffEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- create ----------------------------------------------------------------

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        var email = NewEmailTextBox.Text.Trim();
        var password = NewPasswordBox.Password;
        var role = RoleComboBox.SelectedValue as string ?? StaffRoleOptions.Default.Name;
        if (email.Length == 0 || password.Length == 0)
        {
            ShowStatus(PosMessages.EmailAndPasswordRequired, isError: true);
            return;
        }

        await _busy.RunAsync(PosMessages.Saving, async () =>
        {
            ShowStatus(string.Empty, isError: false);
            var outcome = await _client.CreateUserAsync(new CreateUserAdminRequestDto(email, password, [role], [_branchId]), _busy.Token);
            if (outcome.Kind != UserAdminMutationKind.Succeeded)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.SaveFailed, isError: true);
                return;
            }

            NewEmailTextBox.Text = string.Empty;
            NewPasswordBox.Clear();
            RoleComboBox.SelectedValue = StaffRoleOptions.Default.Name;
            ShowStatus(PosMessages.StaffCreated, isError: false);
            await LoadUsersAsync();
        });
    }

    // ---- deactivate / reactivate -------------------------------------------------

    private static Guid? UserIdOf(object sender) => (sender as FrameworkElement)?.Tag as Guid?;

    private void StatusActionButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingStatusUserId = UserIdOf(sender);
        ShowStatus(string.Empty, isError: false);
        RenderStaff();
    }

    private void CancelStatusButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingStatusUserId = null;
        RenderStaff();
    }

    private async void ConfirmStatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (UserIdOf(sender) is not { } id || _users.FirstOrDefault(user => user.UserId == id) is not { } target)
        {
            return;
        }

        var revoke = !target.IsRevoked;
        _pendingStatusUserId = null;
        RenderStaff();
        await _busy.RunAsync(PosMessages.UpdatingStatus, async () =>
        {
            ShowStatus(string.Empty, isError: false);
            var outcome = await _client.SetStatusAsync(id, revoke, _busy.Token);
            if (outcome.Kind != UserAdminMutationKind.Succeeded)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.StaffDeactivateFailed, isError: true);
                return;
            }

            ShowStatus(revoke ? PosMessages.StaffDeactivated : PosMessages.StaffReactivated, isError: false);
            await LoadUsersAsync();
        });
    }

    // ---- reset password --------------------------------------------------------------

    private void ResetPasswordRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (UserIdOf(sender) is not { } id || _users.FirstOrDefault(user => user.UserId == id) is not { } target)
        {
            return;
        }

        _resetUserId = id;
        ResetTitle.Text = $"Nueva contraseña para {target.Email}";
        ResetPasswordBox.Clear();
        ResetPanel.Visibility = Visibility.Visible;
        ResetPasswordBox.Focus();
    }

    private void ResetCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _resetUserId = null;
        ResetPasswordBox.Clear();
        ResetPanel.Visibility = Visibility.Collapsed;
    }

    private async void ResetSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_resetUserId is not { } id)
        {
            ShowStatus(PosMessages.SelectStaffUserFirst, isError: true);
            return;
        }

        var newPassword = ResetPasswordBox.Password;
        if (newPassword.Length == 0)
        {
            ShowStatus(PosMessages.NewPasswordRequired, isError: true);
            return;
        }

        await _busy.RunAsync(PosMessages.ResettingPassword, async () =>
        {
            ShowStatus(string.Empty, isError: false);
            var outcome = await _client.ResetPasswordAsync(id, new AdminResetPasswordRequestDto(newPassword), _busy.Token);
            if (outcome.Kind != UserAdminMutationKind.Succeeded)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.PasswordResetFailed, isError: true);
                return;
            }

            ResetPasswordBox.Clear();
            ResetPanel.Visibility = Visibility.Collapsed;
            _resetUserId = null;
            ShowStatus(PosMessages.PasswordResetDone, isError: false);
        });
    }

    // ---- operators of this terminal --------------------------------------------------

    private void RenderOperators()
    {
        var rows = TerminalOperatorRowPresenter.Build(_operatorStore.Load(), DateTimeOffset.UtcNow, _pendingOperatorRemovalUserId);
        OperatorsItemsControl.ItemsSource = rows;
        OperatorsEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var pending = rows.FirstOrDefault(row => row.IsConfirming);
        OperatorConfirmText.Text = pending?.ConfirmText ?? string.Empty;
        OperatorConfirmText.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RemoveOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingOperatorRemovalUserId = UserIdOf(sender);
        ShowStatus(string.Empty, isError: false);
        RenderOperators();
    }

    private void CancelRemoveOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingOperatorRemovalUserId = null;
        RenderOperators();
    }

    private void ConfirmRemoveOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        if (UserIdOf(sender) is not { } id)
        {
            return;
        }

        _pendingOperatorRemovalUserId = null;
        _operatorStore.Remove(id);
        RenderOperators();
        ShowStatus(PosMessages.OperatorRemoved, isError: false);
        OperatorsChanged?.Invoke(this, EventArgs.Empty);
    }
}
