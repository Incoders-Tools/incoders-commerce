using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows.Controls;

/// <summary>
/// The inline "confirm your password" prompt of the admin sections (Clientes,
/// Personal). The section owns the sign-in call; this control only collects the
/// credentials and hands them over. The password is never kept: the section
/// clears it as soon as the request was sent.
/// </summary>
public partial class AdminSignInPanel : UserControl
{
    public AdminSignInPanel() => InitializeComponent();

    /// <summary>Raised when the operator confirms (button or Enter).</summary>
    public event EventHandler? SignInRequested;

    /// <param name="email">The signed-in operator; shown read-only. Null lets the operator type the admin email.</param>
    public void Initialize(string title, string hint, string? email)
    {
        PromptTitle.Text = title;
        PromptHint.Text = hint;
        EmailTextBox.Text = email ?? string.Empty;
        EmailTextBox.IsReadOnly = email is not null;
    }

    public string Email => EmailTextBox.Text;

    public string Password => PasswordBox.Password;

    public void ClearPassword() => PasswordBox.Clear();

    public void FocusPassword() => PasswordBox.Focus();

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => SignInRequested?.Invoke(this, EventArgs.Empty);

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SignInRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
