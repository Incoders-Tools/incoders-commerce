using System.Security.Claims;

namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Reasons a caller's request could not be resolved to a
/// <see cref="CloudTenantScope"/>.
/// </summary>
public enum TenantScopeFailureReason
{
    /// <summary>No authenticated principal — caller must sign in / present a device credential.</summary>
    NotAuthenticated,

    /// <summary>Authenticated, but the <c>org_id</c> claim is missing, empty, or not a valid GUID.</summary>
    MissingOrInvalidClaim
}

/// <summary>
/// Pure claim -> <see cref="CloudTenantScope"/> derivation (ADR-002,
/// design.md "Browser auth" / "Device auth"). This is the ONLY place a
/// <see cref="CloudTenantScope"/> is minted from a request; it is
/// intentionally free of any ASP.NET Core hosting types so the
/// claim-derivation and rejection rules are unit-testable without spinning up
/// a host (see <see cref="TenantScopeEndpointFilter"/> for the endpoint-filter
/// wrapper actually wired into the pipeline).
///
/// The organization id is ALWAYS read from the authenticated principal's
/// <c>org_id</c> claim, stamped at sign-in — NEVER from a caller-submitted
/// route parameter or request body field. A request body/route that also
/// carries an organization id is simply ignored by every endpoint; nothing
/// here ever reads one.
/// </summary>
public static class TenantScopeResolver
{
    public const string OrganizationClaimType = "org_id";

    public static bool TryResolve(ClaimsPrincipal? user, out CloudTenantScope? scope, out TenantScopeFailureReason failure)
    {
        if (user?.Identity is not { IsAuthenticated: true })
        {
            scope = null;
            failure = TenantScopeFailureReason.NotAuthenticated;
            return false;
        }

        var claim = user.FindFirst(OrganizationClaimType);
        if (claim is null || string.IsNullOrWhiteSpace(claim.Value)
            || !Guid.TryParse(claim.Value, out var organizationId) || organizationId == Guid.Empty)
        {
            scope = null;
            failure = TenantScopeFailureReason.MissingOrInvalidClaim;
            return false;
        }

        scope = new CloudTenantScope(organizationId);
        failure = default;
        return true;
    }
}
