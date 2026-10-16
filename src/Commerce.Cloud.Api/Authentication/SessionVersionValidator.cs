using System.Security.Claims;
using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Commerce.Cloud.Api.Authentication;

/// <summary>
/// `CookieAuthenticationEvents.OnValidatePrincipal` implementation
/// (commerce-password-recovery design.md "Session invalidation"). A cookie
/// with no `session_ver` claim (issued before this change) is rejected
/// fail-closed; a cookie whose claimed version does not match
/// <see cref="SessionVersionCache"/>'s current value is rejected as stale.
/// </summary>
public sealed class SessionVersionValidator
{
    private readonly SessionVersionCache _cache;

    public SessionVersionValidator(SessionVersionCache cache) => _cache = cache;

    /// <summary>
    /// <paramref name="onReject"/> is a test-observability hook only — it is
    /// always invoked alongside <see cref="CookieValidatePrincipalContext.RejectPrincipal"/>
    /// on any rejection path. Production code (via <c>Program.cs</c>'s
    /// <c>OnValidatePrincipal</c> wiring) never needs to pass it; the cookie
    /// is proactively cleared via <see cref="IAuthenticationService.SignOutAsync"/>
    /// when the request's <see cref="HttpContext.RequestServices"/> can
    /// resolve one (tests use a minimal service provider without it).
    /// </summary>
    public async Task ValidateAsync(CookieValidatePrincipalContext context, Action? onReject = null)
    {
        var principal = context.Principal;

        var sessionVerClaim = principal?.FindFirst("session_ver")?.Value;
        var orgClaim = principal?.FindFirst(TenantScopeResolver.OrganizationClaimType)?.Value;
        var userIdClaim = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (sessionVerClaim is null || !int.TryParse(sessionVerClaim, out var claimedVersion)
            || orgClaim is null || !Guid.TryParse(orgClaim, out var organizationId)
            || userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            await RejectAsync(context, onReject);
            return;
        }

        var scope = new CloudTenantScope(organizationId);
        var currentVersion = await _cache.GetAsync(scope, userId, context.HttpContext.RequestAborted);

        if (currentVersion is null || currentVersion.Value != claimedVersion)
        {
            await RejectAsync(context, onReject);
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context, Action? onReject)
    {
        context.RejectPrincipal();
        onReject?.Invoke();

        var authenticationService = context.HttpContext.RequestServices.GetService(typeof(IAuthenticationService)) as IAuthenticationService;
        if (authenticationService is not null)
        {
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
