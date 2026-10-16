using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows.Controls;

/// <summary>
/// A catalog card bound to a <see cref="ProductCardViewModel"/>. Clicking the
/// card or the plus button asks the host to add one unit; the minus button
/// asks it to remove one. The host reads the request's DataContext.
/// </summary>
public partial class ProductCard : UserControl
{
    public static readonly RoutedEvent IncrementRequestedEvent =
        EventManager.RegisterRoutedEvent(nameof(IncrementRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ProductCard));

    public static readonly RoutedEvent DecrementRequestedEvent =
        EventManager.RegisterRoutedEvent(nameof(DecrementRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ProductCard));

    public ProductCard() => InitializeComponent();

    public event RoutedEventHandler IncrementRequested { add => AddHandler(IncrementRequestedEvent, value); remove => RemoveHandler(IncrementRequestedEvent, value); }

    public event RoutedEventHandler DecrementRequested { add => AddHandler(DecrementRequestedEvent, value); remove => RemoveHandler(DecrementRequestedEvent, value); }

    private void CardChrome_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ProductCardViewModel { HasPrice: true })
        {
            RaiseEvent(new RoutedEventArgs(IncrementRequestedEvent, this));
        }
    }

    private void IncrementButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(IncrementRequestedEvent, this));

    private void DecrementButton_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(DecrementRequestedEvent, this));
}
