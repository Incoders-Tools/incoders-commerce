using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Purchasing;
using Commerce.Domain.Stock;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Stock of the SELECTED branch (`/stock`), cloud-authoritative and derived: the on-hand of a presentation is the SUM of
/// its append-only movements. Every route needs `X-Branch-Id` (400 `branch-selection-required`) and the supplier
/// permission (<see cref="Commerce.Domain.Identity.Permission.ManageUsers"/>, as for receptions). Document-driven movements
/// (receptions, voids, synced sales) are never posted here: only manual adjustments are, and a movement can never be edited or
/// deleted, a mistake is another adjustment.
/// </summary>
public static class StockEndpoints
{
    private const int MaxReasonLength = 300;
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public static RouteGroupBuilder MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stock")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapGet("", async (
            string? search, bool? onlyBelowMinimum, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresStockStore store, CancellationToken ct) =>
        {
            if (BranchSelectionRequirement.Enforce(httpContext) is { } noBranch)
            {
                return noBranch;
            }

            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            return auth is null
                ? Results.Forbid()
                : Results.Ok(await store.ListLevelsAsync(auth.Value.Scope, new StockLevelFilter(search, onlyBelowMinimum == true), ct));
        });

        // Presentations below their minimum (for alerts and the dashboard).
        group.MapGet("/low", async (
            HttpContext httpContext, PostgresUserAccountStore userStore, PostgresStockStore store, CancellationToken ct) =>
        {
            if (BranchSelectionRequirement.Enforce(httpContext) is { } noBranch)
            {
                return noBranch;
            }

            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            return auth is null
                ? Results.Forbid()
                : Results.Ok(await store.ListLevelsAsync(auth.Value.Scope, new StockLevelFilter(null, OnlyBelowMinimum: true), ct));
        });

        group.MapGet("/{presentationId:guid}/movements", async (
            Guid presentationId, int? page, int? pageSize, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresStockStore store, CancellationToken ct) =>
        {
            if (BranchSelectionRequirement.Enforce(httpContext) is { } noBranch)
            {
                return noBranch;
            }

            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            if (page is < 1)
            {
                return Problem("page", "page starts at 1.");
            }

            if (pageSize is < 1 or > MaxPageSize)
            {
                return Problem("pageSize", $"pageSize must be between 1 and {MaxPageSize}.");
            }

            var history = await store.HistoryAsync(auth.Value.Scope, presentationId, page ?? 1, pageSize ?? DefaultPageSize, ct);
            return history is null ? Results.NotFound() : Results.Ok(history);
        });

        group.MapPost("/adjustments", async (
            StockAdjustmentRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresStockStore store,
            CancellationToken ct) =>
        {
            if (BranchSelectionRequirement.Enforce(httpContext) is { } noBranch)
            {
                return noBranch;
            }

            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (request.PresentationId is not { } presentationId || presentationId == Guid.Empty)
            {
                return Problem("presentationId", "presentationId is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Kind) || !char.IsLetter(request.Kind[0])
                || !Enum.TryParse<StockMovementKind>(request.Kind, out var kind) || !StockRules.ManualKinds.Contains(kind))
            {
                return Problem("kind", "kind must be one of: Opening, Shrinkage, CountCorrection, Adjustment.");
            }

            if (request.Quantity is not { } quantity)
            {
                return Problem("quantity", "quantity is required.");
            }

            var reason = request.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
            {
                return Problem("reason", $"reason is required and must be {MaxReasonLength} characters or fewer.");
            }

            if (await store.FindPresentationBehaviorAsync(scope, presentationId, ct) is not { } behavior)
            {
                return Results.NotFound();
            }

            if (!StockRules.TryValidateAdjustment(kind, behavior, quantity, out var error))
            {
                return Problem("quantity", error!);
            }

            var result = await store.AdjustAsync(scope, presentationId, kind, quantity, reason, "org-user", caller.Id, ct);
            return result is null ? Results.NotFound() : Results.Created($"/stock/{presentationId}/movements", result);
        });

        group.MapPut("/minimums/{presentationId:guid}", async (
            Guid presentationId, StockMinimumRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresStockStore store, CancellationToken ct) =>
        {
            if (BranchSelectionRequirement.Enforce(httpContext) is { } noBranch)
            {
                return noBranch;
            }

            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (await store.FindPresentationBehaviorAsync(scope, presentationId, ct) is not { } behavior)
            {
                return Results.NotFound();
            }

            if (request.MinimumQuantity is { } minimum
                && (minimum < 0 || (minimum > 0 && !ReceptionRules.TryValidateQuantity(behavior, minimum, out _))))
            {
                return Problem("minimumQuantity", "minimumQuantity must be zero or more, a whole number for a fixed-quantity presentation, with at most 3 decimals.");
            }

            var record = await store.SetMinimumAsync(scope, presentationId, request.MinimumQuantity, "org-user", caller.Id, ct);
            return record is null ? Results.NotFound() : Results.Ok(record);
        });

        return group;
    }

    private static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// Body of POST /stock/adjustments. `Kind`: Opening (quantity &gt; 0), Shrinkage (&lt; 0), CountCorrection or Adjustment (either
/// sign, never zero). `Quantity` is SIGNED, in the presentation's unit: a whole number for a FixedQuantity presentation, up
/// to 3 decimals otherwise. `Reason` is required.
/// </summary>
public sealed record StockAdjustmentRequest(Guid? PresentationId, string? Kind, decimal? Quantity, string? Reason);

/// <summary>Body of PUT /stock/minimums/{presentationId}: a null (or omitted) `MinimumQuantity` clears the minimum.</summary>
public sealed record StockMinimumRequest(decimal? MinimumQuantity);
