using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Thin mapping onto <see cref="CloudOrderSubmissionService"/> (bound-access
/// check + <see cref="CloudOrderStore"/> acceptance/delivery — no
/// authorization or business logic duplicated here, per Component Reuse
/// Policy). The organization id is always
/// <see cref="CloudTenantScope.OrganizationId"/>, never a request field.
///
/// NOTE (deviation, documented): no persisted destination-branch registry
/// exists in this host yet, so delivery always targets `destination: null`
/// (branch offline) and `hasAvailableStock: false`, which
/// <see cref="CloudOrderStore"/> already handles as an honest "pending"
/// outcome (ADR-003) rather than a false accept. Wiring a live branch
/// connection registry is follow-up work outside Unit 2's scope.
/// </summary>
public static class OrderingEndpoints
{
    public static RouteGroupBuilder MapOrderingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapPost("/", (
            SubmitOrderRequest request,
            HttpContext httpContext,
            CloudOrderSubmissionService service) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);

            var access = new CustomerOrderingAccess(
                scope.OrganizationId,
                request.CustomerId,
                request.AccessCredential,
                request.AccessEnabled);

            var outcome = service.Submit(
                scope,
                access,
                request.OrderId,
                request.DestinationBranchId,
                request.ActorId,
                request.Lines,
                request.CorrelationId,
                destination: null,
                hasAvailableStock: false);

            return outcome.Status == OrderSubmissionOutcomeStatus.Accepted
                ? Results.Ok(outcome)
                : Results.Json(outcome, statusCode: StatusCodes.Status403Forbidden);
        });

        group.MapGet("/{orderId:guid}", (Guid orderId, HttpContext httpContext, CloudOrderStore store) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            var order = store.Find(scope, orderId);
            return order is null ? Results.NotFound() : Results.Ok(order);
        });

        return group;
    }
}

public sealed record SubmitOrderRequest(
    Guid OrderId,
    Guid CustomerId,
    Guid AccessCredential,
    bool AccessEnabled,
    Guid DestinationBranchId,
    Guid ActorId,
    IReadOnlyList<OrderLineSnapshot> Lines,
    Guid CorrelationId);
