using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Commerce.Pos.Windows.Controls;

/// <summary>
/// Subtotal, TOTAL and the charge action. The tender buttons (cash, card, QR)
/// are Phase 2 and stay disabled; <see cref="CommitRequested"/> is the only
/// action and keeps the existing scanned-sale commit.
/// </summary>
public partial class TotalsPanel : UserControl
{
    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(decimal), typeof(TotalsPanel), new PropertyMetadata(0m, (d, _) => ((TotalsPanel)d).Refresh()));

    public static readonly RoutedEvent CommitRequestedEvent =
        EventManager.RegisterRoutedEvent(nameof(CommitRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TotalsPanel));

    public TotalsPanel()
    {
        InitializeComponent();
        Refresh();
    }

    public event RoutedEventHandler CommitRequested { add => AddHandler(CommitRequestedEvent, value); remove => RemoveHandler(CommitRequestedEvent, value); }

    public decimal Total
    {
        get => (decimal)GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    /// <summary>Enables the charge action (there is something to charge).</summary>
    public bool CanCommit
    {
        get => CommitScannedSaleButton.IsEnabled;
        set => CommitScannedSaleButton.IsEnabled = value;
    }

    private void Refresh()
    {
        var text = Total.ToString("C", CultureInfo.CurrentCulture);
        SubtotalText.Text = text;
        ScannedTotalText.Text = text;
    }

    private void CommitButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CommitRequestedEvent, this));
}
