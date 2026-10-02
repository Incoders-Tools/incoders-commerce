using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Purchasing;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Goods receptions (`/purchases/receptions`), BRANCH-OWNED: every route needs the selected branch (`X-Branch-Id`,
/// 400 `branch-selection-required` otherwise) and the caller holds the supplier permission
/// (<see cref="Commerce.Domain.Identity.Permission.ManageUsers"/>, same guard as <see cref="SupplierEndpoints"/>).
/// A reception is a DRAFT (editable, no number) until POST .../confirm moves the stock, posts the Invoice to the supplier
/// account and assigns `R{branch}-W-{sequence}` in one transaction; POST .../void reverses all of it. A confirmed
/// reception is never edited or deleted.
/// </summary>
public static class PurchaseReceptionEndpoints
{
    private const int MaxLines = 500;
    private const int MaxReferenceLength = 100;
    private const int MaxNotesLength = 2000;
    private const int MaxLotLength = 100;
    private const int MaxReasonLength = 300;

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));

    public static RouteGroupBuilder MapPurchaseReceptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/purchases/receptions")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapGet("", async (
            string? status, Guid? supplierId, DateOnly? from, DateOnly? to, string? search,
            HttpContext httpContext, PostgresUserAccountStore userStore, PostgresPurchaseReceptionStore store, CancellationToken ct) =>
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

            ReceptionStatus? parsedStatus = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!char.IsLetter(status[0]) || !Enum.TryParse<ReceptionStatus>(status, out var value))
                {
                    return Problem("status", "status must be one of: Draft, Confirmed, Voided.");
                }

                parsedStatus = value;
            }

            if (from is { } start && to is { } end && start > end)
            {
                return Problem("from", "from cannot be after to.");
            }

            return Results.Ok(await store.ListAsync(
                auth.Value.Scope, new ReceptionListFilter(parsedStatus, supplierId, from, to, search), ct));
        });

        group.MapGet("/{id:guid}", async (
            Guid id, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresPurchaseReceptionStore store,
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

            var reception = await store.FindAsync(auth.Value.Scope, id, ct);
            return reception is null ? Results.NotFound() : Results.Ok(reception);
        });

        group.MapPost("", async (
            ReceptionRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresPurchaseReceptionStore store, CancellationToken ct) =>
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

            if (!TryBuildContent(request, out var content, out var problem))
            {
                return problem!;
            }

            var id = Guid.NewGuid();
            var result = await store.CreateDraftAsync(scope, id, content!, "org-user", caller.Id, ct);
            return result.Outcome == ReceptionWriteOutcome.Saved
                ? Results.Created($"/purchases/receptions/{id}", result.Reception)
                : WriteProblem(result);
        });

        group.MapPut("/{id:guid}", async (
            Guid id, ReceptionRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresPurchaseReceptionStore store, CancellationToken ct) =>
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

            if (!TryBuildContent(request, out var content, out var problem))
            {
                return problem!;
            }

            var result = await store.UpdateDraftAsync(
                scope, id, content!, request.ExpectedUpdatedAtUtc?.ToUniversalTime(), "org-user", caller.Id, ct);
            return result.Outcome switch
            {
                ReceptionWriteOutcome.Saved => Results.Ok(result.Reception),
                ReceptionWriteOutcome.NotFound => Results.NotFound(),
                ReceptionWriteOutcome.NotDraft => Results.Conflict(new { error = "reception-not-draft" }),
                ReceptionWriteOutcome.Modified => Results.Conflict(new { error = "reception-modified" }),
                _ => WriteProblem(result),
            };
        });

        group.MapPost("/{id:guid}/confirm", async (
            Guid id, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresPurchaseReceptionStore store,
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

            var result = await store.ConfirmAsync(auth.Value.Scope, id, "org-user", auth.Value.Caller.Id, ct);
            return result.Outcome switch
            {
                ReceptionConfirmOutcome.Confirmed => Results.Ok(result.Reception),
                ReceptionConfirmOutcome.NotFound => Results.NotFound(),
                ReceptionConfirmOutcome.NotDraft => Results.Conflict(new { error = "reception-not-draft" }),
                ReceptionConfirmOutcome.DuplicateDocument => Results.Conflict(new { error = "reception-duplicate-document" }),
                _ => Problem("lines", "A reception needs at least one line to be confirmed."),
            };
        });

        group.MapPost("/{id:guid}/void", async (
            Guid id, VoidReceptionRequest? request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresPurchaseReceptionStore store, CancellationToken ct) =>
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

            var reason = request?.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
            {
                return Problem("reason", $"reason is required and must be {MaxReasonLength} characters or fewer.");
            }

            var result = await store.VoidAsync(auth.Value.Scope, id, reason, Today(), "org-user", auth.Value.Caller.Id, ct);
            return result.Outcome switch
            {
                ReceptionVoidOutcome.Voided => Results.Ok(result.Reception),
                ReceptionVoidOutcome.NotFound => Results.NotFound(),
                _ => Results.Conflict(new { error = "reception-not-confirmed" }),
            };
        });

        return group;
    }

    private static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult WriteProblem(ReceptionWriteResult result) => Problem(result.Field ?? "lines", result.Message ?? "Invalid reception.");

    private static bool TryBuildContent(ReceptionRequest request, out ReceptionContent? content, out IResult? problem)
    {
        content = null;
        problem = null;

        if (request.SupplierId is not { } supplierId || supplierId == Guid.Empty)
        {
            problem = Problem("supplierId", "supplierId is required.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.DocumentType) || !char.IsLetter(request.DocumentType[0])
            || !Enum.TryParse<ReceptionDocumentType>(request.DocumentType, out var documentType))
        {
            problem = Problem("documentType", "documentType must be one of: Invoice, DeliveryNote, Other.");
            return false;
        }

        if (request.DocumentReference is { Length: > MaxReferenceLength })
        {
            problem = Problem("documentReference", $"documentReference must be {MaxReferenceLength} characters or fewer.");
            return false;
        }

        if (request.Notes is { Length: > MaxNotesLength })
        {
            problem = Problem("notes", $"notes must be {MaxNotesLength} characters or fewer.");
            return false;
        }

        var occurredOn = request.OccurredOn ?? Today();
        if (request.DueOn is { } due && due < occurredOn)
        {
            problem = Problem("dueOn", "dueOn cannot be before occurredOn.");
            return false;
        }

        var requested = request.Lines ?? [];
        if (requested.Count > MaxLines)
        {
            problem = Problem("lines", $"A reception holds at most {MaxLines} lines.");
            return false;
        }

        var lines = new List<NewReceptionLine>(requested.Count);
        for (var i = 0; i < requested.Count; i++)
        {
            var line = requested[i];
            if (line.PresentationId is not { } presentationId || presentationId == Guid.Empty)
            {
                problem = Problem($"lines[{i}].presentationId", "presentationId is required.");
                return false;
            }

            if (line.Quantity is not { } quantity)
            {
                problem = Problem($"lines[{i}].quantity", "quantity is required.");
                return false;
            }

            if (line.UnitCost is not { } unitCost)
            {
                problem = Problem($"lines[{i}].unitCost", "unitCost is required.");
                return false;
            }

            if (line.LotCode is { Length: > MaxLotLength })
            {
                problem = Problem($"lines[{i}].lotCode", $"lotCode must be {MaxLotLength} characters or fewer.");
                return false;
            }

            lines.Add(new NewReceptionLine(presentationId, quantity, unitCost, CustomerEndpoints.BlankToNull(line.LotCode), line.ExpiresOn));
        }

        content = new ReceptionContent(
            supplierId, documentType, CustomerEndpoints.BlankToNull(request.DocumentReference), occurredOn, request.DueOn,
            CustomerEndpoints.BlankToNull(request.Notes), lines);
        return true;
    }
}

/// <summary>
/// Body of POST (create a draft) and PUT (replace a draft, the WHOLE line set). `DocumentType`: Invoice, DeliveryNote or
/// Other. `DocumentReference` is the supplier's own document number (the duplicate guard compares it, ignoring case and
/// surrounding spaces). `OccurredOn` defaults to today. `DueOn` defaults at confirmation to occurredOn + the supplier's
/// payment terms. `ExpectedUpdatedAtUtc` (PUT only) is the optimistic-concurrency token: a mismatch answers 409
/// `reception-modified`. Quantity: whole number for a FixedQuantity presentation, up to 3 decimals otherwise; unit cost up
/// to 4 decimals.
/// </summary>
public sealed record ReceptionRequest(
    Guid? SupplierId,
    string? DocumentType,
    string? DocumentReference = null,
    DateOnly? OccurredOn = null,
    DateOnly? DueOn = null,
    string? Notes = null,
    IReadOnlyList<ReceptionLineRequest>? Lines = null,
    DateTimeOffset? ExpectedUpdatedAtUtc = null);

public sealed record ReceptionLineRequest(
    Guid? PresentationId, decimal? Quantity, decimal? UnitCost, string? LotCode = null, DateOnly? ExpiresOn = null);

/// <summary>Body of POST .../void: `Reason` is required.</summary>
public sealed record VoidReceptionRequest(string? Reason);
