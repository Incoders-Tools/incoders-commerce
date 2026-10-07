using System.Security.Claims;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>A staff caller allowed for the request: the tenant scope and the caller's id (the actor of anything it does).</summary>
public sealed record StaffCaller(CloudTenantScope Scope, Guid Id);

/// <summary>
/// The one authorization check of staff endpoints: the caller is the signed-in user (NameIdentifier claim), loaded
/// through the store (never trusted from a claim alone), not revoked, and holding AT LEAST ONE of the
/// <paramref name="anyOf"/> permissions for this request (<see cref="ActingPermissions"/>: a system administrator acting
/// on the selected organization is covered). The actor of every write is this caller: a request never names it.
/// Denials are a literal 403 (never a redirect).
/// </summary>
public static class StaffAuthorization
{
    public static async Task<(IResult? Denied, StaffCaller? Caller)> AuthorizeAsync(
        HttpContext httpContext, PostgresUserAccountStore userStore, CancellationToken ct, params Permission[] anyOf)
    {
        var scope = TenantScopeEndpointFilter.GetScope(httpContext);
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (claim is null || !Guid.TryParse(claim, out var callerId))
        {
            return (Results.StatusCode(StatusCodes.Status403Forbidden), null);
        }

        var caller = await userStore.LoadActorAsync(scope.IdentityScope, callerId, ct);
        if (caller is null || caller.IsRevoked)
        {
            return (Results.StatusCode(StatusCodes.Status403Forbidden), null);
        }

        var granted = ActingPermissions.For(caller, scope);
        return anyOf.Any(permission => granted.HasFlag(permission))
            ? (null, new StaffCaller(scope, callerId))
            : (Results.StatusCode(StatusCodes.Status403Forbidden), null);
    }
}
