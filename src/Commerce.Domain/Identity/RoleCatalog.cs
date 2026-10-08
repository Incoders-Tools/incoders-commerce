using System.Collections.Frozen;

namespace Commerce.Domain.Identity;

/// <summary>
/// Canonical server-side name -> <see cref="Permission"/> map
/// (commerce-role-taxonomy proposal.md "Canonical server-side role catalog" /
/// design.md "RoleCatalog shape"). Permissions attached to any role are read
/// from THIS catalog only — callers never supply permissions directly, so
/// "permissions from the request body" is not validated-away, it is
/// unrepresentable.
///
/// Catalog keys are English kebab-case technical identifiers, never
/// translated or persisted in Spanish (proposal.md "Scope"). Lookup is by
/// string, case-insensitive ordinal, because both the wire format and the
/// persisted jsonb roles column are name-keyed.
/// </summary>
public static class RoleCatalog
{
    public const string BusinessAdmin = "business-admin";
    public const string Seller = "seller";
    public const string Cashier = "cashier";
    public const string Provider = "provider";
    public const string PlatformAdmin = "platform-admin";

    /// <summary>
    /// The permission set every `business-admin` row is created with. Endpoints
    /// that bootstrap an admin persist THIS constant so a new organization's
    /// admin can never drift from the catalog (e.g. miss `OperatePos`).
    /// </summary>
    public const Permission BusinessAdminPermissions =
        Identity.Permission.ViewSales
        | Identity.Permission.ManageCatalog
        | Identity.Permission.ManageUsers
        | Identity.Permission.ManageBranchSettings
        | Identity.Permission.OperatePos
        | Identity.Permission.TakeOrders;

    private static readonly FrozenDictionary<string, Permission> Permissions =
        new Dictionary<string, Permission>(StringComparer.OrdinalIgnoreCase)
        {
            [BusinessAdmin] = BusinessAdminPermissions,
            [Seller] = Identity.Permission.ViewSales | Identity.Permission.TakeOrders,
            [Cashier] = Identity.Permission.OperatePos,
            [Provider] = Identity.Permission.None,
            [PlatformAdmin] = Identity.Permission.None,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every catalog entry EXCEPT <see cref="PlatformAdmin"/> — an org-scoped
    /// caller can never be handed the platform-admin name, regardless of its
    /// own permission set (design.md "Grant-cap location").
    /// </summary>
    public static IReadOnlySet<string> OrgAssignable { get; } =
        Permissions.Keys.Where(name => !string.Equals(name, PlatformAdmin, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolve(string name, out Role? role)
    {
        if (name is not null && Permissions.TryGetValue(name, out var permissions))
        {
            // Preserve the canonical casing from the catalog key, not the
            // caller's casing, so persisted role names are always canonical.
            var canonicalName = Permissions.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            role = new Role(canonicalName, permissions);
            return true;
        }

        role = null;
        return false;
    }
}
