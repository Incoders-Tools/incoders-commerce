using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Commerce.BranchNode;
using Commerce.Domain.CashSessions;
using Commerce.Domain.Discounts;

namespace Commerce.Pos.Windows;

/// <summary>
/// "Movimiento de caja": money taken out of the drawer (a withdrawal, which needs the branch PIN) or put into it, outside
/// a sale, with where it goes or comes from and a reason. It only decides and reports; the host records it.
/// </summary>
public partial class CashMovementWindow : Window
{
    private sealed record Choice(string Value, string Label);

    private static readonly Choice[] WithdrawalChoices =
    [
        new(CashMovement.Safe, "A la caja fuerte"),
        new(CashMovement.Bank, "Para depositar en el banco"),
        new(CashMovement.Expense, "Para pagar un gasto"),
        new(CashMovement.Other, "Otro"),
    ];

    private static readonly Choice[] DepositChoices =
    [
        new(CashMovement.Safe, "Desde la caja fuerte"),
        new(CashMovement.Other, "Otro"),
    ];

    private readonly IDiscountAuthorizer _authorizer;
    private readonly Guid _operatorId;
    private readonly decimal _expectedCash;
    private bool _ready;

    public CashMovementWindow(IDiscountAuthorizer authorizer, Guid operatorId, decimal expectedCash)
    {
        InitializeComponent();
        _authorizer = authorizer;
        _operatorId = operatorId;
        _expectedCash = expectedCash;
        ExpectedCashText.Text = $"Efectivo que debería haber en la caja ahora: {expectedCash.ToString("C", CultureInfo.CurrentCulture)}.";
        _ready = true;
        ApplyKind();
    }

    public string Kind => DepositRadio.IsChecked == true ? CashMovement.Deposit : CashMovement.Withdrawal;

    public string Counterpart => (CounterpartComboBox.SelectedItem as Choice)?.Value ?? CashMovement.Other;

    public decimal Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    /// <summary>The PIN authorization of a withdrawal; null for a deposit.</summary>
    public DiscountAuthorization? Authorization { get; private set; }

    private void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            ApplyKind();
        }
    }

    private void ApplyKind()
    {
        var withdrawal = Kind == CashMovement.Withdrawal;
        CounterpartLabel.Text = withdrawal ? "¿A dónde va el dinero?" : "¿De dónde viene el dinero?";
        CounterpartComboBox.ItemsSource = withdrawal ? WithdrawalChoices : DepositChoices;
        CounterpartComboBox.SelectedIndex = 0;
        PinPanel.Visibility = withdrawal ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = withdrawal ? "Registrar retiro" : "Registrar ingreso";
        Validate();
    }

    private void Counterpart_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
        {
            ExplanationText.Text = CashMovementInput.Explanation(Kind, Counterpart);
        }
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (_ready)
        {
            Validate();
        }
    }

    private CashMovementEntry Validate()
    {
        var entry = CashMovementInput.Evaluate(Kind, AmountTextBox.Text, ReasonTextBox.Text, _expectedCash);
        ConfirmButton.IsEnabled = entry.IsValid;
        if (entry.Message is { } message)
        {
            MessageText.Text = message;
            MessageBorder.Visibility = Visibility.Visible;
        }
        else
        {
            MessageBorder.Visibility = Visibility.Collapsed;
        }

        return entry;
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ConfirmButton.IsEnabled)
        {
            e.Handled = true;
            Confirm();
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        var entry = Validate();
        if (!entry.IsValid || entry.Amount is not { } amount)
        {
            return;
        }

        if (Kind == CashMovement.Withdrawal)
        {
            var outcome = _authorizer.Authorize(PinBox.Password, _operatorId);
            if (outcome.Kind != DiscountAuthorizationKind.Granted)
            {
                MessageText.Text = outcome.Message ?? "PIN incorrecto.";
                MessageBorder.Visibility = Visibility.Visible;
                PinBox.Clear();
                PinBox.Focus();
                return;
            }

            Authorization = outcome.Authorization;
        }

        Amount = amount;
        Reason = ReasonTextBox.Text.Trim();
        DialogResult = true;
    }
}
