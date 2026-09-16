using System.Security.Claims;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Real Postgres-backed credential verification (commerce-user-credentials
/// design.md "Org resolution at sign-in" / "Bootstrap"). Replaces the prior
/// walking-skeleton sign-in that trusted caller-submitted
/// organizationId/userId with zero password verification.
///
/// Sign-in never reveals WHICH check failed (unknown email vs. wrong
/// password vs. revoked user) — every failure path returns the same generic
/// 401, and the unknown-email path still runs a dummy hash verification for
/// timing parity (design.md risk #4).
/// </summary>
public static class AccountEndpoints
{
    /// <summary>
    /// A hash of a fixed, never-used dummy password, computed once. Verified
    /// against on the "unknown email" path so that path costs roughly the
    /// same wall-clock time as a real (wrong-password) verification — this is
    /// the ONLY use of this hash; it never gates real access.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<UserAccount>().HashPassword(
            new UserAccount(Guid.Empty, Guid.Empty, [], []),
            "dummy-password-for-timing-parity-only");

    public static RouteGroupBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account");

        group.MapPost("/sign-in", async (
            SignInRequest request,
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
                // Timing parity: run a real (failing) hash verification even
                // though there is no account to check against.
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

            // SuccessRehashNeeded is treated as success — rehash-on-login is
            // explicitly out of scope (design.md "Hashing").

            var claims = new[]
            {
                new Claim(TenantScopeResolver.OrganizationClaimType, credential.OrganizationId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, credential.Id.ToString()),
                new Claim(ClaimTypes.Name, credential.Email),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            return Results.Ok(new SignedInResponse(credential.OrganizationId, credential.Id, credential.Email));
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

        // --- Bootstrap: one-time first-admin creation gated by a log-only
        // token (design.md "Bootstrap token delivery") ------------------------

        group.MapPost("/bootstrap/request-token", async (
            BootstrapTokenRequest request,
            PostgresUserAccountStore store,
            BootstrapTokenRegistry registry,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var scope = new CloudTenantScope(request.OrganizationId);
            if (await store.HasAnyUserAsync(scope, ct))
            {
                return Results.Conflict();
            }

            var token = registry.Issue(request.OrganizationId);
            var logger = loggerFactory.CreateLogger("Commerce.Cloud.Api.Bootstrap");
            // Plaintext token reaches ONLY server stdout (`railway logs`),
            // never the HTTP response — the anonymous caller gets 202 + empty
            // body regardless of whether they are the legitimate operator.
            logger.LogInformation(
                "Bootstrap token for organization {OrganizationId}: {Token}", request.OrganizationId, token);

            return Results.StatusCode(StatusCodes.Status202Accepted);
        }).AllowAnonymous();

        group.MapPost("/bootstrap", async (
            BootstrapRequest request,
            PostgresOrganizationStore organizationStore,
            BootstrapTokenRegistry registry,
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            // Validate BEFORE token consumption (design.md "Interfaces /
            // Contracts"): a blank organizationName is a caller mistake, not
            // a spent bootstrap attempt, so it must not burn the token.
            if (string.IsNullOrWhiteSpace(request.OrganizationName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["organizationName"] = ["organizationName is required."],
                });
            }

            // Bootstrap token vs. transaction ordering (design.md): TryConsume
            // stays BEFORE the transaction. A failed transaction after this
            // point therefore burns the token by design — single-use is the
            // security property; re-usability after failure is not
            // (rejected alternative: peek-then-consume-after-commit, which
            // opens a replay window).
            if (!registry.TryConsume(request.OrganizationId, request.Token))
            {
                return Results.Unauthorized();
            }

            var scope = new CloudTenantScope(request.OrganizationId);
            var branchId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var branchName = string.IsNullOrWhiteSpace(request.BranchName) ? "Main" : request.BranchName.Trim();
            var passwordHash = hasher.HashPassword(
                new UserAccount(userId, request.OrganizationId, [], []), request.Password);

            var outcome = await organizationStore.TryCreateBootstrapAsync(
                scope,
                new NewOrganization(request.OrganizationId, request.OrganizationName.Trim()),
                new NewBranch(branchId, branchName),
                new NewUserAccount(
                    userId,
                    request.Email,
                    passwordHash,
                    [branchId],
                    [new RoleDto("admin", Permission.ViewSales | Permission.ManageCatalog | Permission.ManageUsers | Permission.ManageBranchSettings)]),
                ct);

            if (outcome != BootstrapOutcome.Created)
            {
                return Results.Conflict();
            }

            return Results.Ok(new BootstrapResponse(request.OrganizationId, branchId, userId));
        }).AllowAnonymous();

        return group;
    }
}

public sealed record SignInRequest(string Email, string Password);

public sealed record SignedInResponse(Guid OrganizationId, Guid UserId, string DisplayName);

public sealed record BootstrapTokenRequest(Guid OrganizationId);

public sealed record BootstrapRequest(
    Guid OrganizationId, string Token, string OrganizationName, string? BranchName, string Email, string Password);

public sealed record BootstrapResponse(Guid OrganizationId, Guid BranchId, Guid UserId);
