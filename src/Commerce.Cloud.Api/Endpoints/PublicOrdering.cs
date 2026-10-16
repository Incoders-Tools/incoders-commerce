using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// The platform's first anonymous HTTP boundary (commerce-guest-ordering
/// design.md "Public surface"; public-order-surface spec.md). Mapped by
/// `Program.cs` ONLY when <see cref="GuestOrderTarget.TryFromConfiguration"/>
/// succeeds — with `GuestOrdering__*` config absent, this method is never
/// called and the whole group 404s (the narrowest rollback: unset config,
/// not a code revert). Every route here is anonymous, derives its
/// organization/branch scope solely from the injected
/// <see cref="GuestOrderTarget"/> via <see cref="PublicScopeEndpointFilter"/>
/// (no org/branch field exists on any request DTO), and carries a named
/// rate-limiter policy — staff, customer, and device endpoint groups carry
/// none.
/// </summary>
public static class PublicOrderingEndpoints
{
    public static RouteGroupBuilder MapPublicOrderingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/public")
            .AllowAnonymous()
            .AddEndpointFilter<PublicScopeEndpointFilter>();

        group.MapGet("/catalog/presentations", async (
            HttpContext httpContext,
            PostgresCatalogStore catalogStore,
            CancellationToken ct) =>
        {
            var scope = PublicScopeEndpointFilter.GetScope(httpContext);
            var presentations = await catalogStore.ListPresentationsAsync(scope, ct);
            return Results.Ok(presentations);
        }).RequireRateLimiting(PublicRateLimitPolicies.PublicCatalogRead);

        group.MapPost("/guest-orders/verification", async (
            GuestVerificationRequest request,
            HttpContext httpContext,
            GuestVerificationService verificationService,
            GuestVerificationThrottle throttle,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.DocumentId) || string.IsNullOrWhiteSpace(request.Email))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["documentId and email are required."],
                });
            }

            var scope = PublicScopeEndpointFilter.GetScope(httpContext);
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            // GuestVerificationThrottle (design.md Data Flow): a SECOND,
            // identity-keyed layer on top of the IP-only rate limiter below —
            // a throttled request still returns the SAME 202 with no
            // verification id issued (the reset-password precedent: delivery
            // status is not an oracle).
            if (!throttle.TryAcquire(normalizedEmail, httpContext.Connection.RemoteIpAddress))
            {
                return Results.Accepted();
            }

            var verificationId = await verificationService.RequestAsync(
                scope, request.DocumentId.Trim(), GuestContactChannel.Email, normalizedEmail, ct);

            return Results.Accepted(value: new GuestVerificationRequestedResponse(verificationId));
        }).RequireRateLimiting(PublicRateLimitPolicies.GuestVerificationRequest);

        group.MapPost("/guest-orders/verification/confirm", async (
            GuestVerificationConfirmRequest request,
            GuestVerificationService verificationService,
            CancellationToken ct) =>
        {
            var result = await verificationService.ConfirmAsync(request.VerificationId, request.Code, ct);
            return result == GuestVerificationConfirmResult.Confirmed
                ? Results.NoContent()
                : Results.Unauthorized();
        }).RequireRateLimiting(PublicRateLimitPolicies.GuestVerificationConfirm);

        group.MapPost("/guest-orders", async (
            SubmitGuestOrderRequest request,
            HttpContext httpContext,
            GuestOrderTarget target,
            CloudOrderSubmissionService service,
            CancellationToken ct) =>
        {
            GuestContact guestContact;
            try
            {
                guestContact = new GuestContact(
                    request.DocumentId, GuestContactChannel.Email, request.Email, request.DisplayName, request.DeliveryNotes);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = [ex.Message],
                });
            }

            var scope = PublicScopeEndpointFilter.GetScope(httpContext);
            var outcome = await service.SubmitGuestAsync(
                scope,
                request.OrderId,
                request.VerificationId,
                guestContact,
                target.DestinationBranchId,
                request.Lines,
                request.CorrelationId,
                destination: null,
                hasAvailableStock: false,
                ct);

            return outcome.Status == OrderSubmissionOutcomeStatus.Accepted
                ? Results.Ok(outcome)
                : Results.Json(outcome, statusCode: StatusCodes.Status403Forbidden);
        }).RequireRateLimiting(PublicRateLimitPolicies.GuestOrderSubmit);

        return group;
    }
}

/// <summary>
/// Named rate-limit policy constants (design.md "Rate limiting") — a single
/// source of truth shared by `Program.cs`'s `AddRateLimiter` registration and
/// this file's `RequireRateLimiting` calls, so a typo cannot silently leave a
/// route unlimited.
/// </summary>
public static class PublicRateLimitPolicies
{
    public const string GuestVerificationRequest = "guest-verification-request";
    public const string GuestVerificationConfirm = "guest-verification-confirm";
    public const string GuestOrderSubmit = "guest-order-submit";
    public const string PublicCatalogRead = "public-catalog-read";
}

// Public DTOs (design.md "Interfaces / Contracts") — a guest cannot address
// another org or branch: neither field exists on any of these.
public sealed record GuestVerificationRequest(string DocumentId, string Email);

public sealed record GuestVerificationRequestedResponse(Guid VerificationId);

public sealed record GuestVerificationConfirmRequest(Guid VerificationId, string Code);

public sealed record SubmitGuestOrderRequest(
    Guid OrderId,
    Guid VerificationId,
    string DocumentId,
    string Email,
    string DisplayName,
    string? DeliveryNotes,
    IReadOnlyList<SubmitOrderLine> Lines,
    Guid CorrelationId);
