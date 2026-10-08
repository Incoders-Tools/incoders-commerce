using System.Globalization;

namespace Commerce.Pos.Windows;

/// <summary>Which form the Empleados editor shows: none (closed), the staff file (new or edit), an advance, or the account.</summary>
public enum EmployeeEditorPurpose
{
    None,
    Create,
    Edit,
    Advance,
    Account,
}

/// <summary>
/// The Personal → Empleados list on the reusable entity list, aligned with the web's <c>EmployeesScreen</c>
/// (docs/architecture/cross-layer-parity.md): the Legajo / Apellido y nombre / Puesto / Sueldo pactado / Cuenta /
/// Mercadería a descontar / Estado columns, the search over name, file number, DNI and CUIL, the Estado filter,
/// and the Editar / Adelanto / Cuenta / Dar de baja-Reincorporar row actions. UI-free; the screen supplies the
/// status change (it needs the connection and its busy controller) and fills its forms on <see cref="EditorChanged"/>.
/// The terminal shows the staff of ITS branch only (the web picks the branch).
/// </summary>
public sealed class EmployeeList
{
    public const string AdvanceAction = "advance";
    public const string AccountAction = "account";
    public const string ToggleActiveAction = "toggle-active";

    // The form the next opening of the editor on a row shows; a plain row selection (or Editar) opens the staff file.
    private EmployeeEditorPurpose? _requested;

    /// <param name="toggleActive">Dar de baja / Reincorporar, after the inline confirmation.</param>
    public EmployeeList(Func<EmployeeRecordDto, Task> toggleActive)
    {
        Model = new EntityListModel<EmployeeRecordDto>(Definition(toggleActive));
        Model.EditorChanged += OnModelEditorChanged;
    }

    public EntityListModel<EmployeeRecordDto> Model { get; }

    public EmployeeEditorPurpose Purpose { get; private set; }

    /// <summary>The editor opened on a form (with the employee it acts on, null while creating) or closed (None).</summary>
    public event Action<EmployeeEditorPurpose, EmployeeRecordDto?>? EditorChanged;

    public void BeginAdvance(EmployeeRecordDto employee) => Open(employee, EmployeeEditorPurpose.Advance);

    public void BeginAccount(EmployeeRecordDto employee) => Open(employee, EmployeeEditorPurpose.Account);

    public static bool CanGiveAdvance(EmployeeRecordDto employee) => employee.IsActive;

    // ---- the cells, in the web's words ------------------------------------------------------

    private static readonly CultureInfo Money = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>"$ 650.000,00", as the web's formatMoney (es-AR, ARS, two decimals).</summary>
    public static string MoneyText(decimal amount) => amount.ToString("C2", Money);

    public static string FrequencyLabel(string frequency) => frequency switch
    {
        "Biweekly" => "Quincenal",
        "Weekly" => "Semanal",
        _ => "Mensual",
    };

    private static string FrequencyShort(string frequency) => frequency switch
    {
        "Biweekly" => "por quincena",
        "Weekly" => "por semana",
        _ => "por mes",
    };

    public static string PayText(EmployeeRecordDto employee) => $"{MoneyText(employee.BaseSalary)} {FrequencyShort(employee.PayFrequency)}";

    /// <summary>What the business owes the employee ("Le debemos") or the employee owes ("Nos debe"); "—" when settled.</summary>
    public static string BalanceText(decimal balance) => balance switch
    {
        > 0m => $"Le debemos {MoneyText(balance)}",
        < 0m => $"Nos debe {MoneyText(-balance)}",
        _ => "—",
    };

    public static string GoodsText(EmployeeRecordDto employee) =>
        employee.CustomerId is null ? "No lleva" : employee.PurchasesOwed > 0m ? MoneyText(employee.PurchasesOwed) : "—";

    public static string StatusText(EmployeeRecordDto employee) => employee.IsActive ? "Activo" : "Baja";

    private static string ToggleLabel(EmployeeRecordDto employee) => employee.IsActive ? "Dar de baja" : "Reincorporar";

    private EntityListDefinition<EmployeeRecordDto> Definition(Func<EmployeeRecordDto, Task> toggleActive) => new(
        employee => employee.Id,
        [
            new("file", "Legajo", employee => employee.FileNumber.ToString(CultureInfo.InvariantCulture))
            {
                Width = 80, SortKey = employee => employee.FileNumber,
            },
            new("name", "Apellido y nombre", employee => employee.FullName),
            new("role", "Puesto", employee => employee.RoleName) { Width = 130 },
            new("pay", "Sueldo pactado", PayText) { Width = 190, SortKey = employee => employee.BaseSalary },
            new("balance", "Cuenta", employee => BalanceText(employee.Balance)) { Width = 190, SortKey = employee => employee.Balance },
            new("goods", "Mercadería a descontar", GoodsText) { Width = 170, SortKey = employee => employee.PurchasesOwed },
            new("state", "Estado", StatusText) { Width = 90 },
        ],
        employee => [employee.FullName, employee.FileNumber.ToString(CultureInfo.InvariantCulture), employee.DocumentNumber, employee.Cuil])
    {
        Filters =
        [
            new("state", "Estado",
            [
                new("Activos", employee => employee.IsActive),
                new("Dados de baja", employee => !employee.IsActive),
                new("Todos"),
            ]),
        ],
        RowActions =
        [
            EntityRowAction<EmployeeRecordDto>.Edit(),
            new(AdvanceAction, _ => "Adelanto", _ => EmployeeIcons.Advance)
            {
                IsEnabled = CanGiveAdvance,
                Run = employee => { BeginAdvance(employee); return Task.CompletedTask; },
            },
            new(AccountAction, _ => "Cuenta", _ => EmployeeIcons.Account)
            {
                Run = employee => { BeginAccount(employee); return Task.CompletedTask; },
            },
            new(ToggleActiveAction, ToggleLabel, employee => employee.IsActive ? EntityIcons.Disable : EntityIcons.Enable)
            {
                Run = toggleActive,
                Confirmation = employee => $"¿{ToggleLabel(employee)} a {employee.FullName}?",
            },
        ],
        InitialSortKey = "name",
        SearchPlaceholder = "Buscar por nombre, legajo, DNI o CUIL…",
        NewLabel = "Nuevo empleado",
        NewTitle = "Nuevo empleado",
        EditTitle = employee => Purpose switch
        {
            EmployeeEditorPurpose.Advance => $"Adelanto a {employee.FullName}",
            EmployeeEditorPurpose.Account => $"Cuenta corriente: {employee.FullName}",
            _ => $"Editar a {employee.FullName}",
        },
        EmptyEditorHint = "Elegí un empleado de la lista o tocá «Nuevo empleado».",
    };

    /// <summary>
    /// Opens <paramref name="purpose"/> on <paramref name="employee"/>. The same form already open on that employee stays
    /// as it is; another form on it closes first, so the editor opens again on the asked one.
    /// </summary>
    private void Open(EmployeeRecordDto employee, EmployeeEditorPurpose purpose)
    {
        if (Model.EditorMode == EntityEditorMode.Edit && Model.Editing?.Id == employee.Id)
        {
            if (Purpose == purpose)
            {
                return;
            }

            Model.CloseEditor();
        }

        _requested = purpose;
        Model.BeginEdit(employee);
    }

    private void OnModelEditorChanged(EntityEditorMode mode, EmployeeRecordDto? employee)
    {
        Purpose = mode switch
        {
            EntityEditorMode.New => EmployeeEditorPurpose.Create,
            EntityEditorMode.Edit => _requested ?? EmployeeEditorPurpose.Edit,
            _ => EmployeeEditorPurpose.None,
        };
        _requested = null;
        EditorChanged?.Invoke(Purpose, employee);
    }
}

/// <summary>Glyphs of the theme's icon font for the Empleados row actions.</summary>
public static class EmployeeIcons
{
    public const string Advance = "\uE8C7";
    public const string Account = "\uE8A5";
}

/// <summary>
/// The staff form's input rules, the web's (<c>EmployeeForm</c>, <c>AdvanceForm</c>, <c>parseAmount</c>) so both layers
/// accept and refuse the same values before the server does.
/// </summary>
public static class EmployeeFormRules
{
    /// <summary>The choices of "Se le paga", in the web's order.</summary>
    public static readonly IReadOnlyList<FormChoice> Frequencies =
    [
        new FormChoice("Monthly", "Mensual"),
        new FormChoice("Biweekly", "Quincenal"),
        new FormChoice("Weekly", "Semanal"),
    ];

    /// <summary>
    /// An amount as the web's <c>parseAmount</c>: digits with an optional "$" and up to two decimals after "," or ".",
    /// no thousands separators, greater than zero. Null when it is not one.
    /// </summary>
    public static decimal? ParseAmount(string text)
    {
        var normalized = text.Trim();
        if (normalized.StartsWith('$'))
        {
            normalized = normalized[1..].Trim();
        }

        normalized = normalized.Replace(',', '.');
        var parts = normalized.Split('.');
        if (parts.Length > 2 || parts[0].Length == 0 || !parts[0].All(char.IsAsciiDigit)
            || parts.Length == 2 && (parts[1].Length is 0 or > 2 || !parts[1].All(char.IsAsciiDigit)))
        {
            return null;
        }

        var value = decimal.Parse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return value > 0m ? value : null;
    }

    /// <summary>The salary field: empty is zero, otherwise an amount (<see cref="ParseAmount"/>); null when invalid.</summary>
    public static decimal? ParseSalary(string text) => string.IsNullOrWhiteSpace(text) ? 0m : ParseAmount(text);

    /// <summary>The salary as the form shows it: "650000,5" (no thousands separators, a decimal comma), as the web.</summary>
    public static string SalaryText(decimal salary) => salary.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>The file number: empty is "the next one" (null, valid); otherwise a whole number above zero.</summary>
    public static bool TryParseFileNumber(string text, out int? fileNumber)
    {
        fileNumber = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            fileNumber = parsed;
            return true;
        }

        return false;
    }

    /// <summary>A date typed as dd/mm/aaaa: empty is none (valid); otherwise a real date.</summary>
    public static bool TryParseDate(string text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateOnly.TryParseExact(text.Trim(), ["d/M/yyyy", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }

    public static string DateText(DateOnly? date) => date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// The accounts an advance may come from, at this terminal: the active ones of its branch or company-wide, the
    /// branch's drawer ("Cash") first, as the web picks it by default.
    /// </summary>
    public static IReadOnlyList<TreasuryAccountDto> AdvanceAccounts(IEnumerable<TreasuryAccountDto> accounts, Guid branchId) =>
        accounts
            .Where(account => account.IsActive && (account.BranchId is null || account.BranchId == branchId))
            .OrderBy(account => account.BranchId == branchId && account.Kind == "Cash" ? 0 : 1)
            .ThenBy(account => account.BranchId is null ? 1 : 0)
            .ThenBy(account => account.Name, StringComparer.Create(CultureInfo.GetCultureInfo("es-AR"), CompareOptions.IgnoreCase))
            .ToList();

    /// <summary>"Banco Nación (Toda la empresa)" for a company-wide account, as the web's accountLabel.</summary>
    public static string AccountLabel(TreasuryAccountDto account) =>
        account.BranchId is null ? $"{account.Name} (Toda la empresa)" : account.Name;

    /// <summary>The positions the form offers: the active ones, plus the employee's current one even if inactive.</summary>
    public static IReadOnlyList<EmployeeRoleDto> RoleOptions(IEnumerable<EmployeeRoleDto> roles, Guid? currentRoleId) =>
        [new EmployeeRoleDto(Guid.Empty, "Sin puesto"), .. roles.Where(role => role.IsActive || role.Id == currentRoleId)];

    /// <summary>The statement's kinds, in the web's employee-account words.</summary>
    public static string KindLabel(string kind) => kind switch
    {
        "OpeningBalance" => "Saldo inicial",
        "Invoice" => "Sueldo liquidado",
        "DebitNote" => "Nota de débito",
        "CreditNote" => "Nota de crédito",
        "Payment" => "Pago",
        "Adjustment" => "Ajuste",
        _ => kind,
    };

    /// <summary>The movement as it moves what the business owes the employee: a Credit raises it, a Debit lowers it.</summary>
    public static decimal SignedAmount(EmployeeStatementLineDto line) => line.Direction == "Credit" ? line.Amount : -line.Amount;
}
