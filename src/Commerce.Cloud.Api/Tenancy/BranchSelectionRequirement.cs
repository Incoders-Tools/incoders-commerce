namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Opt-in guard for a branch-owned endpoint (tenant-access-foundation spec
/// "Selected Branch Scopes Every Branch-Owned Staff Request"). Not wired
/// into any endpoint yet (B7 U1) — each module's own U4+ work unit calls
/// this once its migration and RLS policy make a selected branch meaningful
/// for that module's tables. Organization-level endpoints (organization,
/// branch management, identity, session) MUST NOT call this.
/// </summary>
public static class BranchSelectionRequirement
{
    /// <summary>
    /// Returns a "branch selection required" 400 result when the request's
    /// already-resolved <see cref="CloudTenantScope"/> (see
    /// <see cref="TenantScopeEndpointFilter.GetScope"/>) carries no selected
    /// branch, otherwise <c>null</c>. The caller returns the result
    /// immediately rather than proceeding — never falls back to any branch.
    /// </summary>
    public static IResult? Enforce(HttpContext httpContext)
    {
        var scope = TenantScopeEndpointFilter.GetScope(httpContext);
        return scope.BranchId is null
            ? Results.BadRequest(new { error = "branch-selection-required" })
            : null;
    }
}
