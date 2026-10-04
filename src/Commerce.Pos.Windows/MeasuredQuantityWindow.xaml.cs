using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// The small kilos prompt of a weighted (or bulk) product: a numeric box, Enter confirms and Esc cancels. Adding opens it
/// empty; editing a line prefills its current kilos. It only reads and validates the quantity (<see cref="SaleQuantity"/>);
/// the host applies it to the cart.
/// </summary>
public partial class MeasuredQuantityWindow : Window
{
    private readonly string _quantityBehavior;

    public MeasuredQuantityWindow(string heading, string subject, string quantityBehavior, decimal? current)
    {
        InitializeComponent();

        _quantityBehavior = quantityBehavior;
        Title = heading;
        HeadingText.Text = heading;
        SubjectText.Text = subject;
        if (quantityBehavior != SaleQuantity.Weighted)
        {
            UnitLabelText.Text = $"Cantidad (hasta {SaleQuantity.MeasuredDecimals} decimales)";
            KilosTextBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Cantidad");
        }

        KilosTextBox.Text = current is { } quantity ? SaleQuantity.EditText(quantity, quantityBehavior, CultureInfo.CurrentCulture) : string.Empty;
        Loaded += (_, _) => KilosTextBox.SelectAll();
    }

    /// <summary>The confirmed quantity; only set when the dialog result is true.</summary>
    public decimal Quantity { get; private set; }

    // Only digits and one decimal separator (comma or point) can be typed; a pasted text is still validated on confirm.
    private void KilosTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => char.IsAsciiDigit(c) || c is ',' or '.');

    private void KilosTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        MessageBorder.Visibility = Visibility.Collapsed;

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaleQuantity.TryParse(KilosTextBox.Text, _quantityBehavior, out var quantity, out var error))
        {
            MessageText.Text = error;
            MessageBorder.Visibility = Visibility.Visible;
            KilosTextBox.Focus();
            KilosTextBox.SelectAll();
            return;
        }

        Quantity = quantity;
        DialogResult = true;
    }
}
