using Commerce.Application.Management;
using Commerce.Cloud.Api.Management;
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
/// NOTE (deviation, documented): this walking-skeleton host has no
/// persisted product/user-account repository yet, so the actor and current
/// product state are supplied by the caller in the request body rather than
/// loaded from a store. Wiring real product/identity persistence is
/// follow-up work outside Unit 2's 9 tasks (host + tenant filter + Postgres
/// inbox adapter); this endpoint proves the real HTTP -> shared-service call
/// path that Unit 3's SPA will call.
/// </summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/catalog")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapPost("/products/{productId:guid}/rename", (
            Guid productId,
            RenameProductRequest request,
            HttpContext httpContext,
            CloudCatalogManagementAdapter adapter) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);

            var actor = new UserAccount(
                request.ActorId,
                scope.OrganizationId,
                request.ActorBranchScope,
                request.ActorRoles.Select(r => new Role(r.Name, r.Permissions)));

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
    Guid ActorId,
    IReadOnlyList<Guid> ActorBranchScope,
    IReadOnlyList<RoleDto> ActorRoles,
    Guid TargetBranchId,
    string CurrentName,
    Guid CategoryId,
    Guid DefaultUnitId,
    string NewName,
    bool IsOffline,
    Guid CorrelationId);

public sealed record RoleDto(string Name, Permission Permissions);
