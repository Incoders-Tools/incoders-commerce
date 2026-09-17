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
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            var scope = new CloudTenantScope(request.OrganizationId);
            var branchId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var passwordHash = hasher.HashPassword(
                new UserAccount(userId, request.OrganizationId, [], []), request.Password);

            var outcome = await organizationStore.TryCreateBootstrapAsync(
                scope,
                new NewOrganization(request.OrganizationId, "E2E Test Organization"),
                new NewBranch(branchId, "Main"),
                new NewUserAccount(
                    userId,
                    request.Email,
                    passwordHash,
                    [branchId],
                    [new RoleDto(
                        RoleCatalog.BusinessAdmin,
                        Permission.ViewSales | Permission.ManageCatalog | Permission.ManageUsers | Permission.ManageBranchSettings)]),
                ct);

            return outcome == BootstrapOutcome.Created
                ? Results.Ok(new TestSeedUserResponse(userId, request.OrganizationId, branchId, request.Email))
                : Results.Conflict();
        }).AllowAnonymous();

        return group;
    }
}

public sealed record TestSeedUserRequest(Guid OrganizationId, string Email, string Password);

public sealed record TestSeedUserResponse(Guid UserId, Guid OrganizationId, Guid BranchId, string Email);
