using System.Security.Claims;
using Commerce.Cloud.Api.Authentication;

namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Pure claim reader for the branch/installation identity minted by
/// <see cref="DeviceBearerAuthenticationHandler"/> (design.md
/// "`CloudTenantScope` stays `(Guid OrganizationId)`"). Branch identity
/// travels as a claim, read via this helper, rather than widening
/// <see cref="CloudTenantScope"/> — that record is also constructed on the
/// cookie/SPA path (`/account/sign-in`, catalog, ordering) where no single
/// branch exists, so widening it would force a meaningless `Guid?` through
/// every one of those call sites. Free of ASP.NET Core hosting types, like
/// <see cref="TenantScopeResolver"/>.
/// </summary>
public sealed record DeviceIdentity(Guid BranchId, Guid InstallationId)
{
    public static bool TryResolve(ClaimsPrincipal? user, out DeviceIdentity? identity)
    {
        if (user?.Identity is not { IsAuthenticated: true })
        {
            identity = null;
            return false;
        }

        var branchClaim = user.FindFirst(DeviceBearerAuthenticationHandler.BranchClaimType);
        var installationClaim = user.FindFirst(DeviceBearerAuthenticationHandler.InstallationClaimType);

        if (branchClaim is null || installationClaim is null
            || !Guid.TryParse(branchClaim.Value, out var branchId) || branchId == Guid.Empty
            || !Guid.TryParse(installationClaim.Value, out var installationId) || installationId == Guid.Empty)
        {
            identity = null;
            return false;
        }

        identity = new DeviceIdentity(branchId, installationId);
        return true;
    }
}
