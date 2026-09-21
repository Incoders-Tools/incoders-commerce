using System.Windows;
using System.Windows.Controls;
using Commerce.Domain.Identity;

namespace Commerce.Pos.Windows;

public partial class UsersWindow : Window
{
    private readonly UserAdminClient _client;
    private readonly Guid _branchId;
    private Guid? _selectedUserId;

    public UsersWindow(UserAdminClient client, Guid branchId, ApplicationBranding branding)
    {
        InitializeComponent();
        _client = client;
        _branchId = branchId;
        Title = branding.UsersWindowTitle;
        RolesItemsControl.ItemsSource = RoleCatalog.OrgAssignable.OrderBy(x => x).Select(x => new RoleChoice(x));
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        var outcome = await _client.SignInAsync(AdminEmailTextBox.Text, AdminPasswordBox.Password);
        if (outcome.Kind != AdminSignInOutcomeKind.SignedIn) { StatusText.Text = outcome.ErrorMessage ?? "Sign in failed."; return; }
        SignInPanel.Visibility = Visibility.Collapsed; ManagementPanel.Visibility = Visibility.Visible; await LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        var users = await _client.ListUsersAsync();
        if (users is null) { FormStatusText.Text = "Staff management requires connectivity."; return; }
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
        UserAdminMutationOutcome result;
        if (_selectedUserId is { } id) result = await _client.ReplaceRolesAsync(id, new AssignRolesAdminRequestDto(roles));
        else result = await _client.CreateUserAsync(new CreateUserAdminRequestDto(EmailTextBox.Text, PasswordBox.Password, roles, [_branchId]));
        FormStatusText.Text = result.ErrorMessage ?? (result.Kind == UserAdminMutationKind.Succeeded ? "Saved." : "Save failed.");
        if (result.Kind == UserAdminMutationKind.Succeeded) await LoadUsersAsync();
    }

    private async void ResetPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUserId is not { } id) { FormStatusText.Text = "Select a staff user first."; return; }
        var result = await _client.ResetPasswordAsync(id, new AdminResetPasswordRequestDto(PasswordBox.Password));
        FormStatusText.Text = result.ErrorMessage ?? (result.Kind == UserAdminMutationKind.Succeeded ? "Password reset." : "Reset failed.");
    }

    private string[] SelectedRoles() => RolesItemsControl.Items.OfType<RoleChoice>().Where(x => x.IsSelected).Select(x => x.Name).ToArray();
    private void SetRoles(IEnumerable<string> names) { var selected = names.ToHashSet(StringComparer.OrdinalIgnoreCase); foreach (var role in RolesItemsControl.Items.OfType<RoleChoice>()) role.IsSelected = selected.Contains(role.Name); RolesItemsControl.Items.Refresh(); }
    public sealed class RoleChoice(string name) { public string Name { get; } = name; public bool IsSelected { get; set; } }
}
