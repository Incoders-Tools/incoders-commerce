using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Commerce.Pos.Windows.Controls;

/// <summary>
/// Subtotal, discount, TOTAL and the tender buttons (cash, card, QR), which are
/// the way to complete a sale: each raises <see cref="TenderRequested"/> with
/// the method and the host collects the tender and commits.
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

    public static readonly RoutedEvent TenderRequestedEvent =
        EventManager.RegisterRoutedEvent(nameof(TenderRequested), RoutingStrategy.Bubble, typeof(TenderRequestedEventHandler), typeof(TotalsPanel));

    public TotalsPanel()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>The operator chose how the customer pays; the host collects the tender and commits the sale.</summary>
    public event TenderRequestedEventHandler TenderRequested { add => AddHandler(TenderRequestedEvent, value); remove => RemoveHandler(TenderRequestedEvent, value); }

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

    /// <summary>Enables the tender buttons (there is something to charge).</summary>
    public bool CanCommit
    {
        get => CashTenderButton.IsEnabled;
        set => CashTenderButton.IsEnabled = CardTenderButton.IsEnabled = QrTenderButton.IsEnabled = value;
    }

    /// <summary>
    /// The sale can go on a customer's current account: a customer is chosen. Without one the button stays disabled and
    /// says why.
    /// </summary>
    public bool AccountAvailable
    {
        get => AccountTenderButton.IsEnabled;
        set
        {
            AccountTenderButton.IsEnabled = value;
            AccountTenderButton.ToolTip = value
                ? "Se carga a la cuenta corriente del cliente: no se cobra ahora"
                : "Elegí un cliente para vender a cuenta corriente";
        }
    }

    private void Refresh()
    {
        SubtotalText.Text = Subtotal.ToString("C", CultureInfo.CurrentCulture);
        ScannedTotalText.Text = Total.ToString("C", CultureInfo.CurrentCulture);
        DiscountText.Text = (-DiscountTotal).ToString("C", CultureInfo.CurrentCulture);
    }

    private void SaleDiscountButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(SaleDiscountRequestedEvent, this));

    private void TenderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string method })
        {
            RaiseEvent(new TenderRequestedEventArgs(TenderRequestedEvent, this, method));
        }
    }
}

/// <summary>The tender method the operator picked (<see cref="Commerce.Domain.Sales.SaleTender"/> constants).</summary>
public sealed class TenderRequestedEventArgs(RoutedEvent routedEvent, object source, string method) : RoutedEventArgs(routedEvent, source)
{
    public string Method { get; } = method;
}

public delegate void TenderRequestedEventHandler(object sender, TenderRequestedEventArgs e);
