using System.Security.Claims;
using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// The `/platform` group (commerce-role-taxonomy design.md "Data Flow" /
/// "Interfaces / Contracts"): genesis pair, sign-in, sign-out, list
/// organizations, create organization. Requires ONLY the `PlatformAdmin`
/// policy, which names `CloudAuthenticationSchemes.PlatformAdminCookie`
/// explicitly — the default org cookie authenticates nothing here, and this
/// group carries NO `TenantScopeEndpointFilter` (design.md "Scheme mutual
/// exclusivity").
/// </summary>
public static class PlatformAdminEndpoints
{
    /// <summary>
    /// A hash of a fixed, never-used dummy password, computed once. Verified
    /// against on the "unknown email" sign-in path for timing parity — the
    /// ONLY use of this hash; it never gates real access (mirrors
    /// AccountEndpoints' identical pattern).
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<PlatformAdmin>().HashPassword(
            new PlatformAdmin(Guid.Empty, "dummy@example.com"), "dummy-password-for-timing-parity-only");

    // No real organization owns the platform genesis token — reuses
    // BootstrapTokenRegistry's per-organization keying with a fixed pseudo-id
    // (design.md "Platform genesis endpoint gating").
    private static readonly Guid GenesisPseudoOrganizationId = Guid.Empty;

    public static RouteGroupBuilder MapPlatformAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/platform");

        // --- Genesis: one-time first-platform-admin creation, identical
        // shape to /account/bootstrap's log-only token flow (design.md
        // "Platform genesis endpoint gating") -----------------------------

        group.MapPost("/bootstrap/request-token", async (
            PostgresPlatformAdminStore store,
            BootstrapTokenRegistry registry,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (await store.HasAnyAdminAsync(ct))
            {
                return Results.Conflict();
            }

            var token = registry.Issue(GenesisPseudoOrganizationId);
            var logger = loggerFactory.CreateLogger("Commerce.Cloud.Api.PlatformBootstrap");
            // Plaintext token reaches ONLY server stdout — never the HTTP
            // response.
            logger.LogInformation("Platform genesis token: {Token}", token);

            return Results.StatusCode(StatusCodes.Status202Accepted);
        }).AllowAnonymous();

        group.MapPost("/bootstrap", async (
            PlatformGenesisRequest request,
            PostgresPlatformAdminStore store,
            BootstrapTokenRegistry registry,
            PasswordHasher<PlatformAdmin> hasher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["email and password are required."],
                });
            }

            // Token consumption BEFORE the write, same ordering rationale as
            // /account/bootstrap: single-use is the security property, not
            // re-usability after a later failure.
            if (!registry.TryConsume(GenesisPseudoOrganizationId, request.Token))
            {
                return Results.Unauthorized();
            }

            var id = Guid.NewGuid();
            var passwordHash = hasher.HashPassword(new PlatformAdmin(id, request.Email), request.Password);
            var created = await store.TryCreateGenesisAsync(id, request.Email, passwordHash, ct);

            // Empty-body 202 either way — identical shape to
            // /account/bootstrap/request-token; the database's NOT EXISTS
            // policy is the second, independent barrier against a second
            // admin (design.md "Platform genesis endpoint gating").
            return created ? Results.StatusCode(StatusCodes.Status202Accepted) : Results.Conflict();
        }).AllowAnonymous();

        // --- Sign-in / sign-out --------------------------------------------

        group.MapPost("/sign-in", async (
            PlatformSignInRequest request,
            HttpContext httpContext,
            PostgresPlatformAdminStore store,
            PasswordHasher<PlatformAdmin> hasher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["email and password are required."],
                });
            }

            var record = await store.FindByEmailAsync(request.Email, ct);
            if (record is null)
            {
                // Timing parity: run a real (failing) hash verification even
                // though there is no admin row to check against. This also
                // covers org-scoped `UserAccount` credentials submitted
                // here — they are never present in `platform_admins`, so
                // they fall into this exact same branch.
                hasher.VerifyHashedPassword(
                    new PlatformAdmin(Guid.Empty, "dummy@example.com"), DummyPasswordHash, request.Password);
                return Results.Unauthorized();
            }

            var verification = hasher.VerifyHashedPassword(
                new PlatformAdmin(record.Id, record.Email), record.PasswordHash, request.Password);
            if (verification == PasswordVerificationResult.Failed)
            {
                return Results.Unauthorized();
            }

            // NO org_id claim — TenantScopeResolver fails closed on this
            // identity by construction (design.md "Scheme mutual
            // exclusivity").
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, record.Id.ToString()),
                new Claim(ClaimTypes.Name, record.Email),
            };
            var identity = new ClaimsIdentity(claims, CloudAuthenticationSchemes.PlatformAdminCookie);
            await httpContext.SignInAsync(CloudAuthenticationSchemes.PlatformAdminCookie, new ClaimsPrincipal(identity));

            await store.TouchLastSignInAsync(record.Id, ct);

            return Results.Ok();
        }).AllowAnonymous();

        group.MapPost("/sign-out", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CloudAuthenticationSchemes.PlatformAdminCookie);
            return Results.Ok();
        }).RequireAuthorization("PlatformAdmin");

        // --- List organizations: the ONLY genuinely cross-org read ---------

        group.MapGet("/organizations", async (PostgresPlatformAdminStore store, CancellationToken ct) =>
        {
            if (!store.CanListOrganizations)
            {
                // Fails closed — NEVER falls back to app_runtime (design.md
                // "Platform-admin cross-org read").
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var organizations = await store.ListOrganizationsAsync(ct);
            return Results.Ok(organizations);
        }).RequireAuthorization("PlatformAdmin");

        // --- Bootstrap an organization: reuses TryCreateBootstrapAsync -----

        group.MapPost("/organizations", async (
            CreateOrganizationRequest request,
            HttpContext httpContext,
            PostgresOrganizationStore organizationStore,
            PasswordHasher<UserAccount> userHasher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.OrganizationName)
                || string.IsNullOrWhiteSpace(request.AdminEmail)
                || string.IsNullOrWhiteSpace(request.AdminPassword))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["organizationName, adminEmail and adminPassword are required."],
                });
            }

            var actorIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (actorIdClaim is null || !Guid.TryParse(actorIdClaim, out var actorId))
            {
                return Results.Forbid();
            }

            // Server-minted, NEVER caller-supplied (spec: "Platform-admin
            // action targets an explicit organization id").
            var organizationId = Guid.NewGuid();
            var scope = new CloudTenantScope(organizationId);
            var branchId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var branchName = string.IsNullOrWhiteSpace(request.BranchName) ? "Main" : request.BranchName.Trim();
            var passwordHash = userHasher.HashPassword(new UserAccount(userId, organizationId, [], []), request.AdminPassword);

            var auditNewValue = JsonSerializer.Serialize(new
            {
                organizationName = request.OrganizationName.Trim(),
                adminEmail = request.AdminEmail.Trim().ToLowerInvariant(),
            });

            var outcome = await organizationStore.TryCreateBootstrapAsync(
                scope,
                new NewOrganization(organizationId, request.OrganizationName.Trim()),
                new NewBranch(branchId, branchName),
                new NewUserAccount(
                    userId,
                    request.AdminEmail,
                    passwordHash,
                    [branchId],
                    [new RoleDto(
                        RoleCatalog.BusinessAdmin,
                        Permission.ViewSales | Permission.ManageCatalog | Permission.ManageUsers | Permission.ManageBranchSettings)]),
                new UserManagementAuditEntry(
                    "platform-admin", actorId, organizationId, "organization", organizationId,
                    "organization.bootstrapped", null, auditNewValue),
                ct);

            if (outcome != BootstrapOutcome.Created)
            {
                return Results.Conflict();
            }

            return Results.Created(
                $"/platform/organizations/{organizationId}",
                new CreateOrganizationResponse(organizationId, branchId, userId));
        }).RequireAuthorization("PlatformAdmin");

        return group;
    }
}

public sealed record PlatformSignInRequest(string Email, string Password);

public sealed record PlatformBootstrapTokenRequest();

public sealed record PlatformGenesisRequest(string Token, string Email, string Password);

public sealed record OrganizationSummary(Guid Id, string Name, DateTimeOffset CreatedAt);

public sealed record CreateOrganizationRequest(string OrganizationName, string? BranchName, string AdminEmail, string AdminPassword);

public sealed record CreateOrganizationResponse(Guid OrganizationId, Guid BranchId, Guid UserId);
