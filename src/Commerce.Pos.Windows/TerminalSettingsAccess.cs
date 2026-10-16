using Commerce.Domain.Identity;

namespace Commerce.Pos.Windows;

/// <summary>
/// Who sees the technical part of "Configuración de terminal" (owner feedback 2026-10-15 round 2, S2): "Identidad
/// técnica / Reconfigurar terminal" and "Buscar actualizaciones" are for an administrator (the operator menu's
/// "Administrador": <see cref="Permission.ManageUsers"/>); a cashier keeps Apariencia, Sincronización and Sucursal y
/// estado. A UX gate only, like the admin sections of the nav bar.
/// </summary>
public static class TerminalSettingsAccess
{
    public static bool ShowsTechnicalSections(CachedOperator? signedIn) =>
        signedIn is not null && ((Permission)signedIn.Permissions).HasFlag(Permission.ManageUsers);
}
