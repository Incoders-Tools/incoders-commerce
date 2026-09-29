using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Commerce.Pos.Windows.Controls;

public sealed class SaleLineQuantityEventArgs(Guid presentationId, decimal quantity) : EventArgs
{
    public Guid PresentationId { get; } = presentationId;

    public decimal Quantity { get; } = quantity;
}

/// <summary>
/// The pending-sale table: product, SKU, quantity, unit price, total and the
/// edit/delete actions. It only presents <see cref="ScannedSaleLineViewModel"/>
/// rows and reports what the operator asked for; the host applies it to the cart.
/// </summary>
public partial class SaleLinesTable : UserControl
{
    private INotifyCollectionChanged? _observed;

    public SaleLinesTable()
    {
        InitializeComponent();
        UpdateEmptyState();
    }

    public event EventHandler<SaleLineQuantityEventArgs>? QuantityEdited;

    public event EventHandler<Guid>? LineRemoved;

    /// <summary>The operator asked to add, change or remove the discount of a line; the host authorizes and applies it.</summary>
    public event EventHandler<Guid>? LineDiscountRequested;

    public IEnumerable? ItemsSource
    {
        get => RowsItemsControl.ItemsSource;
        set
        {
            if (_observed is not null)
            {
                _observed.CollectionChanged -= OnItemsChanged;
            }

            RowsItemsControl.ItemsSource = value;
            _observed = value as INotifyCollectionChanged;
            if (_observed is not null)
            {
                _observed.CollectionChanged += OnItemsChanged;
            }

            UpdateEmptyState();
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState() =>
        EmptyState.Visibility = RowsItemsControl.HasItems ? Visibility.Collapsed : Visibility.Visible;

    // Keep the header aligned with the rows when the vertical scrollbar takes space.
    private void RowsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        HeaderRow.Margin = new Thickness(
            0, 0, RowsScrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0, 0);

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ScannedSaleLineViewModel line)
        {
            LineRemoved?.Invoke(this, line.PresentationId);
        }
    }

    private void DiscountButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ScannedSaleLineViewModel line)
        {
            LineDiscountRequested?.Invoke(this, line.PresentationId);
        }
    }

    private void EditPopup_Opened(object? sender, EventArgs e)
    {
        var popup = (Popup)sender!;
        if (popup.DataContext is ScannedSaleLineViewModel line && FindEditBox(popup) is { } box)
        {
            box.Text = line.QuantityText;
            box.Focus();
            box.SelectAll();
        }
    }

    private void QuantityEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Apply((FrameworkElement)sender);
            e.Handled = true;
        }
    }

    private void ApplyQuantityButton_Click(object sender, RoutedEventArgs e) => Apply((FrameworkElement)sender);

    private void Apply(FrameworkElement source)
    {
        var popup = FindPopup(source);
        if (popup?.DataContext is not ScannedSaleLineViewModel line || FindEditBox(popup) is not { } box)
        {
            return;
        }

        // Accept both "2.5" and "2,5" (Spanish keyboards).
        var text = box.Text.Trim().Replace(',', '.');
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity))
        {
            popup.IsOpen = false;
            QuantityEdited?.Invoke(this, new SaleLineQuantityEventArgs(line.PresentationId, quantity));
        }
        else
        {
            box.SelectAll();
        }
    }

    private static Popup? FindPopup(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current is not null and not Popup)
        {
            current = LogicalTreeHelper.GetParent(current);
        }

        return current as Popup;
    }

    private static TextBox? FindEditBox(Popup popup) =>
        ((popup.Child as Border)?.Child as Panel)?.Children.OfType<TextBox>().FirstOrDefault();
}
