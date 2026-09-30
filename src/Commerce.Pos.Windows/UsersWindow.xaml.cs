using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Commerce.Domain.Identity;

namespace Commerce.Pos.Windows;

public partial class UsersWindow : Window
{
    private readonly UserAdminClient _client;
    private readonly Guid _branchId;
    private readonly BusyController _busy;
    private readonly Action? _openTerminalOperators;
    private Guid? _selectedUserId;

    /// <param name="openTerminalOperators">Opens the operators of this terminal (add / remove); null hides the entry.</param>
    public UsersWindow(UserAdminClient client, Guid branchId, ApplicationBranding branding, Action? openTerminalOperators = null)
    {
        InitializeComponent();
        _client = client;
        _branchId = branchId;
        _openTerminalOperators = openTerminalOperators;
        TerminalOperatorsButton.Visibility = openTerminalOperators is null ? Visibility.Collapsed : Visibility.Visible;
        Title = branding.UsersWindowTitle;
        RolesItemsControl.ItemsSource = RoleCatalog.OrgAssignable.OrderBy(x => x).Select(x => new RoleChoice(x));
        _busy = new BusyController(ApplyBusy, nameof(UsersWindow), ShowStatus);

        // A request in flight must finish before the window goes away.
        Closing += (_, e) => e.Cancel = _busy.IsBusy;
    }

    private void ApplyBusy(bool busy, string? text)
    {
        FormPanel.IsEnabled = !busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? string.Empty;
        Cursor = busy ? Cursors.Wait : null;
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        FormStatusText.Text = message;
    }

    private void TerminalOperatorsButton_Click(object sender, RoutedEventArgs e) => _openTerminalOperators?.Invoke();

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        var email = AdminEmailTextBox.Text;
        var password = AdminPasswordBox.Password;
        await _busy.RunAsync(PosMessages.SigningIn, async () =>
        {
            StatusText.Text = string.Empty;
            var outcome = await _client.SignInAsync(email, password);
            if (outcome.Kind != AdminSignInOutcomeKind.SignedIn)
            {
                StatusText.Text = outcome.ErrorMessage ?? PosMessages.SignInFailed;
                return;
            }

            SignInPanel.Visibility = Visibility.Collapsed;
            ManagementPanel.Visibility = Visibility.Visible;
            await LoadUsersAsync();
        });
    }

    private async Task LoadUsersAsync()
    {
        var users = await _client.ListUsersAsync();
        if (users is null) { FormStatusText.Text = PosMessages.UsersLoadFailed; return; }
        UsersListBox.ItemsSource = users;
    }

    private void NewUserButton_Click(object sender, RoutedEventArgs e) { _selectedUserId = null; UsersListBox.SelectedItem = null; EmailTextBox.Text = string.Empty; PasswordBox.Password = string.Empty; SetRoles([]); }
    private void UsersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UsersListBox.SelectedItem is not UserAdminRecordDto user) return;
        _selectedUserId = user.UserId; EmailTextBox.Text = user.Email; PasswordBox.Password = string.Empty; SetRoles(user.RoleNames);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var roles = SelectedRoles();
        var email = EmailTextBox.Text;
        var password = PasswordBox.Password;
        var selectedUserId = _selectedUserId;
        await _busy.RunAsync(PosMessages.Saving, async () =>
        {
            FormStatusText.Text = string.Empty;
            UserAdminMutationOutcome result;
            if (selectedUserId is { } id) result = await _client.ReplaceRolesAsync(id, new AssignRolesAdminRequestDto(roles));
            else result = await _client.CreateUserAsync(new CreateUserAdminRequestDto(email, password, roles, [_branchId]));
            FormStatusText.Text = result.ErrorMessage ?? (result.Kind == UserAdminMutationKind.Succeeded ? PosMessages.Saved : PosMessages.SaveFailed);
            if (result.Kind == UserAdminMutationKind.Succeeded) await LoadUsersAsync();
        });
    }

    private async void ResetPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUserId is not { } id) { FormStatusText.Text = PosMessages.SelectStaffUserFirst; return; }
        var newPassword = PasswordBox.Password;
        await _busy.RunAsync(PosMessages.ResettingPassword, async () =>
        {
            FormStatusText.Text = string.Empty;
            var result = await _client.ResetPasswordAsync(id, new AdminResetPasswordRequestDto(newPassword));
            FormStatusText.Text = result.ErrorMessage ?? (result.Kind == UserAdminMutationKind.Succeeded ? PosMessages.PasswordResetDone : PosMessages.PasswordResetFailed);
        });
    }

    private string[] SelectedRoles() => RolesItemsControl.Items.OfType<RoleChoice>().Where(x => x.IsSelected).Select(x => x.Name).ToArray();
    private void SetRoles(IEnumerable<string> names) { var selected = names.ToHashSet(StringComparer.OrdinalIgnoreCase); foreach (var role in RolesItemsControl.Items.OfType<RoleChoice>()) role.IsSelected = selected.Contains(role.Name); RolesItemsControl.Items.Refresh(); }
    public sealed class RoleChoice(string name) { public string Name { get; } = name; public bool IsSelected { get; set; } }
}
