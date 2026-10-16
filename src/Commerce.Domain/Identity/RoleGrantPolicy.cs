namespace Commerce.Domain.Identity;

/// <summary>
/// Why a grant request was denied (design.md "Grant-cap location").
/// </summary>
public enum GrantDenial
{
    None,
    UnknownRole,
    ReservedRole,
    ExceedsCallerPermissions,
}

/// <summary>
/// Pure grant-cap + reserved-role + unknown-role decision
/// (commerce-role-taxonomy proposal.md "Grant cap" / design.md
/// "Grant-cap location"). No I/O — called by both
/// <c>POST /account/users</c> and <c>PUT /account/users/{userId}/roles</c>
/// BEFORE any I/O happens.
/// </summary>
public static class RoleGrantPolicy
{
    /// <summary>
    /// A caller can only assign permissions that are a subset of their own
    /// <see cref="UserAccount.EffectivePermissions"/> — a `ManageUsers`
    /// holder can never create or promote a user to a permission set greater
    /// than their own. `platform-admin` is checked FIRST, before the subset
    /// math, so it is refused even for a caller holding every flag.
    /// </summary>
    public static bool TryAuthorize(
        UserAccount caller,
        IReadOnlyList<string> requestedNames,
        out IReadOnlyList<Role>? roles,
        out GrantDenial denial)
    {
        var resolved = new List<Role>(requestedNames.Count);
        var union = Permission.None;

        foreach (var name in requestedNames)
        {
            if (!RoleCatalog.TryResolve(name, out var role))
            {
                roles = null;
                denial = GrantDenial.UnknownRole;
                return false;
            }

            if (string.Equals(role!.Name, RoleCatalog.PlatformAdmin, StringComparison.OrdinalIgnoreCase))
            {
                roles = null;
                denial = GrantDenial.ReservedRole;
                return false;
            }

            resolved.Add(role);
            union |= role.Permissions;
        }

        if ((union & ~caller.EffectivePermissions) != Permission.None)
        {
            roles = null;
            denial = GrantDenial.ExceedsCallerPermissions;
            return false;
        }

        roles = resolved;
        denial = GrantDenial.None;
        return true;
    }
}
