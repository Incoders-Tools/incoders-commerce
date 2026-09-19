using System.Security.Claims;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// The `/customer` group (commerce-guest-ordering design.md "Customer
/// session"): a FOURTH auth scheme, <see cref="CloudAuthenticationSchemes.CustomerCookie"/>,
/// applying the `PlatformAdminCookie` pattern a second time — its own
/// `Cookie.Name`/`Cookie.Path`, and a `"Customer"` authorization policy that
/// names ONLY this scheme, so the default staff cookie authenticates
/// nothing here and vice versa (`EffectivePermissions ⇒ Permission.None`
/// for a `CustomerId`-bearing account stays as defence in depth, not the
/// primary barrier).
///
/// `/customer/sign-in` rejects an account whose `CustomerId` is null with
/// the SAME generic 401 as a bad password — the exclusion between staff and
/// customer identity is symmetric: a staff account cannot sign into the
/// customer surface either.
/// </summary>
public static class CustomerSessionEndpoints
{
    /// <summary>Claim carrying the signed-in customer's id (never a staff role/permission claim).</summary>
    public const string CustomerIdClaimType = "customer_id";

    /// <summary>
    /// A hash of a fixed, never-used dummy password, computed once —
    /// AccountEndpoints'/PlatformAdminEndpoints' identical timing-parity
    /// pattern: every failure path costs roughly the same wall-clock time,
    /// including "unknown email" and "staff account, no CustomerId".
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<UserAccount>().HashPassword(
            new UserAccount(Guid.Empty, Guid.Empty, [], []),
            "dummy-password-for-timing-parity-only");

    /// <remarks>
    /// Phase 8 follow-up A (verify-report.md WARNING 1 — decouple
    /// `/customer/orders` from `GuestOrdering__*`): this route is now ALWAYS
    /// mapped, unconditionally, alongside `/customer/sign-in`, `/sign-out`,
    /// and `/me` — registered-customer self-service ordering is its own
    /// channel (ADR-009: order-origin channels are independent) and must not
    /// structurally disappear because the public guest surface is disabled.
    /// <see cref="GuestOrderTarget"/> is still the ONE existing org/branch
    /// resolution point in this host (design.md "Org/branch resolution
    /// point") and is reused here for the branch id only, independent of the
    /// customer's own organization (resolved from the session claim) — but
    /// it is resolved DEFENSIVELY via <c>httpContext.RequestServices</c>
    /// rather than as a DI parameter, because it is only registered as a
    /// singleton when `GuestOrdering__*` is configured. When it is absent,
    /// the route stays mapped and auth still runs (a missing/invalid session
    /// still gets 401) — only the branch-dependent step degrades to a
    /// explicit 503, never a route-level 404. This is a real, pre-existing
    /// infrastructure gap (no persisted branch registry exists yet, see
    /// design.md Open Questions), not silently hidden.
    /// </remarks>
    public static RouteGroupBuilder MapCustomerSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/customer");

        group.MapPost("/sign-in", async (
            CustomerSignInRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore store,
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["email and password are required."],
                });
            }

            var directoryEntry = await store.FindDirectoryEntryAsync(request.Email, ct);
            if (directoryEntry is null)
            {
                hasher.VerifyHashedPassword(
                    new UserAccount(Guid.Empty, Guid.Empty, [], []), DummyPasswordHash, request.Password);
                return Results.Unauthorized();
            }

            var scope = new CloudTenantScope(directoryEntry.OrganizationId);
            var credential = await store.FindByEmailAsync(scope, request.Email, ct);
            if (credential is null)
            {
                hasher.VerifyHashedPassword(
                    new UserAccount(Guid.Empty, Guid.Empty, [], []), DummyPasswordHash, request.Password);
                return Results.Unauthorized();
            }

            var verification = hasher.VerifyHashedPassword(
                new UserAccount(credential.Id, credential.OrganizationId, [], []), credential.PasswordHash, request.Password);
            if (verification == PasswordVerificationResult.Failed)
            {
                return Results.Unauthorized();
            }

            if (credential.IsRevoked)
            {
                return Results.Unauthorized();
            }

            // Symmetric exclusion (design.md "Customer session"): an account
            // with no CustomerId is a STAFF account — it gets the SAME
            // generic 401 a bad password gets, never a distinguishable
            // error, so this endpoint cannot be used to probe which emails
            // are staff-only.
            var actor = await store.LoadActorAsync(scope, credential.Id, ct);
            if (actor?.CustomerId is not { } customerId)
            {
                return Results.Unauthorized();
            }

            var claims = new[]
            {
                new Claim(TenantScopeResolver.OrganizationClaimType, credential.OrganizationId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, credential.Id.ToString()),
                new Claim(ClaimTypes.Name, credential.Email),
                new Claim(CustomerIdClaimType, customerId.ToString()),
            };
            var identity = new ClaimsIdentity(claims, CloudAuthenticationSchemes.CustomerCookie);
            await httpContext.SignInAsync(CloudAuthenticationSchemes.CustomerCookie, new ClaimsPrincipal(identity));

            return Results.Ok(new CustomerSignedInResponse(customerId, credential.Email));
        }).AllowAnonymous();

        group.MapPost("/sign-out", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CloudAuthenticationSchemes.CustomerCookie);
            return Results.Ok();
        }).RequireAuthorization("Customer");

        group.MapGet("/me", (HttpContext httpContext) =>
        {
            var customerIdClaim = httpContext.User.FindFirst(CustomerIdClaimType)?.Value;
            var emailClaim = httpContext.User.FindFirst(ClaimTypes.Name)?.Value;
            if (customerIdClaim is null || !Guid.TryParse(customerIdClaim, out var customerId))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new CustomerSignedInResponse(customerId, emailClaim ?? string.Empty));
        }).RequireAuthorization("Customer");

        // Always mapped (Phase 8 follow-up A) — never conditioned on whether
        // the public guest surface is configured.
        group.MapPost("/orders", async (
            SubmitCustomerOrderRequest request,
            HttpContext httpContext,
            CloudOrderSubmissionService service,
            CancellationToken ct) =>
        {
            // The session IS the authorization — customerId is read from
            // the CustomerCookie claim, NEVER from the request body (no
            // customerId field exists on SubmitCustomerOrderRequest at
            // all, so a caller has nowhere to assert someone else's id).
            var customerIdClaim = httpContext.User.FindFirst(CustomerIdClaimType)?.Value;
            if (customerIdClaim is null || !Guid.TryParse(customerIdClaim, out var customerId))
            {
                return Results.Unauthorized();
            }

            if (!TenantScopeResolver.TryResolve(httpContext.User, out var scope, out _) || scope is null)
            {
                return Results.Unauthorized();
            }

            // The signed-in UserAccount id (stamped at /customer/sign-in)
            // is the real, non-empty, non-staff identity that performed
            // this self-service action — analogous to a staff ActorId,
            // never Guid.Empty.
            var actorIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (actorIdClaim is null || !Guid.TryParse(actorIdClaim, out var actorId))
            {
                return Results.Unauthorized();
            }

            // Resolved DEFENSIVELY (not as a DI parameter): GuestOrderTarget
            // is only registered as a singleton when GuestOrdering__* is
            // configured (Program.cs). Auth already ran above — an
            // unconfigured branch target degrades this ONE step to an
            // explicit 503, never a route-level 404 (follow-up A: the
            // channel itself must not disappear).
            var target = httpContext.RequestServices.GetService<GuestOrderTarget>();
            if (target is null)
            {
                return Results.Json(
                    new { reason = "ordering-destination-not-configured" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var outcome = await service.SubmitForCustomerSessionAsync(
                scope,
                customerId,
                request.OrderId,
                target.DestinationBranchId,
                actorId,
                request.Lines,
                request.CorrelationId,
                destination: null,
                hasAvailableStock: false,
                ct);

            return outcome.Status == OrderSubmissionOutcomeStatus.Accepted
                ? Results.Ok(outcome)
                : Results.Json(outcome, statusCode: StatusCodes.Status403Forbidden);
        }).RequireAuthorization("Customer");

        return group;
    }
}

public sealed record CustomerSignInRequest(string Email, string Password);

public sealed record CustomerSignedInResponse(Guid CustomerId, string Email);

/// <summary>
/// Gap-closing follow-up unit DTO, mirrored EXACTLY from
/// `src/Commerce.Web/src/api/customerSession.ts`'s already-written
/// `SubmitCustomerOrderRequest` (Unit 6) so that client needs no changes:
/// no `customerId`/`accessCredential`/`actorId`/`destinationBranchId`
/// fields exist here — all four are resolved server-side from the session
/// claim and <see cref="GuestOrderTarget"/>, never from the request.
/// </summary>
public sealed record SubmitCustomerOrderRequest(Guid OrderId, IReadOnlyList<SubmitOrderLine> Lines, Guid CorrelationId);
