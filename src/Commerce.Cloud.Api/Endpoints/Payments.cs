using System.Text.Json.Serialization;
using Commerce.Application.Payments;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Payments;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Payment recording + settlement read surface (commerce-payments design.md
/// "Endpoint shape and authorization"): <c>MapGroup("/payments")</c> +
/// <c>.RequireAuthorization()</c> + <see cref="TenantScopeEndpointFilter"/>,
/// exactly <c>OrderingEndpoints</c>'s shape — thin mapping onto
/// <see cref="PaymentRecordingService"/>. Organization id is ALWAYS
/// <see cref="CloudTenantScope.OrganizationId"/>, never a request field. The
/// SAME permission that manages the order records or reverses a payment — no
/// second-approver requirement (answered product question (c)); settlement
/// reads are reporting-only and gate nothing.
/// </summary>
public static class PaymentsEndpoints
{
    public static RouteGroupBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/payments")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapPost("/", async (
            RecordPaymentRequest request,
            HttpContext httpContext,
            PaymentRecordingService service,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            var subject = new PaymentSubject(request.SubjectKind, request.SubjectId);

            var entry = await service.RecordAsync(
                subject, scope.OrganizationId, request.Method, request.Amount,
                request.ActorId, request.EntryId, ct);

            if (entry is null)
            {
                // Fail-closed (Decision 2): the gateway is Unavailable. Never
                // 200, never a zero-amount entry.
                return Results.Json(
                    new { outcome = "Unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(entry);
        });

        group.MapPost("/{entryId:guid}/reversal", async (
            Guid entryId,
            ReversePaymentRequest request,
            HttpContext httpContext,
            PaymentRecordingService service,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            var subject = new PaymentSubject(request.SubjectKind, request.SubjectId);

            var reversal = await service.ReverseAsync(
                entryId, subject, scope.OrganizationId, request.ActorId, request.ReversalEntryId, ct);

            return Results.Ok(reversal);
        });

        group.MapGet("/orders/{orderId:guid}/settlement", async (
            Guid orderId,
            decimal target,
            HttpContext httpContext,
            PaymentRecordingService service,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            var subject = new PaymentSubject(PaymentSubjectKind.Order, orderId);
            var settlement = await service.GetSettlementAsync(subject, scope.OrganizationId, target, ct);
            return Results.Ok(settlement);
        });

        group.MapGet("/customers/{customerId:guid}/settlement", async (
            Guid customerId,
            decimal target,
            HttpContext httpContext,
            PaymentRecordingService service,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            var subject = new PaymentSubject(PaymentSubjectKind.Sale, customerId);
            var settlement = await service.GetSettlementAsync(subject, scope.OrganizationId, target, ct);
            return Results.Ok(settlement);
        });

        return group;
    }
}

// commerce-payments design.md "Endpoint shape and authorization": no
// organization-naming member exists here — the caller has nowhere to put
// one. The organization is always resolved via TenantScopeEndpointFilter.
public sealed record RecordPaymentRequest(
    Guid EntryId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] PaymentSubjectKind SubjectKind,
    Guid SubjectId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] PaymentMethod Method,
    decimal Amount,
    Guid ActorId);

public sealed record ReversePaymentRequest(
    Guid ReversalEntryId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] PaymentSubjectKind SubjectKind,
    Guid SubjectId,
    Guid ActorId);
