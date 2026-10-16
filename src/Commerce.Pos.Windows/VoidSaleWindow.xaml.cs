using System.Windows;
using System.Windows.Input;
using Commerce.BranchNode;
using Commerce.Domain.Discounts;

namespace Commerce.Pos.Windows;

/// <summary>
/// The void prompt: a reason and the branch PIN, the same authorization a discount needs (and the same lockout after
/// repeated wrong attempts). It only decides and reports; the host voids the sale with the granted authorization.
/// </summary>
public partial class VoidSaleWindow : Window
{
    private readonly IDiscountAuthorizer _authorizer;
    private readonly Guid _operatorId;

    public VoidSaleWindow(IDiscountAuthorizer authorizer, Guid operatorId, string subject, string heading = "Anular venta")
    {
        InitializeComponent();
        Title = HeadingText.Text = heading;
        VoidButton.Content = heading;

        _authorizer = authorizer;
        _operatorId = operatorId;
        SubjectText.Text = subject;

        var availability = authorizer.GetAvailability();
        if (availability.State == DiscountAvailability.NotConfigured)
        {
            Disable("No se puede anular: esta sucursal no tiene un PIN de autorización. Pídale a un administrador que lo defina y sincronice la terminal.");
        }
        else if (availability.State == DiscountAvailability.Locked)
        {
            Disable($"Demasiados intentos fallidos. Vuelva a intentar en {Math.Ceiling((availability.LockedFor ?? TimeSpan.Zero).TotalMinutes):0} min.");
        }
    }

    public string Reason { get; private set; } = string.Empty;

    public DiscountAuthorization? Authorization { get; private set; }

    private void Disable(string message)
    {
        VoidButton.IsEnabled = false;
        ReasonTextBox.IsEnabled = false;
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
        if (e.Key == Key.Enter && VoidButton.IsEnabled)
        {
            e.Handled = true;
            Confirm();
        }
    }

    private void VoidButton_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        var reason = ReasonTextBox.Text.Trim();
        if (reason.Length == 0)
        {
            ShowMessage("Escriba el motivo de la anulación.");
            ReasonTextBox.Focus();
            return;
        }

        if (reason.Length > BranchNodeService.MaxVoidReasonLength)
        {
            ShowMessage($"El motivo puede tener hasta {BranchNodeService.MaxVoidReasonLength} caracteres.");
            ReasonTextBox.Focus();
            return;
        }

        var outcome = _authorizer.Authorize(PinBox.Password, _operatorId);
        switch (outcome.Kind)
        {
            case DiscountAuthorizationKind.Granted:
                Reason = reason;
                Authorization = outcome.Authorization;
                DialogResult = true;
                return;

            case DiscountAuthorizationKind.Denied:
                ShowMessage(outcome.Message ?? "PIN incorrecto.");
                PinBox.Clear();
                PinBox.Focus();
                return;

            default:
                Disable(outcome.Message ?? "La anulación no está disponible.");
                return;
        }
    }
}
