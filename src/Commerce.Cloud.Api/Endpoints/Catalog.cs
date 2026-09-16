using System.Security.Claims;
using Commerce.Application.Management;
using Commerce.Cloud.Api.Management;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Thin mapping onto <see cref="CloudCatalogManagementAdapter"/> (which
/// forwards to the shared <c>CatalogManagementService</c> — Component Reuse
/// Policy: no authorization/business logic is duplicated here). The
/// organization id is always <see cref="CloudTenantScope.OrganizationId"/>,
/// never a request field.
///
/// The actor's roles, branch scope, and revocation are loaded from
/// <see cref="PostgresUserAccountStore"/> using the authenticated cookie's
/// <see cref="ClaimTypes.NameIdentifier"/> claim (commerce-user-credentials
/// design.md "Authenticated rename") — NEVER trusted from the request body.
/// This closes the privilege-escalation hole the prior walking-skeleton
/// version had, where <c>RenameProductRequest</c> carried
/// <c>ActorId</c>/<c>ActorBranchScope</c>/<c>ActorRoles</c> directly from the
/// caller.
/// </summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/catalog")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapPost("/products/{productId:guid}/rename", async (
            Guid productId,
            RenameProductRequest request,
            HttpContext httpContext,
            CloudCatalogManagementAdapter adapter,
            PostgresUserAccountStore store,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);

            var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
            {
                return Results.Forbid();
            }

            var actor = await store.LoadActorAsync(scope, userId, ct);
            if (actor is null || actor.IsRevoked)
            {
                return Results.Forbid();
            }

            var product = new Product(
                productId,
                scope.OrganizationId,
                request.CurrentName,
                request.CategoryId,
                request.DefaultUnitId);

            var outcome = adapter.RenameProduct(
                scope,
                actor,
                product,
                request.TargetBranchId,
                request.NewName,
                request.IsOffline,
                request.CorrelationId);

            return outcome.Status == ManagementOutcomeStatus.Allowed
                ? Results.Ok(outcome)
                : Results.Json(outcome, statusCode: StatusCodes.Status403Forbidden);
        });

        return group;
    }
}

public sealed record RenameProductRequest(
    Guid TargetBranchId,
    string CurrentName,
    Guid CategoryId,
    Guid DefaultUnitId,
    string NewName,
    bool IsOffline,
    Guid CorrelationId);

public sealed record RoleDto(string Name, Permission Permissions);
