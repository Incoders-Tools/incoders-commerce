using System.Security.Claims;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Persistence;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Desktop management without a password (admin-console-field-fixes T5): a paired terminal calls the customer and
/// staff admin endpoints with its device credential plus the signed-in operator in <see cref="OperatorHeader"/>.
/// Only endpoints that opt in through <see cref="DeviceOperatorAccess.AllowDeviceOperator"/> carry this filter (and
/// the policy that lets a device credential authenticate at all); every other staff endpoint keeps the cookie-only
/// default and refuses a device exactly as before.
///
/// Runs after <see cref="TenantScopeEndpointFilter"/>, so the organization and branch are the device credential's.
/// A device request is let through only when the operator, loaded from the store under the device's organization
/// (RLS hides any other organization's user), is not revoked, holds <see cref="Permission.ManageUsers"/> in its own
/// <see cref="UserAccount.EffectivePermissions"/> (never elevated, never from the request) and has the device's
/// branch in its <see cref="UserAccount.BranchScope"/>. A missing or malformed header and every failed check answer
/// the same 403 <c>{"error":"operator-not-authorized"}</c> (one body for every reason, so it reveals nothing; the
/// terminal uses it to leave the section). Once verified, the operator becomes the request's caller (its id as the
/// <see cref="ClaimTypes.NameIdentifier"/>), so the handlers run their usual store-loaded authorization, grant caps
/// and audit with the operator as actor.
///
/// A cookie (browser) request is untouched: the header is ignored and the handler sees the signed-in user.
/// </summary>
public sealed class DeviceOperatorEndpointFilter : IEndpointFilter
{
    /// <summary>The signed-in operator's user id on a device-authenticated management request.</summary>
    public const string OperatorHeader = "X-Operator-Id";

    /// <summary>The installation the verified operator acted from (the device origin of the request).</summary>
    public const string OriginInstallationClaimType = "origin_installation_id";

    internal const string AuthenticationType = "DeviceOperator";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        if (!DeviceIdentity.TryResolve(httpContext.User, out var device))
        {
            return await next(context);
        }

        var operatorId = ReadOperatorId(httpContext.Request);
        if (operatorId is null)
        {
            return OperatorRefused();
        }

        var scope = TenantScopeEndpointFilter.GetScope(httpContext);
        var userStore = httpContext.RequestServices.GetRequiredService<PostgresUserAccountStore>();
        var operatorAccount = await userStore.LoadActorAsync(scope, operatorId.Value, httpContext.RequestAborted);
        if (operatorAccount is null
            || operatorAccount.IsRevoked
            || operatorAccount.OrganizationId != scope.OrganizationId
            || !operatorAccount.EffectivePermissions.HasFlag(Permission.ManageUsers)
            || !operatorAccount.BranchScope.Contains(device!.BranchId))
        {
            return OperatorRefused();
        }

        httpContext.User.AddIdentity(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, operatorAccount.Id.ToString()),
                new Claim(OriginInstallationClaimType, device.InstallationId.ToString()),
            ],
            AuthenticationType));

        var result = await next(context);

        // `Results.Forbid()` challenges the default (cookie) scheme, which would answer a terminal with a redirect
        // to a sign-in page; a device caller gets the plain 403 the handler meant.
        return result is ForbidHttpResult ? Refused() : result;
    }

    private static Guid? ReadOperatorId(HttpRequest request) =>
        request.Headers.TryGetValue(OperatorHeader, out var values)
        && Guid.TryParse(values.ToString(), out var operatorId) && operatorId != Guid.Empty
            ? operatorId
            : null;

    /// <summary>The body of every operator refusal (<c>error</c>).</summary>
    public const string OperatorNotAuthorizedError = "operator-not-authorized";

    private static IResult OperatorRefused() =>
        Results.Json(new { error = OperatorNotAuthorizedError }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult Refused() => Results.StatusCode(StatusCodes.Status403Forbidden);
}

/// <summary>
/// The opt-in for <see cref="DeviceOperatorEndpointFilter"/>: the authorization policy whose scheme authenticates a
/// device bearer (and the browser cookie otherwise), plus the filter that admits the device only with a verified
/// operator. Applied endpoint by endpoint, never to a group, so nothing else gains device access.
/// </summary>
public static class DeviceOperatorAccess
{
    /// <summary>Accepts the browser cookie or, when the request carries a bearer, the device credential.</summary>
    public const string PolicyName = "StaffOrDeviceOperator";

    public static RouteHandlerBuilder AllowDeviceOperator(this RouteHandlerBuilder builder) =>
        builder
            .RequireAuthorization(PolicyName)
            .AddEndpointFilter<DeviceOperatorEndpointFilter>();

    /// <summary>The scheme behind <see cref="PolicyName"/>: a bearer request is a device, anything else the cookie.</summary>
    public static string? SelectScheme(HttpContext httpContext) =>
        httpContext.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? CloudAuthenticationSchemes.DeviceBearer
            : Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
}
