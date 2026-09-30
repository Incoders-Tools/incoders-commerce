using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// The open-cash prompt (pos-cash-session "Opening a Cash Session"): names the
/// signed-in operator and asks for the opening float. It only runs once an
/// operator is signed in (the lock screen comes first). It only decides and
/// reports the <see cref="OpeningFloat"/>; the host opens the session.
/// </summary>
public partial class OpenCashWindow : Window
{
    /// <param name="operatorLabel">The signed-in operator.</param>
    public OpenCashWindow(string operatorLabel)
    {
        InitializeComponent();

        OperatorText.Text = operatorLabel;
        Refresh();
        Loaded += (_, _) =>
        {
            OpeningFloatTextBox.Focus();
            OpeningFloatTextBox.SelectAll();
        };
    }

    /// <summary>The opening float to record; set only when the operator confirmed.</summary>
    public decimal? OpeningFloat { get; private set; }

    private void Refresh()
    {
        var entry = CashSessionInput.ReadAmount(OpeningFloatTextBox.Text);
        ConfirmButton.IsEnabled = entry.IsValid;

        MessageText.Text = entry.Message ?? string.Empty;
        MessageBorder.Visibility = entry.Message is null ? Visibility.Collapsed : Visibility.Visible;
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
        if (!entry.IsValid)
        {
            return;
        }

        OpeningFloat = entry.Amount;
        DialogResult = true;
    }
}
