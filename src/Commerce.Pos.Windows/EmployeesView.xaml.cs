using System.Windows;
using Commerce.Domain.Payroll;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// Personal → Empleados: the staff of THIS terminal's branch (PRD 9.19) against the SAME <c>/employees</c> endpoints the
/// web's <c>EmployeesScreen</c> uses, over the shared <see cref="ManagementConnection"/> (device credential + the
/// signed-in operator; the server re-checks the operator and <c>ManageUsers</c> on every call). Same operations as the
/// web (docs/architecture/cross-layer-parity.md): list, create, edit, give an advance (money out of a treasury account,
/// a debit on the employee's account), read the account, and Dar de baja / Reincorporar. New staff is created in this
/// terminal's branch and an edit keeps the stored branch (the web may move an employee). Connectivity is required
/// end-to-end: a network failure is shown inline, never a local write.
/// </summary>
public partial class EmployeesView : UserControl, ISectionView
{
    private readonly EmployeeAdminClient _client;
    private readonly Guid _branchId;
    private readonly BusyController _busy;
    private readonly EmployeeList _employees;
    private IReadOnlyList<EmployeeRoleDto> _roles = [];
    private IReadOnlyList<TreasuryAccountDto>? _accounts;
    private EmployeeRecordDto? _editing;
    // employee-lifecycle E2: the overtime form proposes the day's multiplier until the operator types one.
    private bool _multiplierTouched;
    private bool _settingMultiplier;
    private OvertimeRecordDto? _deletingOvertime;

    /// <param name="branchId">This terminal's branch: the list shows its staff, new staff is created in it.</param>
    public EmployeesView(EmployeeAdminClient client, Guid branchId)
    {
        InitializeComponent();
        _client = client;
        _branchId = branchId;
        _busy = new BusyController(ApplyBusy, nameof(EmployeesView), message => ShowStatus(message, isError: true));

        FrequencyComboBox.ItemsSource = EmployeeFormRules.Frequencies;
        _employees = new EmployeeList(ToggleActiveAsync);
        _employees.EditorChanged += OnEditorChanged;
        EmployeesListView.Model = _employees.Model;

        ShowForm(EmployeeEditorPurpose.None);
        Loaded += async (_, _) => await _busy.RunAsync(PosMessages.Loading, async () =>
        {
            var roles = await _client.ListRolesAsync(_busy.Token);
            if (roles is null)
            {
                ShowStatus(PosMessages.EmployeeRolesLoadFailed, isError: true);
            }

            _roles = roles ?? [];
            await LoadEmployeesAsync();
        });
    }

    public bool IsBusy => _busy.IsBusy;

    public event Action? Idle
    {
        add => _busy.Idle += value;
        remove => _busy.Idle -= value;
    }

    public void CancelPending() => _busy.Cancel();

    /// <summary>The client is the shared management connection's: only the request in flight is cancelled.</summary>
    public void Dispose() => _busy.Cancel();

    private void ApplyBusy(bool busy, string? text)
    {
        FormPanel.IsEnabled = !busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? string.Empty;
        Cursor = busy ? Cursors.Wait : null;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "DangerBrush" : "SuccessBrush");
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadEmployeesAsync()
    {
        var employees = await _client.ListEmployeesAsync(_branchId, _busy.Token);
        if (employees is null)
        {
            ShowStatus(PosMessages.EmployeesLoadFailed, isError: true);
            return;
        }

        _employees.Model.SetItems(employees);
    }

    // ---- the editor: one form at a time ------------------------------------------------

    private async void OnEditorChanged(EmployeeEditorPurpose purpose, EmployeeRecordDto? employee)
    {
        ShowStatus(string.Empty, isError: false);
        _editing = employee;
        ShowForm(purpose);
        switch (purpose)
        {
            case EmployeeEditorPurpose.Create:
            case EmployeeEditorPurpose.Edit:
                FillFile(employee);
                if (employee is null)
                {
                    await ShowNextFileNumberAsync();
                }

                break;
            case EmployeeEditorPurpose.Advance when employee is not null:
                await OpenAdvanceAsync();
                break;
            case EmployeeEditorPurpose.Account when employee is not null:
                await OpenAccountAsync(employee);
                break;
            case EmployeeEditorPurpose.Overtime when employee is not null:
                await OpenOvertimeAsync(employee);
                break;
        }
    }

    private void ShowForm(EmployeeEditorPurpose purpose)
    {
        FilePanel.Visibility = purpose is EmployeeEditorPurpose.Create or EmployeeEditorPurpose.Edit ? Visibility.Visible : Visibility.Collapsed;
        AdvancePanel.Visibility = purpose == EmployeeEditorPurpose.Advance ? Visibility.Visible : Visibility.Collapsed;
        AccountPanel.Visibility = purpose == EmployeeEditorPurpose.Account ? Visibility.Visible : Visibility.Collapsed;
        OvertimePanel.Visibility = purpose == EmployeeEditorPurpose.Overtime ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelEditorButton_Click(object sender, RoutedEventArgs e) => _employees.Model.CloseEditor();

    // ---- the staff file ----------------------------------------------------------------

    /// <summary>Empty for "Nuevo empleado" (takes goods by default, as the web), filled from the row otherwise.</summary>
    private void FillFile(EmployeeRecordDto? employee)
    {
        RoleComboBox.ItemsSource = EmployeeFormRules.RoleOptions(_roles, employee?.RoleId);
        LastNameTextBox.Text = employee?.LastName ?? string.Empty;
        FirstNameTextBox.Text = employee?.FirstName ?? string.Empty;
        FileNumberTextBox.Text = employee is null ? string.Empty : EmployeeFileNumber.Format(employee.FileNumber);
        FileNumberHint.Text = EmployeeFormRules.FileNumberHint(null);
        FileNumberHint.Visibility = employee is null ? Visibility.Visible : Visibility.Collapsed;
        RoleComboBox.SelectedValue = employee?.RoleId ?? Guid.Empty;
        HireDateTextBox.Text = EmployeeFormRules.DateText(employee?.HireDate);
        DocumentTextBox.Text = employee?.DocumentNumber ?? string.Empty;
        CuilTextBox.Text = employee?.Cuil ?? string.Empty;
        PhoneTextBox.Text = employee?.Phone ?? string.Empty;
        EmailTextBox.Text = employee?.Email ?? string.Empty;
        AddressTextBox.Text = employee?.Address ?? string.Empty;
        FrequencyComboBox.SelectedValue = employee?.PayFrequency ?? "Monthly";
        SalaryTextBox.Text = employee is null ? string.Empty : EmployeeFormRules.SalaryText(employee.BaseSalary);
        OvertimeRateTextBox.Text = employee?.OvertimeHourlyRate is { } rate ? EmployeeFormRules.SalaryText(rate) : string.Empty;
        TakesGoodsCheckBox.IsChecked = employee is null || employee.CustomerId is not null;
        // A linked customer is never unlinked (its account may carry debt), as in the web.
        TakesGoodsCheckBox.IsEnabled = employee?.CustomerId is null;
        NotesTextBox.Text = employee?.Notes ?? string.Empty;
    }

    /// <summary>employee-lifecycle E1: tells which file number a new employee will get; the plain hint stays when unreadable.</summary>
    private async Task ShowNextFileNumberAsync()
    {
        var next = await _client.NextFileNumberAsync(_busy.Token);
        if (_editing is null && FilePanel.Visibility == Visibility.Visible)
        {
            FileNumberHint.Text = EmployeeFormRules.FileNumberHint(next);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(string.Empty, isError: false);
        if (string.IsNullOrWhiteSpace(FirstNameTextBox.Text) || string.IsNullOrWhiteSpace(LastNameTextBox.Text))
        {
            ShowStatus(PosMessages.EmployeeNameRequired, isError: true);
            return;
        }

        if (EmployeeFormRules.ParseSalary(SalaryTextBox.Text) is not { } salary)
        {
            ShowStatus(PosMessages.EmployeeSalaryInvalid, isError: true);
            return;
        }

        if (!EmployeeFormRules.TryParseFileNumber(FileNumberTextBox.Text, out var fileNumber))
        {
            ShowStatus(PosMessages.EmployeeFileNumberInvalid, isError: true);
            return;
        }

        if (!EmployeeFormRules.TryParseDate(HireDateTextBox.Text, out var hireDate))
        {
            ShowStatus(PosMessages.EmployeeHireDateInvalid, isError: true);
            return;
        }

        var editing = _editing;
        // employee-lifecycle E2: always sent, so an edit that empties it clears it (0); an older terminal sends none.
        decimal? overtimeRate = string.IsNullOrWhiteSpace(OvertimeRateTextBox.Text)
            ? editing is null ? null : 0m
            : EmployeeFormRules.ParseAmount(OvertimeRateTextBox.Text);
        if (overtimeRate is null && !string.IsNullOrWhiteSpace(OvertimeRateTextBox.Text))
        {
            ShowStatus(PosMessages.OvertimeRateInvalid, isError: true);
            return;
        }

        var roleId = RoleComboBox.SelectedValue is Guid role && role != Guid.Empty ? role : (Guid?)null;
        var request = new EmployeeRequestDto(
            editing?.BranchId ?? _branchId, fileNumber, FirstNameTextBox.Text.Trim(), LastNameTextBox.Text.Trim(),
            NullIfBlank(DocumentTextBox.Text), NullIfBlank(CuilTextBox.Text), roleId, NullIfBlank(PhoneTextBox.Text),
            NullIfBlank(EmailTextBox.Text), NullIfBlank(AddressTextBox.Text), hireDate,
            FrequencyComboBox.SelectedValue as string ?? "Monthly", salary, NullIfBlank(NotesTextBox.Text),
            TakesGoodsCheckBox.IsChecked == true, OvertimeHourlyRate: overtimeRate);

        await _busy.RunAsync(PosMessages.Saving, async () =>
        {
            var outcome = editing is null
                ? await _client.CreateEmployeeAsync(request, _busy.Token)
                : await _client.UpdateEmployeeAsync(editing.Id, request, _busy.Token);
            if (outcome.Kind != EmployeeAdminMutationKind.Succeeded)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.SaveFailed, isError: true);
                return;
            }

            _employees.Model.CloseEditor();
            ShowStatus(outcome.Employee is { } saved ? $"{saved.FullName} guardado (legajo {EmployeeFileNumber.Format(saved.FileNumber)})." : PosMessages.Saved, isError: false);
            await LoadEmployeesAsync();
        });
    }

    // ---- Dar de baja / Reincorporar ------------------------------------------------------

    private Task ToggleActiveAsync(EmployeeRecordDto employee) => _busy.RunAsync(PosMessages.UpdatingStatus, async () =>
    {
        ShowStatus(string.Empty, isError: false);
        var outcome = await _client.SetActiveAsync(employee.Id, !employee.IsActive, _busy.Token);
        if (outcome.Kind != EmployeeAdminMutationKind.Succeeded)
        {
            ShowStatus(outcome.ErrorMessage ?? PosMessages.SaveFailed, isError: true);
            return;
        }

        ShowStatus(employee.IsActive ? $"{employee.FullName} dado de baja." : $"{employee.FullName} reincorporado.", isError: false);
        await LoadEmployeesAsync();
    });

    // ---- an advance ------------------------------------------------------------------------

    private Task OpenAdvanceAsync()
    {
        AdvanceAmountTextBox.Text = string.Empty;
        AdvanceConceptTextBox.Text = string.Empty;
        AdvanceDateTextBox.Text = EmployeeFormRules.DateText(DateOnly.FromDateTime(DateTime.Now));
        return _busy.RunAsync(PosMessages.Loading, async () =>
        {
            if (_accounts is null)
            {
                var accounts = await _client.ListTreasuryAccountsAsync(_busy.Token);
                if (accounts is null)
                {
                    ShowStatus(PosMessages.TreasuryAccountsLoadFailed, isError: true);
                    return;
                }

                _accounts = EmployeeFormRules.AdvanceAccounts(accounts, _branchId);
            }

            AdvanceAccountComboBox.ItemsSource = _accounts
                .Select(account => new FormChoice(account.AccountId.ToString(), EmployeeFormRules.AccountLabel(account)))
                .ToList();
            AdvanceAccountComboBox.SelectedIndex = _accounts.Count > 0 ? 0 : -1;
        });
    }

    private async void AdvanceSaveButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(string.Empty, isError: false);
        if (_editing is not { } employee)
        {
            return;
        }

        if (EmployeeFormRules.ParseAmount(AdvanceAmountTextBox.Text) is not { } amount)
        {
            ShowStatus(PosMessages.AdvanceAmountInvalid, isError: true);
            return;
        }

        if (!Guid.TryParse(AdvanceAccountComboBox.SelectedValue as string, out var accountId))
        {
            ShowStatus(PosMessages.AdvanceAccountRequired, isError: true);
            return;
        }

        if (!EmployeeFormRules.TryParseDate(AdvanceDateTextBox.Text, out var date))
        {
            ShowStatus(PosMessages.AdvanceDateInvalid, isError: true);
            return;
        }

        var request = new EmployeeAdvanceRequestDto(amount, date, accountId, NullIfBlank(AdvanceConceptTextBox.Text));
        await _busy.RunAsync(PosMessages.Saving, async () =>
        {
            var outcome = await _client.GiveAdvanceAsync(employee.Id, request, _busy.Token);
            if (outcome.Kind != EmployeeAdminMutationKind.Succeeded)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.SaveFailed, isError: true);
                return;
            }

            _employees.Model.CloseEditor();
            ShowStatus($"Adelanto de {EmployeeList.MoneyText(amount)} a {employee.FullName} registrado: salió de la caja y se descuenta en la próxima liquidación.", isError: false);
            await LoadEmployeesAsync();
        });
    }

    // ---- the account -----------------------------------------------------------------------

    private Task OpenAccountAsync(EmployeeRecordDto employee)
    {
        AccountBalanceText.Text = EmployeeList.BalanceText(employee.Balance);
        MovementsItemsControl.ItemsSource = null;
        MovementsEmptyText.Visibility = Visibility.Collapsed;
        return _busy.RunAsync(PosMessages.Loading, async () =>
        {
            var statement = await _client.GetStatementAsync(employee.Id, _busy.Token);
            if (statement is null)
            {
                ShowStatus(PosMessages.EmployeeAccountLoadFailed, isError: true);
                return;
            }

            AccountBalanceText.Text = EmployeeList.BalanceText(statement.ClosingBalance);
            var rows = EmployeeAccountRow.From(statement);
            MovementsItemsControl.ItemsSource = rows;
            MovementsEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // ---- overtime (employee-lifecycle E2) -----------------------------------------------------

    private Task OpenOvertimeAsync(EmployeeRecordDto employee)
    {
        var hasRate = employee.OvertimeHourlyRate is not null;
        OvertimeNoRateText.Visibility = hasRate ? Visibility.Collapsed : Visibility.Visible;
        OvertimeFormPanel.Visibility = hasRate ? Visibility.Visible : Visibility.Collapsed;
        OvertimeRateText.Text = employee.OvertimeHourlyRate is { } rate ? $"Valor de la hora extra: {EmployeeList.MoneyText(rate)}" : string.Empty;
        OvertimeConfirmPanel.Visibility = Visibility.Collapsed;
        _deletingOvertime = null;
        _multiplierTouched = false;
        OvertimeHoursTextBox.Text = string.Empty;
        OvertimeNoteTextBox.Text = string.Empty;
        OvertimeDateTextBox.Text = EmployeeFormRules.DateText(DateOnly.FromDateTime(DateTime.Now));
        return _busy.RunAsync(PosMessages.Loading, () => LoadOvertimeAsync(employee));
    }

    private async Task LoadOvertimeAsync(EmployeeRecordDto employee)
    {
        var records = await _client.ListOvertimeAsync(employee.Id, _busy.Token);
        if (records is null)
        {
            ShowStatus(PosMessages.OvertimeLoadFailed, isError: true);
            return;
        }

        var rows = EmployeeOvertimeRow.From(records);
        OvertimeItemsControl.ItemsSource = rows;
        OvertimeEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OvertimeDateTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_multiplierTouched && EmployeeFormRules.TryParseDate(OvertimeDateTextBox.Text, out var date) && date is { } day)
        {
            _settingMultiplier = true;
            OvertimeMultiplierTextBox.Text = EmployeeFormRules.ProposedMultiplierText(day);
            _settingMultiplier = false;
        }

        UpdateOvertimePreview();
    }

    private void OvertimeMultiplierTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _multiplierTouched |= !_settingMultiplier;
        UpdateOvertimePreview();
    }

    private void OvertimeInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateOvertimePreview();

    private void UpdateOvertimePreview() =>
        OvertimePreviewText.Text = EmployeeFormRules.OvertimePreview(EmployeeFormRules.ParseHours(OvertimeHoursTextBox.Text),
            _editing?.OvertimeHourlyRate, EmployeeFormRules.ParseMultiplier(OvertimeMultiplierTextBox.Text)) ?? string.Empty;

    private async void OvertimeSaveButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(string.Empty, isError: false);
        if (_editing is not { } employee)
        {
            return;
        }

        if (EmployeeFormRules.ParseHours(OvertimeHoursTextBox.Text) is not { } hours)
        {
            ShowStatus(PosMessages.OvertimeHoursInvalid, isError: true);
            return;
        }

        if (EmployeeFormRules.ParseMultiplier(OvertimeMultiplierTextBox.Text) is not { } multiplier)
        {
            ShowStatus(PosMessages.OvertimeMultiplierInvalid, isError: true);
            return;
        }

        if (!EmployeeFormRules.TryParseDate(OvertimeDateTextBox.Text, out var date) || date is not { } workDate
            || workDate > DateOnly.FromDateTime(DateTime.Now))
        {
            ShowStatus(PosMessages.OvertimeDateInvalid, isError: true);
            return;
        }

        var request = new OvertimeRequestDto(workDate, hours, multiplier, NullIfBlank(OvertimeNoteTextBox.Text));
        await _busy.RunAsync(PosMessages.Saving, async () =>
        {
            var outcome = await _client.RecordOvertimeAsync(employee.Id, request, _busy.Token);
            if (outcome.Kind != EmployeeAdminMutationKind.Succeeded)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.SaveFailed, isError: true);
                return;
            }

            OvertimeHoursTextBox.Text = string.Empty;
            OvertimeNoteTextBox.Text = string.Empty;
            ShowStatus($"Horas extra de {employee.FullName} registradas: la próxima liquidación las toma.", isError: false);
            await LoadOvertimeAsync(employee);
        });
    }

    private void OvertimeDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id }
            && (OvertimeItemsControl.ItemsSource as IEnumerable<EmployeeOvertimeRow>)?.FirstOrDefault(row => row.Id == id) is { } row)
        {
            _deletingOvertime = row.Record;
            OvertimeConfirmText.Text = EmployeeFormRules.OvertimeDeleteQuestion(row.Record);
            OvertimeConfirmPanel.Visibility = Visibility.Visible;
        }
    }

    private void OvertimeCancelDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        _deletingOvertime = null;
        OvertimeConfirmPanel.Visibility = Visibility.Collapsed;
    }

    private async void OvertimeConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } employee || _deletingOvertime is not { } record)
        {
            return;
        }

        OvertimeConfirmPanel.Visibility = Visibility.Collapsed;
        _deletingOvertime = null;
        await _busy.RunAsync(PosMessages.Saving, async () =>
        {
            var outcome = await _client.DeleteOvertimeAsync(employee.Id, record.Id, _busy.Token);
            if (outcome.Kind != EmployeeAdminMutationKind.Succeeded)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.SaveFailed, isError: true);
                return;
            }

            ShowStatus("Horas extra borradas.", isError: false);
            await LoadOvertimeAsync(employee);
        });
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One overtime record as the POS lists it, in the web's words; only a pending one offers "Borrar".</summary>
public sealed record EmployeeOvertimeRow(
    Guid Id, string Title, string Detail, string Amount, Visibility DeleteVisibility, string DeleteLabel, OvertimeRecordDto Record)
{
    public static IReadOnlyList<EmployeeOvertimeRow> From(IEnumerable<OvertimeRecordDto> records) =>
        records
            .Select(record => new EmployeeOvertimeRow(
                record.Id,
                $"{EmployeeFormRules.DateText(record.WorkDate)} · {EmployeeFormRules.DecimalText(record.Hours)} h × {EmployeeFormRules.DecimalText(record.Multiplier)}",
                record.Note is null ? EmployeeFormRules.OvertimeStatusText(record) : $"{EmployeeFormRules.OvertimeStatusText(record)} · {record.Note}",
                EmployeeList.MoneyText(record.Amount),
                record.Status == Commerce.Domain.Payroll.OvertimeStatus.Pending ? Visibility.Visible : Visibility.Collapsed,
                $"Borrar horas extra del {EmployeeFormRules.DateText(record.WorkDate)}",
                record))
            .ToList();
}

/// <summary>One movement of the employee's account as the POS lists it: newest first, with its running balance.</summary>
public sealed record EmployeeAccountRow(string Concept, string Detail, string Amount, string Balance)
{
    public static IReadOnlyList<EmployeeAccountRow> From(EmployeeStatementDto statement) =>
        statement.Movements
            .Reverse()
            .Select(line =>
            {
                var signed = EmployeeFormRules.SignedAmount(line);
                var detail = $"{EmployeeFormRules.DateText(line.OccurredOn)} · {EmployeeFormRules.KindLabel(line.Kind)}"
                    + (line.Reversed ? " · Anulado" : string.Empty);
                var amount = (signed >= 0m ? "+" : "−") + EmployeeList.MoneyText(Math.Abs(signed));
                return new EmployeeAccountRow(line.Concept, detail, amount, $"Saldo: {EmployeeList.BalanceText(line.RunningBalance)}");
            })
            .ToList();
}
