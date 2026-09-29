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

    public static readonly DependencyProperty SubtotalProperty = DependencyProperty.Register(
        nameof(Subtotal), typeof(decimal), typeof(TotalsPanel), new PropertyMetadata(0m, (d, _) => ((TotalsPanel)d).Refresh()));

    public static readonly DependencyProperty DiscountTotalProperty = DependencyProperty.Register(
        nameof(DiscountTotal), typeof(decimal), typeof(TotalsPanel), new PropertyMetadata(0m, (d, _) => ((TotalsPanel)d).Refresh()));

    public static readonly RoutedEvent SaleDiscountRequestedEvent =
        EventManager.RegisterRoutedEvent(nameof(SaleDiscountRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TotalsPanel));

    public static readonly RoutedEvent CommitRequestedEvent =
        EventManager.RegisterRoutedEvent(nameof(CommitRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TotalsPanel));

    public TotalsPanel()
    {
        InitializeComponent();
        Refresh();
    }

    public event RoutedEventHandler CommitRequested { add => AddHandler(CommitRequestedEvent, value); remove => RemoveHandler(CommitRequestedEvent, value); }

    /// <summary>The operator asked to add, change or remove the whole-sale discount; the host authorizes and applies it.</summary>
    public event RoutedEventHandler SaleDiscountRequested { add => AddHandler(SaleDiscountRequestedEvent, value); remove => RemoveHandler(SaleDiscountRequestedEvent, value); }

    /// <summary>The undiscounted sum of the lines.</summary>
    public decimal Subtotal
    {
        get => (decimal)GetValue(SubtotalProperty);
        set => SetValue(SubtotalProperty, value);
    }

    /// <summary>What every discount takes off; shown as a negative amount.</summary>
    public decimal DiscountTotal
    {
        get => (decimal)GetValue(DiscountTotalProperty);
        set => SetValue(DiscountTotalProperty, value);
    }

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
        SubtotalText.Text = Subtotal.ToString("C", CultureInfo.CurrentCulture);
        ScannedTotalText.Text = Total.ToString("C", CultureInfo.CurrentCulture);
        DiscountText.Text = (-DiscountTotal).ToString("C", CultureInfo.CurrentCulture);
    }

    private void SaleDiscountButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(SaleDiscountRequestedEvent, this));

    private void CommitButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CommitRequestedEvent, this));
}
