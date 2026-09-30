using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// The open-cash prompt (pos-cash-session "Opening a Cash Session"): names the
/// signed-in operator and asks for the opening float. When nobody is signed in
/// it offers to sign in first, since opening requires an operator. It only
/// decides and reports the <see cref="OpeningFloat"/>; the host opens the session.
/// </summary>
public partial class OpenCashWindow : Window
{
    private readonly Func<string?>? _signInOperator;
    private string? _operatorLabel;

    /// <param name="operatorLabel">The signed-in operator, or null when nobody is.</param>
    /// <param name="signInOperator">Runs the operator sign-in and returns the new operator label (null when cancelled).</param>
    public OpenCashWindow(string? operatorLabel, Func<string?>? signInOperator = null)
    {
        InitializeComponent();

        _operatorLabel = operatorLabel;
        _signInOperator = signInOperator;
        Refresh();
        Loaded += (_, _) =>
        {
            if (_operatorLabel is not null)
            {
                OpeningFloatTextBox.Focus();
                OpeningFloatTextBox.SelectAll();
            }
        };
    }

    /// <summary>The opening float to record; set only when the operator confirmed.</summary>
    public decimal? OpeningFloat { get; private set; }

    private void Refresh()
    {
        var signedIn = _operatorLabel is not null;
        OperatorText.Text = _operatorLabel ?? "Sin operador activo";
        SignInButton.Visibility = !signedIn && _signInOperator is not null ? Visibility.Visible : Visibility.Collapsed;
        OpeningFloatTextBox.IsEnabled = signedIn;

        var entry = CashSessionInput.ReadAmount(OpeningFloatTextBox.Text);
        ConfirmButton.IsEnabled = signedIn && entry.IsValid;

        var message = signedIn ? entry.Message : "Inicie sesión con un operador para abrir la caja.";
        MessageText.Text = message ?? string.Empty;
        MessageBorder.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_signInOperator?.Invoke() is { } label)
        {
            _operatorLabel = label;
            Refresh();
            OpeningFloatTextBox.Focus();
            OpeningFloatTextBox.SelectAll();
        }
    }

    private void OpeningFloatTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Refresh();
        }
    }

    private void OpeningFloatTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ConfirmButton.IsEnabled)
        {
            e.Handled = true;
            Confirm();
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        var entry = CashSessionInput.ReadAmount(OpeningFloatTextBox.Text);
        if (_operatorLabel is null || !entry.IsValid)
        {
            return;
        }

        OpeningFloat = entry.Amount;
        DialogResult = true;
    }
}
