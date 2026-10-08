using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Commerce.Domain.Discounts;

namespace Commerce.Pos.Windows;

/// <summary>What the operator chose in the discount prompt.</summary>
public enum DiscountWindowResult
{
    Cancelled,

    /// <summary>A percentage was accepted together with a granted <see cref="DiscountAuthorization"/>.</summary>
    Applied,

    /// <summary>The existing discount was removed; removing needs no authorization.</summary>
    Removed,
}

/// <summary>
/// The discount prompt: the percentage plus the branch PIN, asked together
/// because every add or change of a discount is authorized. It only decides
/// and reports; the host applies the outcome to the cart. When discounts are
/// unavailable (the branch has no PIN, or the prompt is locked) it says why and
/// disables Apply, but still lets the operator remove an existing discount.
/// </summary>
public partial class DiscountWindow : Window
{
    private readonly IDiscountAuthorizer _authorizer;
    private readonly Guid _operatorId;

    public DiscountWindow(
        IDiscountAuthorizer authorizer, Guid operatorId, string heading, string subject, decimal? currentPercent)
    {
        InitializeComponent();

        _authorizer = authorizer;
        _operatorId = operatorId;
        Title = heading;
        HeadingText.Text = heading;
        SubjectText.Text = subject;
        PercentTextBox.Text = currentPercent?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
        RemoveDiscountButton.Visibility = currentPercent is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.Content = currentPercent is null ? "Aplicar descuento" : "Cambiar descuento";

        var availability = authorizer.GetAvailability();
        if (availability.State == DiscountAvailability.NotConfigured)
        {
            Disable("Los descuentos no están disponibles: esta sucursal no tiene un PIN de descuentos. Pídale a un administrador que lo defina y sincronice la terminal.");
        }
        else if (availability.State == DiscountAvailability.Locked)
        {
            Disable($"Demasiados intentos fallidos. Vuelva a intentar en {Math.Ceiling((availability.LockedFor ?? TimeSpan.Zero).TotalMinutes):0} min.");
        }
    }

    public DiscountWindowResult Result { get; private set; } = DiscountWindowResult.Cancelled;

    public decimal Percent { get; private set; }

    public DiscountAuthorization? Authorization { get; private set; }

    private void Disable(string message)
    {
        ApplyButton.IsEnabled = false;
        PercentTextBox.IsEnabled = false;
        PinBox.IsEnabled = false;
        ShowMessage(message);
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageBorder.Visibility = Visibility.Visible;
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ApplyButton.IsEnabled)
        {
            e.Handled = true;
            Apply();
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        if (!DiscountPercentInput.TryParse(PercentTextBox.Text, out var percent))
        {
            ShowMessage("Ingrese un porcentaje mayor que 0 y hasta 100, con hasta 2 decimales.");
            PercentTextBox.Focus();
            PercentTextBox.SelectAll();
            return;
        }

        var outcome = _authorizer.Authorize(PinBox.Password, _operatorId);
        switch (outcome.Kind)
        {
            case DiscountAuthorizationKind.Granted:
                Percent = percent;
                Authorization = outcome.Authorization;
                Result = DiscountWindowResult.Applied;
                DialogResult = true;
                return;

            case DiscountAuthorizationKind.Denied:
                ShowMessage(outcome.Message ?? "PIN incorrecto.");
                PinBox.Clear();
                PinBox.Focus();
                return;

            default:
                // Locked (this attempt was the last one) or no PIN: nothing more can be tried here.
                Disable(outcome.Message ?? "Los descuentos no están disponibles.");
                return;
        }
    }

    private void RemoveDiscountButton_Click(object sender, RoutedEventArgs e)
    {
        Result = DiscountWindowResult.Removed;
        DialogResult = true;
    }
}
