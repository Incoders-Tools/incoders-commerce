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

        group.MapPost("/", async (
            SubmitOrderRequest request,
            HttpContext httpContext,
            CloudOrderSubmissionService service,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);

            // commerce-customer-identity security fix: no CustomerOrderingAccess
            // is ever built from request data here. The request carries only
            // the customer id it claims and the credential it holds; the
            // service resolves enabled/binding state from the persisted
            // store. A caller has nowhere to assert "enabled" — the field is
            // gone from the DTO and there is no constructor path for it.
            var outcome = await service.SubmitAsync(
                scope,
                request.CustomerId,
                request.AccessCredential,
                request.OrderId,
                request.DestinationBranchId,
                request.ActorId,
                request.Lines,
                request.CorrelationId,
                destination: null,
                hasAvailableStock: false,
                ct);

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

        // Phase 8 follow-up C (commerce-guest-ordering verify-report.md
        // WARNING 3): the first read path that exercises
        // Order.DispatchRank as a sort key end to end (design.md File
        // Changes: "pending-list reads ordered by DispatchRank then
        // SubmittedAtUtc"). Registered-customer orders (rank 0) sort before
        // guest orders (rank 1) regardless of submission order; ties break
        // by submission time.
        group.MapGet("/pending", (HttpContext httpContext, CloudOrderStore store) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            return Results.Ok(store.ListPending(scope));
        });

        return group;
    }
}

// commerce-customer-identity security fix: NO caller-supplied enabled flag.
// This is deliberately unrepresentable, not merely unvalidated — there is no
// member here a careless future edit could wire back up.
//
// commerce-pricing-engine design.md "OrderLineSnapshot extension and where
// resolution runs": SubmitOrderLine is price-free — a client supplying a
// price is not rejected, it has nowhere to put one. The four resolved-price
// fields exist only on OrderLineSnapshot, populated server-side after
// PricingResolutionService.ResolveAsync.
public sealed record SubmitOrderLine(Guid ProductId, Guid PresentationId, decimal Quantity);

public sealed record SubmitOrderRequest(
    Guid OrderId,
    Guid CustomerId,
    Guid AccessCredential,
    Guid DestinationBranchId,
    Guid ActorId,
    IReadOnlyList<SubmitOrderLine> Lines,
    Guid CorrelationId);
