using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows.Controls;

/// <summary>
/// The sale's customer picker with a search box: the operator types part of the name, CUIT or locality and picks with
/// the mouse or with the arrows and Enter (<see cref="SaleCustomerPicker.Filter"/> decides what matches). It only
/// REPORTS a choice through <see cref="CustomerChosen"/>; the host applies it to the cart and then sets
/// <see cref="SelectedCustomerId"/> to the buyer the cart actually has, so a refused change shows the previous buyer.
/// </summary>
public partial class CustomerSearchPicker : UserControl
{
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    private readonly PopupReopenGuard _reopenGuard = new();
    private IReadOnlyList<SaleCustomerPickerItem> _items = [new SaleCustomerPickerItem(null, SaleCustomerPicker.WalkInLabel)];
    private Guid? _selectedCustomerId;

    public CustomerSearchPicker()
    {
        InitializeComponent();
        RefreshLabel();
    }

    /// <summary>The operator picked a row (walk-in included); the host decides whether the sale can change to it.</summary>
    public event EventHandler<SaleCustomerPickerItem>? CustomerChosen;

    /// <summary>Every row the picker offers, walk-in first (<see cref="SaleCustomerPicker.BuildItems"/>).</summary>
    public IReadOnlyList<SaleCustomerPickerItem> Items
    {
        get => _items;
        set
        {
            _items = value;
            RefreshLabel();
            if (SearchPopup.IsOpen)
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>The buyer the sale has (null = walk-in). Setting it never raises <see cref="CustomerChosen"/>.</summary>
    public Guid? SelectedCustomerId
    {
        get => _selectedCustomerId;
        set
        {
            _selectedCustomerId = value;
            RefreshLabel();
        }
    }

    private void RefreshLabel()
    {
        var selected = _items.FirstOrDefault(i => i.CustomerId == _selectedCustomerId);
        SelectedLabelText.Text = selected?.Label ?? SaleCustomerPicker.WalkInLabel;
        OpenButton.ToolTip = selected?.HasDetail == true
            ? $"{selected.Label} — {selected.Detail}"
            : "Elegir o buscar el cliente de la venta";
    }

    private void OpenButton_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _reopenGuard.NotifyPressed(Clock.Elapsed);

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        // StaysOpen=False closes the popup on the press; the Click that follows must not reopen it.
        if (!SearchPopup.IsOpen && _reopenGuard.ShouldOpenOnClick(Clock.Elapsed))
        {
            SearchPopup.IsOpen = true;
        }
    }

    private void SearchPopup_Opened(object? sender, EventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        ApplyFilter();

        // The popup's window is not ready to take keyboard focus while it is opening: focusing right here leaves no caret.
        // Deferred to after its first render, the caret is in the search box and typing searches straight away.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
        });
    }

    private void SearchPopup_Closed(object? sender, EventArgs e) => _reopenGuard.NotifyClosed(Clock.Elapsed);

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var term = SearchTextBox.Text;
        var matches = SaleCustomerPicker.Filter(_items, term);
        ResultsListBox.ItemsSource = matches;
        SearchPlaceholderText.Visibility = string.IsNullOrEmpty(term) ? Visibility.Visible : Visibility.Collapsed;

        var hasTerm = !string.IsNullOrWhiteSpace(term);
        NoMatchesText.Visibility = hasTerm && matches.All(m => m.CustomerId is null) ? Visibility.Visible : Visibility.Collapsed;

        // While searching, Enter takes the first matching customer; otherwise the current buyer is highlighted.
        ResultsListBox.SelectedItem = hasTerm
            ? matches.FirstOrDefault(m => m.CustomerId is not null) ?? matches.FirstOrDefault()
            : matches.FirstOrDefault(m => m.CustomerId == _selectedCustomerId) ?? matches.FirstOrDefault();
        if (ResultsListBox.SelectedItem is { } highlighted)
        {
            ResultsListBox.ScrollIntoView(highlighted);
        }
    }

    private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                ChooseHighlighted();
                e.Handled = true;
                break;
            case Key.Escape:
                SearchPopup.IsOpen = false;
                OpenButton.Focus();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var count = ResultsListBox.Items.Count;
        if (count == 0)
        {
            return;
        }

        var index = Math.Clamp(ResultsListBox.SelectedIndex + delta, 0, count - 1);
        ResultsListBox.SelectedIndex = index;
        ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
    }

    private void ResultsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ResultsListBox, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: SaleCustomerPickerItem item })
        {
            Choose(item);
        }
    }

    private void ResultsListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ChooseHighlighted();
            e.Handled = true;
        }
    }

    private void ChooseHighlighted()
    {
        if (ResultsListBox.SelectedItem is SaleCustomerPickerItem item)
        {
            Choose(item);
        }
    }

    private void Choose(SaleCustomerPickerItem item)
    {
        SearchPopup.IsOpen = false;
        CustomerChosen?.Invoke(this, item);
    }
}
