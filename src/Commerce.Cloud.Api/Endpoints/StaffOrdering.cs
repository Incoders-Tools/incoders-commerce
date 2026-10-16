using System.Security.Claims;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Ordering;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Staff order taking (staff-order-taking T2): a signed-in staff member takes an order for a customer of the
/// organization. Authorized by the CALLER's <see cref="Permission.TakeOrders"/> (seller, business admin, or a system
/// administrator acting on the selected organization through <see cref="ActingPermissions"/>), never by a customer
/// credential. Browser cookie only: these routes are deliberately NOT opted into the device-operator policy.
///
/// Every route requires a selected branch (<see cref="BranchSelectionRequirement"/>): the order's destination is that
/// branch and the catalog/price lists the screen reads are that branch's. The actor is the caller, the organization is
/// the tenant scope; neither is a request field. Thin mapping onto <see cref="CloudOrderSubmissionService"/> (checks
/// and pricing shared with the self-service path) and <see cref="PostgresStaffOrderLookupStore"/> (read-only lookups,
/// because the registry and catalog reads need ManageUsers / ManageCatalog).
/// </summary>
public static class StaffOrderingEndpoints
{
    /// <summary>Most lines one staff order (or quote) may carry.</summary>
    public const int MaxLines = 200;

    public static RouteGroupBuilder MapStaffOrderingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders/staff")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapPost("/quote", async (
            StaffQuoteRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            CloudOrderSubmissionService service,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, ct);
            if (auth.Result is not null) return auth.Result;

            var invalid = Validate(orderId: null, request.CustomerId, request.Lines, note: null);
            if (invalid is not null) return invalid;

            var quote = await service.QuoteForStaffAsync(auth.Scope!, request.CustomerId, request.Lines!, ct);
            return quote.Status == StaffOrderQuote.QuotedStatus
                ? Results.Ok(quote)
                : Results.Json(quote, statusCode: DenialStatusCode(quote.Reason));
        });

        group.MapPost("/", async (
            StaffSubmitOrderRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            CloudOrderSubmissionService service,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, ct);
            if (auth.Result is not null) return auth.Result;

            var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
            var invalid = Validate(request.OrderId, request.CustomerId, request.Lines, note);
            if (invalid is not null) return invalid;

            var scope = auth.Scope!;
            // The destination is always the selected branch; delivery is attempted like POST /orders (no live
            // branch connection registry in this host yet, so the order is stored honestly pending).
            var outcome = await service.SubmitForStaffAsync(
                scope,
                request.CustomerId,
                request.OrderId,
                scope.BranchId!.Value,
                new StaffOrderEntry(auth.CallerId, note),
                request.Lines!,
                correlationId: Guid.NewGuid(),
                destination: null,
                hasAvailableStock: false,
                ct);

            var response = new StaffOrderSubmissionResponse(
                outcome.Status == OrderSubmissionOutcomeStatus.Accepted ? "accepted" : "denied",
                outcome.Reason,
                outcome.WasNewlyAccepted,
                outcome.Order?.OrderNumber?.Format(),
                outcome.Order);
            return outcome.Status == OrderSubmissionOutcomeStatus.Accepted
                ? Results.Ok(response)
                : Results.Json(response, statusCode: DenialStatusCode(outcome.Reason));
        });

        group.MapGet("/customers", async (
            string? search,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresStaffOrderLookupStore lookups,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, ct);
            if (auth.Result is not null) return auth.Result;
            return Results.Ok(await lookups.SearchCustomersAsync(auth.Scope!, search, ct));
        });

        group.MapGet("/presentations", async (
            string? search,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresStaffOrderLookupStore lookups,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, ct);
            if (auth.Result is not null) return auth.Result;
            return Results.Ok(await lookups.SearchPresentationsAsync(auth.Scope!, search, ct));
        });

        return group;
    }

    /// <summary>
    /// Selected branch first (400 <c>branch-selection-required</c>, as the catalog routes), then the caller: id from the
    /// NameIdentifier claim, loaded through the store, not revoked, holding <see cref="Permission.TakeOrders"/> for
    /// this request (403 otherwise). A literal 403, not <c>Results.Forbid()</c>: the staff cookie scheme has no
    /// <c>OnRedirectToAccessDenied</c> override, so <c>Forbid()</c> would redirect (see TenantScopeEndpointFilter).
    /// </summary>
    private static async Task<(IResult? Result, CloudTenantScope? Scope, Guid CallerId)> AuthorizeAsync(
        HttpContext httpContext, PostgresUserAccountStore userStore, CancellationToken ct)
    {
        var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
        if (branchFailure is not null) return (branchFailure, null, Guid.Empty);

        var scope = TenantScopeEndpointFilter.GetScope(httpContext);
        var callerIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (callerIdClaim is null || !Guid.TryParse(callerIdClaim, out var callerId))
        {
            return (Results.StatusCode(StatusCodes.Status403Forbidden), null, Guid.Empty);
        }

        var caller = await userStore.LoadActorAsync(scope.IdentityScope, callerId, ct);
        if (caller is null || caller.IsRevoked || !ActingPermissions.For(caller, scope).HasFlag(Permission.TakeOrders))
        {
            return (Results.StatusCode(StatusCodes.Status403Forbidden), null, Guid.Empty);
        }

        return (null, scope, callerId);
    }

    private static IResult? Validate(Guid? orderId, Guid customerId, IReadOnlyList<SubmitOrderLine>? lines, string? note)
    {
        var errors = new Dictionary<string, string[]>();
        if (orderId == Guid.Empty) errors["orderId"] = ["orderId is required."];
        if (customerId == Guid.Empty) errors["customerId"] = ["customerId is required."];
        if (lines is null || lines.Count == 0) errors["lines"] = ["At least one line is required."];
        else if (lines.Count > MaxLines) errors["lines"] = [$"At most {MaxLines} lines."];
        else if (lines.Any(l => l is null || l.PresentationId == Guid.Empty || l.Quantity <= 0m))
            errors["lines"] = ["Every line needs a presentationId and a quantity greater than zero."];
        if (note is { Length: > Order.MaxNoteLength }) errors["note"] = [$"note has at most {Order.MaxNoteLength} characters."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static int DenialStatusCode(string? reason) => reason switch
    {
        "not-found" => StatusCodes.Status404NotFound,
        "customer-disabled" => StatusCodes.Status409Conflict,
        "no-effective-price" => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status409Conflict,
    };
}

/// <summary>Quote body: the customer and the lines (no prices: a client has nowhere to put one).</summary>
public sealed record StaffQuoteRequest(Guid CustomerId, IReadOnlyList<SubmitOrderLine>? Lines);

/// <summary>
/// Submit body. <c>OrderId</c> is the client-generated idempotency key; the destination branch (selected branch), the
/// actor (the caller) and the organization are never request fields.
/// </summary>
public sealed record StaffSubmitOrderRequest(Guid OrderId, Guid CustomerId, IReadOnlyList<SubmitOrderLine>? Lines, string? Note = null);

/// <summary>
/// Submit outcome. <c>Status</c> is <c>accepted</c> or <c>denied</c>; <c>Reason</c> is <c>accepted</c> /
/// <c>existing-order</c> (a resubmitted order id) or the denial reason; <c>OrderNumber</c> is the human number
/// (<c>P01-W-37</c>).
/// </summary>
public sealed record StaffOrderSubmissionResponse(string Status, string Reason, bool WasNewlyAccepted, string? OrderNumber, Order? Order);
