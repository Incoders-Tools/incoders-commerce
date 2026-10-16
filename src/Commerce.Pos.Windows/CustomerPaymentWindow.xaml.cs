using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Commerce.BranchNode;
using Commerce.Domain.Sales;

namespace Commerce.Pos.Windows;

/// <summary>
/// Collecting a current account payment at the counter ("cobro"): shows what the customer owes (as this terminal knows
/// it), asks how much and how it pays (cash with the amount received and the change, card or QR) and an optional note.
/// It only decides and reports (<see cref="Amount"/>, <see cref="Tender"/>, <see cref="Note"/>); the host records it.
/// </summary>
public partial class CustomerPaymentWindow : Window
{
    public CustomerPaymentWindow(string customerName, CustomerAccountView account)
    {
        InitializeComponent();
        CustomerText.Text = customerName;
        BalanceHeadlineText.Text = CustomerPaymentInput.BalanceHeadline(account);
        BalanceDetailText.Text = CustomerPaymentInput.BalanceDetail(account);
        AmountTextBox.Text = CustomerPaymentInput.SuggestedAmount(account);
        Loaded += (_, _) =>
        {
            AmountTextBox.Focus();
            AmountTextBox.SelectAll();
        };
        Refresh();
    }

    public decimal Amount { get; private set; }

    public SaleTender? Tender { get; private set; }

    public string? Note { get; private set; }

    private string Method => CardRadio.IsChecked == true ? SaleTender.Card : QrRadio.IsChecked == true ? SaleTender.Qr : SaleTender.Cash;

    private CustomerPaymentEntry Evaluate() => CustomerPaymentInput.Evaluate(AmountTextBox.Text, Method, ReceivedTextBox.Text);

    private void Refresh()
    {
        if (ConfirmButton is null)
        {
            return; // still loading the XAML
        }

        CashPanel.Visibility = Method == SaleTender.Cash ? Visibility.Visible : Visibility.Collapsed;
        var entry = Evaluate();
        ConfirmButton.IsEnabled = entry.IsValid;
        ChangeText.Text = entry.Change is { } change ? change.ToString("C", CultureInfo.CurrentCulture) : "—";
        MessageText.Text = entry.Message ?? string.Empty;
        MessageBorder.Visibility = entry.Message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Input_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void Method_Checked(object sender, RoutedEventArgs e) => Refresh();

    private void ExactButton_Click(object sender, RoutedEventArgs e)
    {
        ReceivedTextBox.Text = AmountTextBox.Text;
        ReceivedTextBox.Focus();
        ReceivedTextBox.CaretIndex = ReceivedTextBox.Text.Length;
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
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
        var entry = Evaluate();
        if (!entry.IsValid)
        {
            return;
        }

        Amount = entry.Amount!.Value;
        Tender = entry.Tender;
        Note = string.IsNullOrWhiteSpace(NoteTextBox.Text) ? null : NoteTextBox.Text.Trim();
        DialogResult = true;
    }
}
