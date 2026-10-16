using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Commerce.Domain.Sales;

namespace Commerce.Pos.Windows;

/// <summary>
/// The tender prompt (pos-scan-sale "Tender Recorded at the Moment of Sale").
/// Cash asks for the amount received, offers "Exacto" and shows the change live;
/// card and QR ask for one confirmation that the charge was completed on the
/// merchant's own terminal or app. It only decides and reports the
/// <see cref="Tender"/>; the host commits the sale.
/// </summary>
public partial class TenderWindow : Window
{
    private readonly string _method;
    private readonly decimal _total;

    public TenderWindow(string method, decimal total, string? customerName = null, string? dueText = null)
    {
        InitializeComponent();

        _method = method;
        _total = total;
        TotalText.Text = total.ToString("C", CultureInfo.CurrentCulture);

        if (method == SaleTender.Cash)
        {
            Title = HeadingText.Text = "Cobro en efectivo";
            ConfirmButton.Content = "Confirmar cobro";
            ConfirmButton.IsEnabled = false;
            Loaded += (_, _) => AmountReceivedTextBox.Focus();
        }
        else if (method == SaleTender.Account)
        {
            Title = HeadingText.Text = "Venta a cuenta corriente";
            CashPanel.Visibility = Visibility.Collapsed;
            InstructionText.Visibility = Visibility.Visible;
            InstructionText.Text =
                $"No se cobra ahora: el total se carga a la cuenta corriente de {customerName ?? "el cliente"} y queda pendiente de pago. " +
                "No entra en el efectivo de la caja." + (dueText is null ? string.Empty : $" {dueText}");
            ConfirmButton.Content = "Confirmar venta a cuenta";
            Loaded += (_, _) => ConfirmButton.Focus();
        }
        else
        {
            var isCard = method == SaleTender.Card;
            Title = HeadingText.Text = isCard ? "Cobro con tarjeta" : "Cobro con QR";
            CashPanel.Visibility = Visibility.Collapsed;
            InstructionText.Visibility = Visibility.Visible;
            InstructionText.Text = isCard
                ? "Cobre el importe en la terminal de tarjeta del comercio y confirme cuando el pago esté aprobado. El sistema solo registra el medio de pago."
                : "Cobre el importe con el QR o la aplicación del comercio y confirme cuando el pago esté acreditado. El sistema solo registra el medio de pago.";
            ConfirmButton.Content = isCard ? "Confirmar pago con tarjeta" : "Confirmar pago con QR";
            Loaded += (_, _) => ConfirmButton.Focus();
        }
    }

    /// <summary>The tender to record; set only when the operator confirmed.</summary>
    public SaleTender? Tender { get; private set; }

    private void AmountReceivedTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshCash();

    private void AmountReceivedTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ConfirmButton.IsEnabled)
        {
            e.Handled = true;
            Confirm();
        }
    }

    private void ExactButton_Click(object sender, RoutedEventArgs e)
    {
        AmountReceivedTextBox.Text = TenderInput.ExactText(_total);
        AmountReceivedTextBox.CaretIndex = AmountReceivedTextBox.Text.Length;
        AmountReceivedTextBox.Focus();
    }

    private void RefreshCash()
    {
        var entry = TenderInput.EvaluateCash(_total, AmountReceivedTextBox.Text);
        ConfirmButton.IsEnabled = entry.IsValid;
        ChangeText.Text = entry.Change is { } change ? change.ToString("C", CultureInfo.CurrentCulture) : "—";
        MessageText.Text = entry.Message ?? string.Empty;
        MessageBorder.Visibility = entry.Message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (_method == SaleTender.Cash)
        {
            var entry = TenderInput.EvaluateCash(_total, AmountReceivedTextBox.Text);
            if (!entry.IsValid || !SaleTenderRules.TryCash(_total, entry.Received!.Value, out var cash))
            {
                return;
            }

            Tender = cash;
        }
        else
        {
            Tender = _method switch
            {
                SaleTender.Card => SaleTenderRules.Card(),
                SaleTender.Account => SaleTenderRules.Account(),
                _ => SaleTenderRules.Qr(),
            };
        }

        DialogResult = true;
    }
}
