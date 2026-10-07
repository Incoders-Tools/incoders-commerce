using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Commerce.BranchNode;

namespace Commerce.Pos.Windows;

/// <summary>
/// "Ventas": the sales committed at this terminal, one day at a time (today by default), newest first, with their
/// detail and the void of a sale of the open cash session. Everything is local (the branch database), so the section is
/// never busy. Customer payments of current account debt collected at this terminal ("cobros") are listed with the
/// sales. Voiding is delegated to the host (<see cref="SalesView(BranchNodeService, Func{Guid?}, Func{IHistoryRow, SaleVoidOutcome?})"/>),
/// which asks for the reason and PIN, voids and refreshes the cash session; this view only reloads afterwards.
/// </summary>
public partial class SalesView : UserControl, ISectionView
{
    private readonly BranchNodeService _service;
    private readonly Func<Guid?> _openCashSessionId;
    private readonly Func<IHistoryRow, SaleVoidOutcome?> _voidSale;
    private IReadOnlyList<IHistoryRow> _rows = [];
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);

    public SalesView(BranchNodeService service, Func<Guid?> openCashSessionId, Func<IHistoryRow, SaleVoidOutcome?> voidSale)
    {
        InitializeComponent();
        _service = service;
        _openCashSessionId = openCashSessionId;
        _voidSale = voidSale;
        Loaded += (_, _) => SearchTextBox.Focus();
        Reload();
    }

    public bool IsBusy => false;

    public void CancelPending()
    {
    }

    // Local reads never go idle later: nothing is ever in flight.
    public event Action? Idle
    {
        add { }
        remove { }
    }

    public void Dispose()
    {
    }

    /// <summary>Reads the day again (after a void, or when the day changes), keeping the selection when possible.</summary>
    public void Reload(Guid? select = null)
    {
        select ??= (SalesListBox.SelectedItem as IHistoryRow)?.Id;
        var (fromUtc, toUtc) = SaleHistory.DayRange(_day);
        _rows = SaleHistory.Rows(
            _service.ListSales(fromUtc, toUtc), _service.ListCustomerPayments(fromUtc, toUtc), _openCashSessionId(),
            _service.ListCashMovements(fromUtc, toUtc));

        var today = DateOnly.FromDateTime(DateTime.Now);
        DayText.Text = _day == today
            ? $"Hoy, {_day.ToString("d 'de' MMMM", CultureInfo.CurrentCulture)}"
            : _day.ToString("dddd d 'de' MMMM", CultureInfo.CurrentCulture);
        NextDayButton.IsEnabled = _day < today;
        TodayButton.IsEnabled = _day != today;
        SummaryText.Text = SaleHistory.Summary(_rows);
        ApplyFilter(select);
    }

    private void ApplyFilter(Guid? select)
    {
        var visible = SaleHistory.Filter(_rows, SearchTextBox.Text);
        SalesListBox.ItemsSource = visible;
        SearchPlaceholderText.Visibility = SearchTextBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _rows.Count == 0 ? "No hay ventas ni cobros en este día." : "Nada coincide con la búsqueda.";
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SalesListBox.SelectedItem = visible.FirstOrDefault(row => row.Id == select) ?? visible.FirstOrDefault();
        ShowDetail(SalesListBox.SelectedItem as IHistoryRow);
    }

    private void ShowDetail(IHistoryRow? row)
    {
        NoSelectionText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        switch (row)
        {
            case SaleHistoryRow sale:
                ShowSaleDetail(sale);
                break;
            case PaymentHistoryRow payment:
                ShowPaymentDetail(payment);
                break;
            case CashMovementHistoryRow movement:
                ShowCashMovementDetail(movement);
                break;
            default:
                return;
        }

        var unavailable = SaleHistory.VoidUnavailableReason(row);
        VoidUnavailableText.Text = unavailable ?? string.Empty;
        VoidUnavailableText.Visibility = unavailable is null ? Visibility.Collapsed : Visibility.Visible;
        VoidButton.Visibility = row.CanVoid ? Visibility.Visible : Visibility.Collapsed;
        VoidButton.Content = row is PaymentHistoryRow ? "Anular cobro" : "Anular venta";
        DetailTotalText.Text = row.TotalText;
        DetailTotalText.TextDecorations = row.IsVoided ? TextDecorations.Strikethrough : null;
        VoidInfoBorder.Visibility = row.IsVoided ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A cash movement: what, why, and who authorized a withdrawal.</summary>
    private void ShowCashMovementDetail(CashMovementHistoryRow row)
    {
        var movement = row.Movement;
        DetailNumberText.Text = row.NumberText;
        DetailWhenText.Text = movement.OccurredAtUtc.ToLocalTime().ToString("dddd d 'de' MMMM, HH:mm", CultureInfo.CurrentCulture);
        DetailCustomerText.Text = movement.Description;
        DetailTenderText.Text = $"Motivo: {movement.Reason}";
        DetailDiscountText.Text = movement.Authorization is null ? string.Empty : "Autorizado con el PIN de la sucursal.";
        DetailDiscountText.Visibility = movement.Authorization is null ? Visibility.Collapsed : Visibility.Visible;
        DetailLinesItemsControl.ItemsSource = new[]
        {
            row.IsWithdrawal ? "Salió efectivo de la caja: baja el efectivo esperado del cierre." : "Entró efectivo a la caja: sube el efectivo esperado del cierre.",
            "Si fue un error, registre el movimiento contrario o pídale a un administrador que lo anule o corrija en Tesorería (web).",
        };
    }

    /// <summary>A customer payment: who paid, how much, how, and why it was voided when it was.</summary>
    private void ShowPaymentDetail(PaymentHistoryRow row)
    {
        var culture = CultureInfo.CurrentCulture;
        var payment = row.Payment;
        DetailNumberText.Text = "Cobro de cuenta corriente";
        DetailWhenText.Text = payment.ReceivedAtUtc.ToLocalTime().ToString("dddd d 'de' MMMM, HH:mm", culture);
        DetailCustomerText.Text = $"Cliente: {row.CustomerText}";
        DetailTenderText.Text = $"Pagó con: {row.TenderText}" +
            (payment.Tender.AmountReceived is { } received ? $" · recibido {received.ToString("C", culture)}" : string.Empty) +
            (payment.Tender.ChangeGiven is { } change && change > 0 ? $" · vuelto {change.ToString("C", culture)}" : string.Empty);
        DetailDiscountText.Text = payment.Note ?? string.Empty;
        DetailDiscountText.Visibility = payment.Note is null ? Visibility.Collapsed : Visibility.Visible;
        DetailLinesItemsControl.ItemsSource = new[] { "Baja la deuda del cliente en su cuenta corriente." };
        if (payment.Void is { } voided)
        {
            VoidInfoText.Text = $"Anulado el {voided.VoidedAtUtc.ToLocalTime().ToString("d/M HH:mm", culture)}. Motivo: {voided.Reason}";
        }
    }

    private void ShowSaleDetail(SaleHistoryRow row)
    {
        var culture = CultureInfo.CurrentCulture;
        var entry = row.Entry;
        DetailNumberText.Text = $"Venta {row.NumberText}";
        DetailWhenText.Text = entry.OccurredAtUtc.ToLocalTime().ToString("dddd d 'de' MMMM, HH:mm", culture);
        DetailCustomerText.Text = $"Cliente: {row.CustomerText}";
        DetailTenderText.Text = entry.Tender is { } tender
            ? $"Cobro: {row.TenderText}" +
              (tender.AmountReceived is { } received ? $" · recibido {received.ToString("C", culture)}" : string.Empty) +
              (tender.ChangeGiven is { } change && change > 0 ? $" · vuelto {change.ToString("C", culture)}" : string.Empty)
            : "Cobro: sin medio de pago registrado";

        DetailLinesItemsControl.ItemsSource = entry.SaleKind == "Manual"
            ? ["Venta manual por importe (sin productos)."]
            : _service.ListSaleLines(entry.SaleId).Select(SaleHistory.LineText).ToList();

        var discount = _service.GetSaleEffect(entry.SaleId) is { SaleDiscountPercent: { } percent } effect
            ? $"Descuento de la venta: {percent.ToString("0.##", culture)} % (−{(effect.SaleDiscountAmount ?? 0m).ToString("C", culture)})"
            : null;
        DetailDiscountText.Text = discount ?? string.Empty;
        DetailDiscountText.Visibility = discount is null ? Visibility.Collapsed : Visibility.Visible;
        if (entry.Void is { } voided)
        {
            VoidInfoText.Text =
                $"Anulada el {voided.VoidedAtUtc.ToLocalTime().ToString("d/M HH:mm", culture)}. Motivo: {voided.Reason}";
        }
    }

    private void SalesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDetail(SalesListBox.SelectedItem as IHistoryRow);

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter((SalesListBox.SelectedItem as IHistoryRow)?.Id);

    private void PreviousDayButton_Click(object sender, RoutedEventArgs e) => ChangeDay(_day.AddDays(-1));

    private void NextDayButton_Click(object sender, RoutedEventArgs e) => ChangeDay(_day.AddDays(1));

    private void TodayButton_Click(object sender, RoutedEventArgs e) => ChangeDay(DateOnly.FromDateTime(DateTime.Now));

    private void ChangeDay(DateOnly day)
    {
        _day = day;
        Reload();
    }

    private void VoidButton_Click(object sender, RoutedEventArgs e)
    {
        if (SalesListBox.SelectedItem is not IHistoryRow { CanVoid: true } row)
        {
            return;
        }

        if (_voidSale(row) is { } outcome)
        {
            StatusMessageText.Text = row is PaymentHistoryRow
                ? SaleHistory.PaymentVoidOutcomeMessage(outcome)
                : SaleHistory.VoidOutcomeMessage(outcome, row.NumberText);
            StatusMessageText.Visibility = Visibility.Visible;
            Reload(row.Id);
        }
    }
}
