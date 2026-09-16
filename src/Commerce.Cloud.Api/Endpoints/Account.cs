using System.Security.Claims;
using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Minimal Identity-cookie sign-in surface for the browser SPA (design.md
/// "Browser auth"). Program.cs wires the Identity cookie scheme but Unit 2
/// never mapped a sign-in endpoint against it — Unit 2's scope was the API
/// surface for sync/catalog/ordering, not browser authentication, so the SPA
/// had nothing to authenticate against. This is a genuine gap filled here,
/// documented as a Unit 3 deviation.
///
/// NOTE (deviation, documented): this host has no persisted user-account
/// repository (see <c>CatalogEndpoints</c>'s own NOTE on the same gap), so
/// sign-in trusts the caller-submitted <c>organizationId</c>/<c>userId</c>
/// rather than verifying a password against a stored credential. It stamps
/// the <c>org_id</c> claim into the cookie exactly like a real sign-in would
/// (design.md "the <c>org_id</c> claim is stamped at sign-in") — everything
/// downstream (tenant scoping, endpoint authorization) is real; only the
/// credential-verification step is a walking-skeleton stand-in for a real
/// password/Identity-store check, matching Unit 2's precedent for the
/// missing persistence layer.
/// </summary>
public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account");

        group.MapPost("/sign-in", async (SignInRequest request, HttpContext httpContext) =>
        {
            if (request.OrganizationId == Guid.Empty || request.UserId == Guid.Empty
                || string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["organizationId, userId and displayName are required."],
                });
            }

            var claims = new[]
            {
                new Claim(TenantScopeResolver.OrganizationClaimType, request.OrganizationId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, request.UserId.ToString()),
                new Claim(ClaimTypes.Name, request.DisplayName),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            return Results.Ok(new SignedInResponse(request.OrganizationId, request.UserId, request.DisplayName));
        });

        group.MapPost("/sign-out", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        group.MapGet("/me", (HttpContext httpContext) =>
        {
            if (!TenantScopeResolver.TryResolve(httpContext.User, out var scope, out _))
            {
                return Results.Unauthorized();
            }

            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var displayName = httpContext.User.FindFirst(ClaimTypes.Name)?.Value;
            return Results.Ok(new SignedInResponse(scope!.OrganizationId, Guid.Parse(userId!), displayName ?? string.Empty));
        });

        return group;
    }
}

public sealed record SignInRequest(Guid OrganizationId, Guid UserId, string DisplayName);

public sealed record SignedInResponse(Guid OrganizationId, Guid UserId, string DisplayName);
