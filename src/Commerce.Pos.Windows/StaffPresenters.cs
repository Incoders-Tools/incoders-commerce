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

/// <summary>One staff member as the Personal list shows it.</summary>
public sealed record StaffRow(
    Guid UserId, string Email, string RolesText, string BranchText, string StatusText,
    bool CanChangeStatus, bool WillRevoke, string ActionLabel, bool IsConfirming, string ConfirmText)
{
    /// <summary>Convenience for the template: the confirm step and the action are mutually exclusive.</summary>
    public bool ShowAction => CanChangeStatus && !IsConfirming;

    /// <summary>Second line of a row: where the person works and whether the account is active.</summary>
    public string DetailText => string.Join(" · ", new[] { BranchText, StatusText }.Where(part => part.Length > 0));
}

/// <summary>
/// Turns the staff list of the API into rows: Spanish role labels, branch
/// membership relative to THIS terminal's branch, the status, and the
/// deactivate/reactivate action (hidden on the signed-in administrator's own
/// row; the server refuses it too) with its inline confirmation.
/// </summary>
public static class StaffRowPresenter
{
    public static IReadOnlyList<StaffRow> Build(
        IReadOnlyList<UserAdminRecordDto> users, Guid callerUserId, Guid terminalBranchId, Guid? pendingUserId) =>
        users
            .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
            .Select(user => ToRow(user, callerUserId, terminalBranchId, pendingUserId))
            .ToList();

    private static StaffRow ToRow(UserAdminRecordDto user, Guid callerUserId, Guid terminalBranchId, Guid? pendingUserId)
    {
        var willRevoke = !user.IsRevoked;
        var actionLabel = willRevoke ? "Dar de baja" : "Reactivar";
        return new StaffRow(
            user.UserId,
            user.Email,
            user.RoleNames.Count == 0 ? "Sin rol" : string.Join(", ", user.RoleNames.Select(StaffRoleOptions.LabelFor)),
            BranchText(user.BranchIds, terminalBranchId),
            user.IsRevoked ? "De baja" : "Activo",
            CanChangeStatus: user.UserId != callerUserId,
            willRevoke,
            actionLabel,
            IsConfirming: pendingUserId == user.UserId,
            ConfirmText: $"¿{actionLabel} a {user.Email}?");
    }

    private static string BranchText(IReadOnlyList<Guid>? branchIds, Guid terminalBranchId)
    {
        if (branchIds is null || branchIds.Count == 0)
        {
            return string.Empty;
        }

        if (!branchIds.Contains(terminalBranchId))
        {
            return "Otras sucursales";
        }

        return branchIds.Count == 1 ? "Esta sucursal" : "Esta sucursal y otras";
    }
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
