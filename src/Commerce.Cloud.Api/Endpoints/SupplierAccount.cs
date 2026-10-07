using System.Text.Json.Serialization;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Current accounts of suppliers (`/suppliers/{id}/account/...`, `/suppliers/account/balances`) and customers
/// (`/customers/{id}/account/...`, `/customers/account/balances`): APPEND-ONLY ledgers. A movement is never edited or
/// deleted; a mistake is corrected by a compensating Reversal. Authorization: ManageUsers, as the supplier and customer
/// registries.
/// <para>
/// SIGN CONVENTION, per party (<see cref="PartyAccountRules"/>): a supplier's balance is what the business owes it
/// (an invoice is a Credit, a payment a Debit); a customer's balance is what the customer owes the business (a sale or
/// delivery on account is a Debit, a payment received a Credit). The kind decides the direction; only an Adjustment
/// names it, in the party's own terms. The FIFO aging of the summary is the same rule for both.
/// </para>
/// </summary>
public static class SupplierAccountEndpoints
{
    private const decimal MaxAmount = 9_999_999_999_999_999.99m;
    private const int MaxConceptLength = 500;
    private const int MaxReferenceLength = 100;

    public static RouteGroupBuilder MapSupplierAccountEndpoints(this IEndpointRouteBuilder app) =>
        MapPartyAccountEndpoints(app, "/suppliers", AccountPartyKind.Supplier);

    public static RouteGroupBuilder MapCustomerAccountEndpoints(this IEndpointRouteBuilder app) =>
        MapPartyAccountEndpoints(app, "/customers", AccountPartyKind.Customer);

    /// <summary>The employees' accounts (`/employees/{id}/account/...`): what the business owes each one.</summary>
    public static RouteGroupBuilder MapEmployeeAccountEndpoints(this IEndpointRouteBuilder app) =>
        MapPartyAccountEndpoints(app, "/employees", AccountPartyKind.Employee);

    private static RouteGroupBuilder MapPartyAccountEndpoints(this IEndpointRouteBuilder app, string prefix, AccountPartyKind partyKind)
    {
        var group = app.MapGroup(prefix)
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();
        AccountParty Party(Guid id) => new(partyKind, id);

        group.MapPost("/{id:guid}/account/movements", async (
            Guid id, RegisterMovementRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresCurrentAccountStore store, CancellationToken ct) =>
        {
            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (Validate(partyKind, request, httpContext.Today()) is { } problem)
            {
                return problem;
            }

            // Validate() guarantees these parse and resolve.
            var kind = Enum.Parse<AccountMovementKind>(request.Kind!);
            PartyAccountRules.TryResolveDirection(partyKind, kind, ParseDirection(request.Direction), out var direction, out _);
            var movement = new NewAccountMovement(
                Guid.NewGuid(), kind, direction, request.Amount!.Value, request.OccurredOn ?? httpContext.Today(), request.DueOn,
                CustomerEndpoints.BlankToNull(request.DocumentReference), request.Concept!.Trim(), caller.Id);

            var created = await store.RegisterAsync(scope, Party(id), movement, "org-user", caller.Id, ct);
            return created is null
                ? Results.NotFound()
                : Results.Created($"{prefix}/{id}/account/movements/{created.Id}", created);
        });

        group.MapPost("/{id:guid}/account/movements/{movementId:guid}/reverse", async (
            Guid id, Guid movementId, ReverseMovementRequest? request, HttpContext httpContext,
            PostgresUserAccountStore userStore, PostgresCurrentAccountStore store, CancellationToken ct) =>
        {
            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (request?.Concept is { Length: > MaxConceptLength })
            {
                return Problem("concept", $"concept must be {MaxConceptLength} characters or fewer.");
            }

            var result = await store.ReverseAsync(
                scope, Party(id), movementId, request?.Concept, request?.OccurredOn, httpContext.Today(), "org-user", caller.Id, ct);
            return result.Outcome switch
            {
                ReverseMovementOutcome.Reversed =>
                    Results.Created($"{prefix}/{id}/account/movements/{result.Reversal!.Id}", result.Reversal),
                ReverseMovementOutcome.NotFound => Results.NotFound(),
                ReverseMovementOutcome.AlreadyReversed => Results.Conflict(new { error = "movement-already-reversed" }),
                ReverseMovementOutcome.NotReversible => Results.Conflict(new { error = "movement-not-reversible" }),
                _ => Problem("occurredOn", "occurredOn cannot be before the date of the reversed movement."),
            };
        });

        var statementEndpoint = group.MapGet("/{id:guid}/account/statement", async (
            Guid id, DateOnly? from, DateOnly? to, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresCurrentAccountStore store, CancellationToken ct) =>
        {
            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            if (from is { } start && to is { } end && start > end)
            {
                return Problem("from", "from cannot be after to.");
            }

            var movements = await store.ListMovementsAsync(auth.Value.Scope, Party(id), ct);
            return movements is null ? Results.NotFound() : Results.Ok(BuildStatement(partyKind, movements, from, to));
        });

        var summaryEndpoint = group.MapGet("/{id:guid}/account/summary", async (
            Guid id, DateOnly? asOf, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresCurrentAccountStore store, CancellationToken ct) =>
        {
            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var movements = await store.ListMovementsAsync(auth.Value.Scope, Party(id), ct);
            if (movements is null)
            {
                return Results.NotFound();
            }

            var date = asOf ?? httpContext.Today();
            var summary = PartyAccountRules.Summarize(partyKind, movements.Select(m => m.ToFact()).ToList(), date);
            return Results.Ok(new AccountSummaryResponse(
                date, summary.Balance, summary.Overdue, summary.Current,
                new AgingResponse(summary.Aging.D0To30, summary.Aging.D31To60, summary.Aging.D61To90, summary.Aging.D90Plus)));
        });

        group.MapGet("/account/balances", async (
            DateOnly? asOf, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresCurrentAccountStore store, CancellationToken ct) =>
        {
            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var date = asOf ?? httpContext.Today();
            return partyKind switch
            {
                AccountPartyKind.Supplier => Results.Ok(await store.BalancesAsync(auth.Value.Scope, date, ct)),
                AccountPartyKind.Customer => Results.Ok(await store.CustomerBalancesAsync(auth.Value.Scope, date, ct)),
                _ => Results.Ok(await store.EmployeeBalancesAsync(auth.Value.Scope, date, ct)),
            };
        });

        // An employee's account is also read at the POS (Personal → Empleados → Cuenta).
        if (partyKind == AccountPartyKind.Employee)
        {
            summaryEndpoint.AllowDeviceOperator();
            statementEndpoint.AllowDeviceOperator();
        }

        return group;
    }

    private static AccountDirection? ParseDirection(string? raw) =>
        Enum.TryParse<AccountDirection>(raw, out var direction) && raw is { Length: > 0 } && char.IsLetter(raw[0]) ? direction : null;

    private static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult? Validate(AccountPartyKind partyKind, RegisterMovementRequest request, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(request.Kind) || !char.IsLetter(request.Kind[0])
            || !Enum.TryParse<AccountMovementKind>(request.Kind, out var kind))
        {
            return Problem("kind", "kind must be one of: OpeningBalance, Invoice, DebitNote, CreditNote, Payment, Adjustment.");
        }

        if (kind == AccountMovementKind.Reversal)
        {
            return Problem("kind", "A Reversal is created with POST .../movements/{movementId}/reverse, not registered directly.");
        }

        if (request.Amount is not { } amount || amount <= 0 || amount > MaxAmount || decimal.Round(amount, 2) != amount)
        {
            return Problem("amount", "amount must be greater than zero, with at most two decimals.");
        }

        if (string.IsNullOrWhiteSpace(request.Concept) || request.Concept.Trim().Length > MaxConceptLength)
        {
            return Problem("concept", $"concept is required and must be {MaxConceptLength} characters or fewer.");
        }

        if (request.DocumentReference is { Length: > MaxReferenceLength })
        {
            return Problem("documentReference", $"documentReference must be {MaxReferenceLength} characters or fewer.");
        }

        if (!string.IsNullOrWhiteSpace(request.Direction) && ParseDirection(request.Direction) is null)
        {
            return Problem("direction", "direction must be Debit or Credit.");
        }

        if (!PartyAccountRules.TryResolveDirection(partyKind, kind, ParseDirection(request.Direction), out var direction, out var directionError))
        {
            return Problem("direction", directionError!);
        }

        if (request.DueOn is { } due)
        {
            var debt = PartyAccountRules.DebtDirection(partyKind);
            if (direction != debt)
            {
                return Problem("dueOn", $"dueOn only applies to movements that increase the debt ({debt}).");
            }

            if (due < (request.OccurredOn ?? today))
            {
                return Problem("dueOn", "dueOn cannot be before occurredOn.");
            }
        }

        return null;
    }

    /// <summary>
    /// Opening balance = balance of the movements before `from` (0 without `from`); the lines are the movements in
    /// [from, to] in ledger order with the balance after each one; closing balance = balance as of `to`.
    /// A movement is `reversed` when a Reversal of it exists (whatever its date), and stays visible.
    /// </summary>
    private static StatementResponse BuildStatement(
        AccountPartyKind partyKind, IReadOnlyList<AccountMovementRecord> all, DateOnly? from, DateOnly? to)
    {
        var reversedBy = all
            .Where(m => m.ReversesMovementId is not null)
            .ToDictionary(m => m.ReversesMovementId!.Value, m => m.Id);

        var opening = from is { } start
            ? PartyAccountRules.Balance(partyKind, all.Where(m => m.OccurredOn < start).Select(m => m.ToFact()))
            : 0m;

        var running = opening;
        var lines = new List<StatementLine>();
        foreach (var m in all.Where(m => (from is null || m.OccurredOn >= from) && (to is null || m.OccurredOn <= to)))
        {
            running += PartyAccountRules.SignedAmount(partyKind, m.Direction, m.Amount);
            var reversedById = reversedBy.TryGetValue(m.Id, out var reversal) ? reversal : (Guid?)null;
            lines.Add(new StatementLine(
                m.Id, m.Kind.ToString(), m.Direction.ToString(), m.Amount, m.OccurredOn, m.DueOn, m.DocumentReference,
                m.Concept, m.ReversesMovementId, m.CreatedAtUtc, running, reversedById is not null, reversedById));
        }

        return new StatementResponse(opening, lines, running);
    }
}

/// <summary>
/// Body of POST .../account/movements. `Kind`: OpeningBalance, Invoice, DebitNote, CreditNote, Payment or Adjustment.
/// `Amount` is positive with at most two decimals. `OccurredOn` defaults to today. `DueOn` (Credit movements only) on an
/// Invoice defaults to occurredOn + the supplier's payment terms when the supplier has them. `Direction` (Debit/Credit)
/// is required only for an Adjustment.
/// </summary>
public sealed record RegisterMovementRequest(
    string? Kind, decimal? Amount, DateOnly? OccurredOn = null, DateOnly? DueOn = null, string? DocumentReference = null,
    string? Concept = null, string? Direction = null);

/// <summary>Body of POST .../reverse (all optional): `Concept` defaults to "Reversal: {original concept}"; `OccurredOn` to today.</summary>
public sealed record ReverseMovementRequest(string? Concept = null, DateOnly? OccurredOn = null);

public sealed record StatementLine(
    Guid Id, string Kind, string Direction, decimal Amount, DateOnly OccurredOn, DateOnly? DueOn, string? DocumentReference,
    string Concept, Guid? ReversesMovementId, DateTimeOffset CreatedAtUtc, decimal RunningBalance, bool Reversed,
    Guid? ReversedByMovementId);

public sealed record StatementResponse(decimal OpeningBalance, IReadOnlyList<StatementLine> Movements, decimal ClosingBalance);

public sealed record AgingResponse(
    [property: JsonPropertyName("d0_30")] decimal D0To30,
    [property: JsonPropertyName("d31_60")] decimal D31To60,
    [property: JsonPropertyName("d61_90")] decimal D61To90,
    [property: JsonPropertyName("d90plus")] decimal D90Plus);

public sealed record AccountSummaryResponse(DateOnly AsOf, decimal Balance, decimal Overdue, decimal Current, AgingResponse Aging);
