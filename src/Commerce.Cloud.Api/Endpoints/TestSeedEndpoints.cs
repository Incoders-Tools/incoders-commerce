using System.Text.RegularExpressions;
using Commerce.Cloud.Api.Email;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// TEST-ONLY user seeding for the Playwright E2E suite
/// (<c>src/Commerce.Web/e2e</c>). Mapped ONLY when
/// <c>app.Environment.IsDevelopment()</c> is true (see <c>Program.cs</c>) —
/// never reachable in a real deploy, since Railway/production never sets
/// <c>ASPNETCORE_ENVIRONMENT=Development</c>.
///
/// Narrowed by commerce-organization-persistence (design.md "Test seam:
/// recommendation"): this seam's ONLY remaining privilege is skipping the
/// stdout token hop the real <c>/account/bootstrap</c> flow requires — the
/// bootstrap token is delivered ONLY to server stdout
/// (<see cref="AccountEndpoints"/>'s <c>bootstrap/request-token</c>
/// remarks), which an out-of-process browser test harness cannot retrieve
/// without scraping process logs.
///
/// It no longer accepts an arbitrary caller-fabricated <c>branchScope</c> —
/// that capability existed only because branch persistence did not exist.
/// Now it routes through the exact SAME
/// <see cref="PostgresOrganizationStore.TryCreateBootstrapAsync"/> path the
/// real bootstrap endpoint uses, and returns the REAL generated
/// <c>branchId</c> so E2E callers exercise a genuinely real authorization
/// path.
/// </summary>
public static class TestSeedEndpoints
{
    public static RouteGroupBuilder MapTestSeedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/test-seed");

        group.MapPost("/user", async (
            TestSeedUserRequest request,
            PostgresOrganizationStore organizationStore,
            PostgresUserAccountStore userAccountStore,
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            var scope = new CloudTenantScope(request.OrganizationId);
            var branchId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var passwordHash = hasher.HashPassword(
                new UserAccount(userId, request.OrganizationId, [], []), request.Password);

            // B1 (odd/tasks/frontend-modernization.md, product review
            // backlog): the platform sysadmin must never hold an
            // organization role — that was this seam's own bug, always
            // granting `business-admin`. `systemAdmin: true` grants ZERO
            // roles and ZERO branch scope (openspec/specs/platform-
            // administration/spec.md "Sysadmin Identity Lives in the
            // Unified Model": cross-org capability is the `is_system_admin`
            // flag alone, never a role grant). The organization+branch row
            // still exists — `users.organization_id` is `NOT NULL`, schema
            // has no nullable escape hatch — but it is minimal and never
            // named after the caller's own organization, so it reads as
            // what it is: bootstrap plumbing, not a real tenant.
            var roles = request.SystemAdmin
                ? Array.Empty<RoleDto>()
                : new[]
                {
                    new RoleDto(
                        RoleCatalog.BusinessAdmin,
                        Permission.ViewSales | Permission.ManageCatalog | Permission.ManageUsers | Permission.ManageBranchSettings),
                };
            var branchScope = request.SystemAdmin ? Array.Empty<Guid>() : new[] { branchId };
            var organizationName = request.SystemAdmin ? "Platform System Administrator" : "E2E Test Organization";

            var outcome = await organizationStore.TryCreateBootstrapAsync(
                scope,
                new NewOrganization(request.OrganizationId, organizationName),
                new NewBranch(branchId, "Main"),
                new NewUserAccount(userId, request.Email, passwordHash, branchScope, roles),
                ct);

            if (outcome != BootstrapOutcome.Created)
            {
                return Results.Conflict();
            }

            if (request.SystemAdmin)
            {
                await userAccountStore.PromoteToSystemAdminAsync(scope, userId, ct);
            }

            return Results.Ok(new TestSeedUserResponse(userId, request.OrganizationId, branchId, request.Email));
        }).AllowAnonymous();

        // Phase 8 follow-up B (commerce-guest-ordering verify-report.md
        // WARNING 2): the guest verification E2E flow cannot read the
        // issued 6-digit code back out of LogOnlyEmailSender — the ONLY gap
        // task 6.7 documented as PARTIAL. Mirrors this file's own precedent
        // ("skip an out-of-band hop a browser test harness cannot retrieve
        // without scraping process logs"), scoped to the SAME
        // Development-only mapping guard as every other route in this file
        // (Program.cs: `if (app.Environment.IsDevelopment())`).
        //
        // Contract (for the web-branch Playwright wiring):
        //   GET /internal/test-seed/guest-verification-code?contactAddress={email}
        //   200 OK  { "code": "123456" }               — a message was sent to that address
        //   404     (no body)                            — no message was ever sent to that address in this process
        //   503     (no body)                            — the active IEmailSender is not LogOnlyEmailSender
        //                                                   (e.g. RESEND_API_KEY is configured) or no 6-digit
        //                                                   code could be found in the message body
        group.MapGet("/guest-verification-code", (
            string contactAddress,
            IEmailSender emailSender) =>
        {
            if (emailSender is not LogOnlyEmailSender logOnlySender)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var message = logOnlySender.TryGetLastMessage(contactAddress);
            if (message is null)
            {
                return Results.NotFound();
            }

            var match = Regex.Match(message.TextBody, @"\b\d{6}\b");
            if (!match.Success)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new TestSeedGuestVerificationCodeResponse(match.Value));
        }).AllowAnonymous();

        return group;
    }
}

/// <param name="SystemAdmin">
/// B1 (frontend-modernization): when true, seeds the platform system
/// administrator instead of an ordinary business-admin — zero organization
/// roles, zero branch scope, `is_system_admin = true`. Defaults to false so
/// every pre-existing caller (`src/Commerce.Web/e2e/helpers.ts`'s
/// `seedUser`) keeps its exact current behavior unchanged.
/// </param>
public sealed record TestSeedUserRequest(Guid OrganizationId, string Email, string Password, bool SystemAdmin = false);

public sealed record TestSeedUserResponse(Guid UserId, Guid OrganizationId, Guid BranchId, string Email);

/// <summary>Phase 8 follow-up B DTO — see <see cref="TestSeedEndpoints"/> remarks for the full route contract.</summary>
public sealed record TestSeedGuestVerificationCodeResponse(string Code);
