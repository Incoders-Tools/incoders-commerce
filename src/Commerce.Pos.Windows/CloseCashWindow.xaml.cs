using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Commerce.Domain.CashSessions;

namespace Commerce.Pos.Windows;

/// <summary>
/// The close-cash dialog (pos-cash-session "Closing a Cash Session"): shows what
/// the system expects (expected cash, card and QR totals, sale count), asks for
/// the counted cash and shows the difference live. It only decides and reports
/// the <see cref="CountedCash"/>; the host closes the session.
/// </summary>
public partial class CloseCashWindow : Window
{
    private readonly decimal _expectedCash;

    public CloseCashWindow(CashSession session, CashSessionSummary summary)
    {
        InitializeComponent();

        _expectedCash = summary.ExpectedCash;
        OpenedText.Text = $"Abierta a las {session.OpenedAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} con {session.OpeningFloat.ToString("C", CultureInfo.CurrentCulture)} iniciales.";
        SaleCountText.Text = summary.SaleCount.ToString(CultureInfo.CurrentCulture);
        CardTotalText.Text = summary.CardTotal.ToString("C", CultureInfo.CurrentCulture);
        QrTotalText.Text = summary.QrTotal.ToString("C", CultureInfo.CurrentCulture);
        ExpectedCashText.Text = _expectedCash.ToString("C", CultureInfo.CurrentCulture);
        ExpectedBreakdownText.Text =
            $"Iniciales {summary.OpeningFloat.ToString("C", CultureInfo.CurrentCulture)} + efectivo cobrado {summary.CashKept.ToString("C", CultureInfo.CurrentCulture)} (ya descontado el vuelto).";
        ConfirmButton.IsEnabled = false;
        Loaded += (_, _) => CountedCashTextBox.Focus();
    }

    /// <summary>The counted cash to record; set only when the operator confirmed.</summary>
    public decimal? CountedCash { get; private set; }

    private void CountedCashTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var entry = CashSessionInput.EvaluateClose(_expectedCash, CountedCashTextBox.Text);
        ConfirmButton.IsEnabled = entry.IsValid;

        if (entry.Difference is { } difference)
        {
            DifferenceText.Text = CashSessionInput.DifferenceLabel(difference);
            // A resource reference, not the brush itself: it follows a theme switch.
            DifferenceText.SetResourceReference(TextBlock.ForegroundProperty, difference == 0m ? "AccentTextBrush" : "DangerBrush");
        }
        else
        {
            DifferenceText.Text = "—";
            DifferenceText.ClearValue(TextBlock.ForegroundProperty);
        }

        MessageText.Text = entry.Message ?? string.Empty;
        MessageBorder.Visibility = entry.Message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CountedCashTextBox_KeyDown(object sender, KeyEventArgs e)
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
        var entry = CashSessionInput.EvaluateClose(_expectedCash, CountedCashTextBox.Text);
        if (!entry.IsValid)
        {
            return;
        }

        CountedCash = entry.Counted;
        DialogResult = true;
    }
}
