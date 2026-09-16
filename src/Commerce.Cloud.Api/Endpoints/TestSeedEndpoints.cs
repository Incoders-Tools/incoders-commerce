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
/// This exists to close two gaps the real <c>/account/bootstrap</c> flow
/// deliberately leaves open for E2E purposes:
///   1. The real bootstrap token is delivered ONLY to server stdout
///      (<see cref="AccountEndpoints"/>'s <c>bootstrap/request-token</c>
///      remarks), so an out-of-process browser test harness cannot retrieve
///      it without scraping process logs.
///   2. The real bootstrap admin is ALWAYS created with an EMPTY branch
///      scope (no branch persistence exists anywhere in this system —
///      documented limitation in <see cref="AccountEndpoints"/>), so it can
///      never exercise the "allowed" path of
///      <c>TenantAuthorizationService.Authorize</c>'s branch-containment
///      check. This endpoint lets E2E setup seed an explicit branch scope so
///      that path is genuinely exercised too, in addition to the "denied"
///      path a bootstrap-only admin already covers.
///
/// Reuses <see cref="PostgresUserAccountStore.TryCreateAsync"/> exactly as
/// the real bootstrap endpoint does — no new persistence or business logic,
/// purely a test seam onto the existing store.
/// </summary>
public static class TestSeedEndpoints
{
    public static RouteGroupBuilder MapTestSeedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/test-seed");

        group.MapPost("/user", async (
            TestSeedUserRequest request,
            PostgresUserAccountStore store,
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            var scope = new CloudTenantScope(request.OrganizationId);
            var userId = Guid.NewGuid();
            var passwordHash = hasher.HashPassword(
                new UserAccount(userId, request.OrganizationId, [], []), request.Password);

            var created = await store.TryCreateAsync(
                scope,
                new NewUserAccount(
                    userId,
                    request.Email,
                    passwordHash,
                    request.BranchScope,
                    [new RoleDto(
                        "admin",
                        Permission.ViewSales | Permission.ManageCatalog | Permission.ManageUsers | Permission.ManageBranchSettings)]),
                ct);

            return created
                ? Results.Ok(new TestSeedUserResponse(userId, request.OrganizationId, request.Email))
                : Results.Conflict();
        }).AllowAnonymous();

        return group;
    }
}

public sealed record TestSeedUserRequest(Guid OrganizationId, string Email, string Password, Guid[] BranchScope);

public sealed record TestSeedUserResponse(Guid UserId, Guid OrganizationId, string Email);
