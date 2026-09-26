using System.Security.Claims;
using Commerce.Cloud.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Minimal API endpoint filter wrapper around <see cref="TenantScopeResolver"/>
/// (design.md "HTTP style" — endpoint filters express the tenant-scope
/// concern more directly than action filters). Applied to every tenant-scoped
/// endpoint group in `Program.cs`. Returns 401 when unauthenticated, 403 when
/// authenticated but the <c>org_id</c> claim is missing/invalid, and
/// otherwise stashes the resolved <see cref="CloudTenantScope"/> for the
/// downstream handler via <see cref="GetScope"/> — handlers never derive
/// scope themselves and never accept one from the request body/route.
///
/// Selected-organization override (platform-administration spec "Sysadmin
/// Acts On A Selected Organization"): a request carrying the
/// <see cref="OrganizationSelectorHeader"/> is honored ONLY when the
/// authenticated caller is a verified system administrator
/// (<c>UserAccount.IsSystemAdmin</c>, loaded fresh from the store, never a
/// claim) AND the named organization exists. For a caller who is not a
/// verified system administrator the header is silently IGNORED — the
/// request proceeds scoped to that caller's own organization exactly as if
/// no header had been sent, never to the organization the header names.
/// This keeps every existing org-scoped isolation guarantee unchanged for
/// every caller who is not a verified sysadmin, and never adds a new
/// rejection path to the overwhelming majority of requests, which never
/// carry the header at all.
/// </summary>
public sealed class TenantScopeEndpointFilter : IEndpointFilter
{
    internal const string ScopeItemKey = "Commerce.Cloud.Api.CloudTenantScope";

    /// <summary>
    /// Header a verified system administrator uses to select a target
    /// organization for the request. Never read for any other purpose, and
    /// never trusted before the caller's own sysadmin capability and the
    /// target organization's existence are both confirmed against the
    /// store.
    /// </summary>
    public const string OrganizationSelectorHeader = "X-Organization-Id";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!TenantScopeResolver.TryResolve(context.HttpContext.User, out var claimScope, out var failure))
        {
            return failure == TenantScopeFailureReason.NotAuthenticated
                ? Results.Unauthorized()
                : Results.Forbid();
        }

        var scope = claimScope!;

        if (context.HttpContext.Request.Headers.TryGetValue(OrganizationSelectorHeader, out var headerValues)
            && Guid.TryParse(headerValues.ToString(), out var selectedOrganizationId)
            && selectedOrganizationId != scope.OrganizationId)
        {
            var callerIdClaim = context.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (callerIdClaim is not null && Guid.TryParse(callerIdClaim, out var callerId))
            {
                var userStore = context.HttpContext.RequestServices.GetRequiredService<PostgresUserAccountStore>();
                var caller = await userStore.LoadActorAsync(scope, callerId, context.HttpContext.RequestAborted);

                if (caller is { IsSystemAdmin: true, IsRevoked: false })
                {
                    // A verified sysadmin's selector is honored only for an
                    // organization that actually exists — an unknown id is
                    // rejected outright rather than silently falling back to
                    // the sysadmin's own (tenant-data-empty) scope, so a typo
                    // never looks like "no data" for the target org.
                    var organizationStore = context.HttpContext.RequestServices.GetRequiredService<PostgresOrganizationStore>();
                    if (!await organizationStore.OrganizationExistsAsync(selectedOrganizationId, context.HttpContext.RequestAborted))
                    {
                        return Results.NotFound();
                    }

                    scope = new CloudTenantScope(selectedOrganizationId, scope.OrganizationId);
                }
                // Not a verified sysadmin (or revoked): selector ignored,
                // `scope` stays the caller's own claim-derived scope.
            }
        }

        context.HttpContext.Items[ScopeItemKey] = scope;
        return await next(context);
    }

    /// <summary>
    /// Retrieves the scope stashed by this filter. Throws if the filter was
    /// not applied to the endpoint — a handler must never fall back to
    /// deriving scope another way.
    /// </summary>
    public static CloudTenantScope GetScope(HttpContext httpContext) =>
        httpContext.Items[ScopeItemKey] as CloudTenantScope
            ?? throw new InvalidOperationException(
                $"No {nameof(CloudTenantScope)} resolved on this request. " +
                $"Ensure {nameof(TenantScopeEndpointFilter)} is applied to the endpoint.");
}
