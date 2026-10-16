using Commerce.Domain.Identity;

namespace Commerce.Pos.Windows;

/// <summary>A role an administrator can give to a new staff member, with its Spanish label.</summary>
public sealed record StaffRoleOption(string Name, string Label);

/// <summary>
/// The roles the POS offers when creating staff: Cajero (the default, it is the
/// one that operates the register), Vendedor and Administrador. The server caps
/// what the caller may grant; this list only decides what is offered.
/// </summary>
public static class StaffRoleOptions
{
    public static IReadOnlyList<StaffRoleOption> All { get; } =
        new[] { RoleCatalog.Cashier, RoleCatalog.Seller, RoleCatalog.BusinessAdmin }
            .Where(RoleCatalog.OrgAssignable.Contains)
            .Select(name => new StaffRoleOption(name, LabelFor(name)))
            .ToList();

    public static StaffRoleOption Default => All[0];

    public static string LabelFor(string role) => role.ToLowerInvariant() switch
    {
        RoleCatalog.BusinessAdmin => "Administrador",
        RoleCatalog.Seller => "Vendedor",
        RoleCatalog.Cashier => "Cajero",
        RoleCatalog.Provider => "Proveedor",
        RoleCatalog.PlatformAdmin => "Administrador de plataforma",
        _ => role,
    };
}

/// <summary>One operator cached on this terminal, as "Operadores de esta terminal" lists it.</summary>
public sealed record TerminalOperatorRow(Guid UserId, string Email, string Detail, bool IsConfirming = false)
{
    public string ConfirmText =>
        $"¿Quitar a {Email} de esta terminal? Ya no podrá ingresar con su PIN; para volver tendrá que ingresar con su correo y contraseña.";

    public bool ShowAction => !IsConfirming;
}

public static class TerminalOperatorRowPresenter
{
    public static IReadOnlyList<TerminalOperatorRow> Build(IReadOnlyList<CachedOperator> operators, DateTimeOffset now, Guid? pendingRemovalUserId = null) =>
        operators
            .OrderBy(op => op.Email, StringComparer.OrdinalIgnoreCase)
            .Select(op => new TerminalOperatorRow(
                op.UserId,
                op.Email,
                op.IsStale(now)
                    ? $"{OperatorMenuPresenter.RoleSummary(op.Permissions)} · Vencido: debe ingresar de nuevo con su correo y contraseña"
                    : OperatorMenuPresenter.RoleSummary(op.Permissions),
                op.UserId == pendingRemovalUserId))
            .ToList();
}
