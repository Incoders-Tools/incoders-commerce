using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// Personal section of the main window: administrator staff management over the
/// shared <see cref="ManagementConnection"/> (device credential + the signed-in
/// operator, no password prompt; the server authorizes the operator and applies
/// its grant caps on every call).
///
/// The staff list is the reusable entity list (price-editing-and-desktop-polish
/// T1, <see cref="Controls.EntityListView"/> over <see cref="StaffList"/>) over
/// the full width: search by email, Rol / Estado filters, sortable columns, and
/// the Editar rol / Revocar-Restaurar (after an inline confirmation) / Resetear
/// contraseña row actions. The form is hidden until "Nuevo" (email, initial
/// password, role; created in this terminal's branch) or a row action opens it
/// beside the list; a successful save closes it, back to the list.
///
/// "Operadores de esta terminal" lists who can sign in with a PIN here and lets
/// the admin remove one (local only: the user's cloud account is untouched).
/// Adding an operator is NOT done here: a new person signs in with email and
/// password from the lock/login flow after the current operator signs out.
/// Status and errors render above the list so they are never hidden.
/// </summary>
public partial class StaffView : UserControl, ISectionView
{
    private readonly UserAdminClient _client;
    private readonly LocalOperatorStore _operatorStore;
    private readonly BusyController _busy;
    private readonly StaffList _staff;
    private Guid? _pendingOperatorRemovalUserId;

    /// <param name="callerUserId">The signed-in operator's user id: their own row never offers revoke nor a role change.</param>
    public StaffView(UserAdminClient client, LocalOperatorStore operatorStore, Guid branchId, Guid callerUserId)
    {
        InitializeComponent();
        _client = client;
        _operatorStore = operatorStore;
        _busy = new BusyController(ApplyBusy, nameof(StaffView), message => ShowStatus(message, isError: true));

        RoleComboBox.ItemsSource = StaffRoleOptions.All;
        EditRoleComboBox.ItemsSource = StaffRoleOptions.All;

        _staff = new StaffList(callerUserId, branchId, ToggleStatusAsync);
        _staff.EditorChanged += OnEditorChanged;
        StaffListView.Model = _staff.Model;

        ShowForm(StaffEditorPurpose.None);
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

        _staff.Model.SetItems(users);
    }

    // ---- the editor: one form at a time ------------------------------------------------

    /// <summary>"Nuevo" opens the create form empty, a row its role or password form; closing clears them all.</summary>
    private void OnEditorChanged(StaffEditorPurpose purpose, UserAdminRecordDto? user)
    {
        ShowStatus(string.Empty, isError: false);
        ShowForm(purpose);
        if (purpose == StaffEditorPurpose.EditRole && user is not null)
        {
            var canEdit = _staff.CanEditRole(user);
            EditRoleComboBox.SelectedValue = StaffList.InitialRole(user);
            EditRoleComboBox.IsEnabled = canEdit;
            SaveRoleButton.IsEnabled = canEdit;
            OwnRoleHint.Visibility = canEdit ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ShowForm(StaffEditorPurpose purpose)
    {
        CreatePanel.Visibility = purpose == StaffEditorPurpose.Create ? Visibility.Visible : Visibility.Collapsed;
        EditRolePanel.Visibility = purpose == StaffEditorPurpose.EditRole ? Visibility.Visible : Visibility.Collapsed;
        ResetPanel.Visibility = purpose == StaffEditorPurpose.ResetPassword ? Visibility.Visible : Visibility.Collapsed;
        if (purpose != StaffEditorPurpose.Create)
        {
            NewEmailTextBox.Text = string.Empty;
            NewPasswordBox.Clear();
            RoleComboBox.SelectedValue = StaffRoleOptions.Default.Name;
        }

        if (purpose != StaffEditorPurpose.ResetPassword)
        {
            ResetPasswordBox.Clear();
        }
    }

    private void CancelEditorButton_Click(object sender, RoutedEventArgs e) => _staff.Model.CloseEditor();

    // ---- create ----------------------------------------------------------------

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_staff.CreateRequest(NewEmailTextBox.Text, NewPasswordBox.Password, RoleComboBox.SelectedValue as string) is not { } request)
        {
            ShowStatus(PosMessages.EmailAndPasswordRequired, isError: true);
            return;
        }

        await _busy.RunAsync(PosMessages.Saving, async () =>
        {
            ShowStatus(string.Empty, isError: false);
            var outcome = await _client.CreateUserAsync(request, _busy.Token);
            if (outcome.Kind != UserAdminMutationKind.Succeeded)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.SaveFailed, isError: true);
                return;
            }

            // Back to the list: a second Crear usuario must not create the user twice.
            _staff.Model.CloseEditor();
            ShowStatus(PosMessages.StaffCreated, isError: false);
            await LoadUsersAsync();
        });
    }

    // ---- role ----------------------------------------------------------------------

    private async void SaveRoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_staff.Purpose != StaffEditorPurpose.EditRole || _staff.Model.Editing is not { } target || !_staff.CanEditRole(target))
        {
            return;
        }

        var roles = StaffList.RolesAfterEdit(target, EditRoleComboBox.SelectedValue as string);
        await _busy.RunAsync(PosMessages.Saving, async () =>
        {
            ShowStatus(string.Empty, isError: false);
            var outcome = await _client.ReplaceRolesAsync(target.UserId, new AssignRolesAdminRequestDto(roles), _busy.Token);
            if (outcome.Kind != UserAdminMutationKind.Succeeded)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.SaveFailed, isError: true);
                return;
            }

            _staff.Model.CloseEditor();
            ShowStatus(PosMessages.Saved, isError: false);
            await LoadUsersAsync();
        });
    }

    // ---- revoke / restore ---------------------------------------------------------------

    /// <summary>Revocar / Restaurar, after the list's inline confirmation.</summary>
    private Task ToggleStatusAsync(UserAdminRecordDto target) => _busy.RunAsync(PosMessages.UpdatingStatus, async () =>
    {
        var revoke = !target.IsRevoked;
        ShowStatus(string.Empty, isError: false);
        var outcome = await _client.SetStatusAsync(target.UserId, revoke, _busy.Token);
        if (outcome.Kind != UserAdminMutationKind.Succeeded)
        {
            ShowStatus(outcome.ErrorMessage ?? PosMessages.StaffDeactivateFailed, isError: true);
            return;
        }

        ShowStatus(revoke ? PosMessages.StaffDeactivated : PosMessages.StaffReactivated, isError: false);
        await LoadUsersAsync();
    });

    // ---- reset password --------------------------------------------------------------

    private async void ResetSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_staff.Purpose != StaffEditorPurpose.ResetPassword || _staff.Model.Editing is not { } target)
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
            var outcome = await _client.ResetPasswordAsync(target.UserId, new AdminResetPasswordRequestDto(newPassword), _busy.Token);
            if (outcome.Kind != UserAdminMutationKind.Succeeded)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.PasswordResetFailed, isError: true);
                return;
            }

            _staff.Model.CloseEditor();
            ShowStatus(PosMessages.PasswordResetDone, isError: false);
        });
    }

    // ---- operators of this terminal --------------------------------------------------

    private static Guid? UserIdOf(object sender) => (sender as FrameworkElement)?.Tag as Guid?;

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
