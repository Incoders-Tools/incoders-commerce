using System.Security.Claims;
using Commerce.Application.Time;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Tenancy;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace Commerce.Cloud.Api.Authentication;

/// <summary>
/// Blocks the web while an organization is <see cref="AccountStandingStatus.Suspended"/>
/// (odd/tasks/organization-account-standing.md T4): a staff request to an endpoint that requires authorization answers
/// 403 <c>{ "error": "organization-suspended" }</c> (the repository's typed-error shape), so the SPA can tell it apart
/// from a permission 403 and show the suspended screen.
/// <para>
/// Never applies to: anonymous endpoints (sign-in, password recovery), endpoints marked
/// <see cref="AllowWhileOrganizationSuspendedAttribute"/> (<c>/account/me</c>, sign-out), the POS (the device bearer
/// carries no staff cookie, and the DeviceBearer policy is skipped explicitly so the POS keeps selling and syncing,
/// ADR-002), the customer surfaces (Customer policy, out of scope), static files and the SPA fallback (no
/// authorization), and the system administrator. The administrator check loads the actor only when the organization
/// is suspended, so an active organization pays no extra query.
/// </para>
/// </summary>
public sealed class OrganizationSuspensionMiddleware
{
    public const string SuspendedCode = "organization-suspended";

    private static readonly HashSet<string> ExemptPolicies = ["DeviceBearer", "Customer"];

    private readonly RequestDelegate _next;

    public OrganizationSuspensionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context, OrganizationStandingCache standings, IBusinessClock clock, PostgresUserAccountStore users)
    {
        if (!AppliesTo(context) || !TryReadStaffIdentity(context.User, out var organizationId, out var userId))
        {
            await _next(context);
            return;
        }

        var ct = context.RequestAborted;
        var inputs = await standings.GetAsync(organizationId, ct);
        if (inputs is null
            || AccountStandingRules.Evaluate(inputs.DueOn, inputs.GraceDays, inputs.SuspendedAt is not null, clock.Today).Status
                != AccountStandingStatus.Suspended)
        {
            await _next(context);
            return;
        }

        var actor = await users.LoadActorAsync(new CloudTenantScope(organizationId), userId, ct);
        if (actor is { IsSystemAdmin: true })
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = SuspendedCode }, ct);
    }

    private static bool AppliesTo(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (metadata is null
            || metadata.GetMetadata<IAllowAnonymous>() is not null
            || metadata.GetMetadata<AllowWhileOrganizationSuspendedAttribute>() is not null)
        {
            return false;
        }

        var authorization = metadata.GetOrderedMetadata<IAuthorizeData>();
        return authorization.Count > 0 && !authorization.Any(data => data.Policy is { } policy && ExemptPolicies.Contains(policy));
    }

    /// <summary>Only the staff browser cookie; a device or customer principal never reaches the check.</summary>
    private static bool TryReadStaffIdentity(ClaimsPrincipal user, out Guid organizationId, out Guid userId)
    {
        organizationId = Guid.Empty;
        userId = Guid.Empty;
        return user.Identity is { IsAuthenticated: true, AuthenticationType: CookieAuthenticationDefaults.AuthenticationScheme }
            && Guid.TryParse(user.FindFirst(TenantScopeResolver.OrganizationClaimType)?.Value, out organizationId)
            && Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
    }
}

/// <summary>Marks an endpoint that stays reachable while the caller's organization is suspended.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhileOrganizationSuspendedAttribute : Attribute;

public static class OrganizationSuspensionEndpointExtensions
{
    /// <summary>See <see cref="AllowWhileOrganizationSuspendedAttribute"/>.</summary>
    public static TBuilder AllowWhileOrganizationSuspended<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowWhileOrganizationSuspendedAttribute());
}
