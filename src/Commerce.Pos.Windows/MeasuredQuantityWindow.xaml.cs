using System.Windows;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// The small kilos prompt of a weighted (or bulk) product: a numeric box, Enter confirms and Esc cancels. Adding opens it
/// empty; editing a line prefills its current kilos (with the organization's decimal separator, T5). It only reads and
/// validates the quantity (<see cref="SaleQuantity"/>); the host applies it to the cart. A measure above
/// <see cref="SaleQuantity.ConfirmAboveKilos"/> is confirmed in the prompt itself (T6): "Sí" returns it, "No" (or Esc)
/// returns to the input with the typed value selected. Only <c>RequestMeasuredQuantity</c> opens it, so adding, the
/// card's "-" and editing a line all ask the same way.
/// </summary>
public partial class MeasuredQuantityWindow : Window
{
    private readonly string _quantityBehavior;
    private readonly string _productName;
    private readonly QuantityFormat _quantityFormat;
    private decimal _pending;

    public MeasuredQuantityWindow(
        string heading, string productName, string subject, string quantityBehavior, decimal? current, QuantityFormat quantityFormat)
    {
        InitializeComponent();

        _quantityBehavior = quantityBehavior;
        _productName = productName;
        _quantityFormat = quantityFormat;
        Title = heading;
        HeadingText.Text = heading;
        SubjectText.Text = subject;
        if (quantityBehavior != SaleQuantity.Weighted)
        {
            UnitLabelText.Text = $"Cantidad (hasta {SaleQuantity.MeasuredDecimals} decimales)";
            KilosTextBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Cantidad");
        }

        KilosTextBox.Text = current is { } quantity ? SaleQuantity.EditText(quantity, quantityBehavior, quantityFormat) : string.Empty;
        Loaded += (_, _) => KilosTextBox.SelectAll();
        PreviewKeyDown += Window_PreviewKeyDown;
    }

    /// <summary>The confirmed quantity; only set when the dialog result is true.</summary>
    public decimal Quantity { get; private set; }

    private bool IsAskingToConfirm => ConfirmationPanel.Visibility == Visibility.Visible;

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

        if (SaleQuantity.NeedsConfirmation(quantity))
        {
            // Preventive, never a cap: the operator says whether the measure is right. "No" has the focus, so a second
            // Enter never confirms an unusual measure by accident.
            _pending = quantity;
            ConfirmationText.Text = SaleQuantity.ConfirmationQuestion(quantity, _quantityBehavior, _productName, _quantityFormat);
            ShowConfirmation(true);
            NoButton.Focus();
            return;
        }

        Quantity = quantity;
        DialogResult = true;
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        Quantity = _pending;
        DialogResult = true;
    }

    private void NoButton_Click(object sender, RoutedEventArgs e) => ReturnToInput();

    // While asking, Esc means "No" (back to the input), not "cancel the whole prompt".
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsAskingToConfirm && e.Key == Key.Escape)
        {
            e.Handled = true;
            ReturnToInput();
        }
    }

    private void ReturnToInput()
    {
        ShowConfirmation(false);
        KilosTextBox.Focus();
        KilosTextBox.SelectAll();
    }

    private void ShowConfirmation(bool asking)
    {
        ConfirmationPanel.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
        ActionsPanel.Visibility = asking ? Visibility.Collapsed : Visibility.Visible;
        KilosTextBox.IsReadOnly = asking;
    }
}
