using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// The company's treasury (0046-0048). Read: the money accounts with their type, balance and today's In/Out, and the
/// movements of one account. Write: the administration manages the accounts (create, rename, re-type, activate or
/// deactivate), records money In or Out by hand, transfers between accounts, and voids or edits movements (a voided one is
/// kept and audited but counts nowhere; an edit is a void plus its replacement). Sales and customer payments are voided at
/// the POS; here they only change account. The account types are their own catalog (`/treasury/account-types`,
/// <see cref="MasterDataEndpoints"/>). Everything is administration only (<see cref="Permission.ManageUsers"/>), the
/// actor is always the caller, and every write leaves an audit row.
/// </summary>
public static class TreasuryEndpoints
{
    public const int MaxNameLength = 120;
    public const int MaxTextLength = 200;
    public const int MaxReferenceLength = 60;
    public const int MaxReasonLength = 200;
    private const decimal MaxAmount = 999_999_999_999m;

    public static void MapTreasuryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/treasury").RequireAuthorization().AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapGet("/accounts", async (
            Guid? branchId, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresTreasuryStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            return Results.Ok(await store.ListAccountsAsync(caller!.Scope, branchId, httpContext.Today(), ct));
        }).AllowDeviceOperator(); // the POS picks where an advance is paid from

        group.MapPost("/accounts", async (
            CreateTreasuryAccountRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresTreasuryStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;

            var errors = new Dictionary<string, string[]>();
            var kind = string.IsNullOrWhiteSpace(request.Kind) ? PostgresTreasuryStore.Other : request.Kind.Trim();
            var name = request.Name?.Trim() ?? string.Empty;
            var description = Blank(request.Description);
            if (!PostgresTreasuryStore.ManualAccountKinds.Contains(kind)) errors["kind"] = ["kind is Safe, Bank or Other."];
            ValidateAccount(errors, name, description);
            if (kind == PostgresTreasuryStore.Safe && request.BranchId is null) errors["branchId"] = ["A safe belongs to a branch."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var (outcome, account) = await store.CreateAccountAsync(
                caller!.Scope, caller.Id, kind, name, request.AccountTypeId, request.BranchId, description, httpContext.Today(), ct);
            return outcome == TreasuryWriteOutcome.Done
                ? Results.Created($"/treasury/accounts/{account!.AccountId}", account)
                : Refusal(outcome);
        });

        group.MapPut("/accounts/{accountId:guid}", async (
            Guid accountId, UpdateTreasuryAccountRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresTreasuryStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;

            var errors = new Dictionary<string, string[]>();
            var name = request.Name?.Trim() ?? string.Empty;
            var description = Blank(request.Description);
            ValidateAccount(errors, name, description);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var (outcome, account) = await store.UpdateAccountAsync(
                caller!.Scope, caller.Id, accountId, name, request.AccountTypeId, description, httpContext.Today(), ct);
            return outcome == TreasuryWriteOutcome.Done ? Results.Ok(account) : Refusal(outcome);
        });

        group.MapPost("/accounts/{accountId:guid}/active", async (
            Guid accountId, SetTreasuryAccountActiveRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresTreasuryStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;

            var outcome = await store.SetAccountActiveAsync(caller!.Scope, caller.Id, accountId, request.IsActive, httpContext.Today(), ct);
            return outcome is TreasuryWriteOutcome.Done or TreasuryWriteOutcome.NothingChanged ? Results.NoContent() : Refusal(outcome);
        });

        group.MapGet("/accounts/{accountId:guid}/movements", async (
            Guid accountId, DateOnly? from, DateOnly? to, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresTreasuryStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            if (from is { } start && to is { } end && start > end)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["from cannot be after to."] });
            }

            return await store.ListMovementsAsync(caller!.Scope, accountId, from, to, ct) is { } movements
                ? Results.Ok(movements)
                : Results.NotFound();
        });

        group.MapPost("/movements", async (
            RecordTreasuryMovementRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresTreasuryStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;

            var today = httpContext.Today();
            var errors = ValidateMoney(request.Amount, request.Date, request.Concept, request.Reference, today);
            if (request.Direction is not ("In" or "Out")) errors["direction"] = ["direction is In or Out."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var (outcome, id) = await store.RecordManualAsync(
                caller!.Scope, caller.Id, request.AccountId, request.Direction!, request.Amount, request.Date ?? today, request.Concept!.Trim(),
                Blank(request.Reference), ct);
            return outcome == TreasuryWriteOutcome.Done ? Results.Ok(new { movementId = id }) : Refusal(outcome);
        });

        group.MapPost("/transfers", async (
            TreasuryTransferRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresTreasuryStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;

            var today = httpContext.Today();
            var errors = ValidateMoney(request.Amount, request.Date, request.Concept, request.Reference, today);
            if (request.FromAccountId == request.ToAccountId) errors["toAccountId"] = ["The accounts of a transfer are different."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var (outcome, transferId) = await store.TransferAsync(
                caller!.Scope, caller.Id, request.FromAccountId, request.ToAccountId, request.Amount, request.Date ?? today,
                request.Concept!.Trim(), Blank(request.Reference), ct);
            return outcome == TreasuryWriteOutcome.Done ? Results.Ok(new { transferId }) : Refusal(outcome);
        });

        // Recurring movements (fixed expenses, recurring income): recorded automatically on their dates.
        group.MapGet("/recurrences", async (
            HttpContext httpContext, PostgresUserAccountStore userStore, PostgresTreasuryRecurrenceStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            return Results.Ok(await store.ListAsync(caller!.Scope, httpContext.Today(), ct));
        });

        group.MapPost("/recurrences", async (
            TreasuryRecurrenceRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresTreasuryRecurrenceStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            if (ValidateRecurrence(request, out var input) is { } invalid) return invalid;

            var (outcome, id) = await store.CreateAsync(caller!.Scope, caller.Id, input!, httpContext.Today(), ct);
            return outcome == RecurrenceWriteOutcome.Done ? Results.Created($"/treasury/recurrences/{id}", new { recurrenceId = id }) : RecurrenceRefusal(outcome);
        });

        group.MapPut("/recurrences/{recurrenceId:guid}", async (
            Guid recurrenceId, TreasuryRecurrenceRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresTreasuryRecurrenceStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            if (ValidateRecurrence(request, out var input) is { } invalid) return invalid;

            var outcome = await store.UpdateAsync(caller!.Scope, caller.Id, recurrenceId, input!, httpContext.Today(), ct);
            return outcome == RecurrenceWriteOutcome.Done ? Results.NoContent() : RecurrenceRefusal(outcome);
        });

        group.MapPost("/recurrences/{recurrenceId:guid}/active", async (
            Guid recurrenceId, SetTreasuryAccountActiveRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresTreasuryRecurrenceStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            var outcome = await store.SetActiveAsync(caller!.Scope, caller.Id, recurrenceId, request.IsActive, httpContext.Today(), ct);
            return outcome == RecurrenceWriteOutcome.Done ? Results.NoContent() : RecurrenceRefusal(outcome);
        });

        // Brings into the treasury the operations the terminals pushed before it existed (or whose posting failed): idempotent.
        group.MapPost("/reprocess", async (
            HttpContext httpContext, PostgresUserAccountStore userStore, PostgresTreasuryStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            return Results.Ok(new { movementsAdded = await store.ReprocessAsync(caller!.Scope, caller.Id, ct) });
        });

        group.MapPost("/movements/{movementId:guid}/void", async (
            Guid movementId, VoidTreasuryMovementRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresTreasuryStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;

            var reason = request.Reason?.Trim() ?? string.Empty;
            if (reason.Length is 0 or > MaxReasonLength)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = [$"reason has 1 to {MaxReasonLength} characters."] });
            }

            var outcome = await store.VoidAsync(caller!.Scope, caller.Id, movementId, reason, ct);
            return outcome == TreasuryWriteOutcome.Done ? Results.NoContent() : Refusal(outcome);
        });

        group.MapPut("/movements/{movementId:guid}", async (
            Guid movementId, EditTreasuryMovementRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresTreasuryStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;

            var today = httpContext.Today();
            var errors = new Dictionary<string, string[]>();
            var reason = request.Reason?.Trim() ?? string.Empty;
            if (reason.Length is 0 or > MaxReasonLength) errors["reason"] = [$"reason has 1 to {MaxReasonLength} characters."];
            if (request.Amount is { } amount && (amount <= 0m || decimal.Round(amount, 2) != amount || amount > MaxAmount))
            {
                errors["amount"] = ["amount is positive with at most two decimals."];
            }

            if (request.Date is { } date && date > today) errors["date"] = ["date cannot be in the future."];
            if (request.Concept is { } concept && concept.Trim().Length is 0 or > MaxTextLength) errors["concept"] = [$"concept has 1 to {MaxTextLength} characters."];
            if (request.Reference?.Trim() is { Length: > MaxReferenceLength }) errors["reference"] = [$"reference has at most {MaxReferenceLength} characters."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var (outcome, replacement) = await store.EditAsync(caller!.Scope, caller.Id, movementId, new TreasuryMovementEdit(
                request.AccountId, request.ToAccountId, request.Amount, request.Date, request.Concept?.Trim(), request.Reference?.Trim(), reason), ct);
            return outcome == TreasuryWriteOutcome.Done ? Results.Ok(new { movementId = replacement }) : Refusal(outcome);
        });
    }

    /// <summary>A refused write: 404 for what does not exist, 409 with a machine code for a rule.</summary>
    private static IResult Refusal(TreasuryWriteOutcome outcome) => outcome switch
    {
        TreasuryWriteOutcome.AccountNotFound or TreasuryWriteOutcome.BranchNotFound or TreasuryWriteOutcome.MovementNotFound => Results.NotFound(),
        TreasuryWriteOutcome.AccountTypeNotFound => Results.Conflict(new { error = "account-type-not-found" }),
        TreasuryWriteOutcome.SafeAlreadyExists => Results.Conflict(new { error = "safe-already-exists" }),
        TreasuryWriteOutcome.SameAccount => Results.Conflict(new { error = "same-account" }),
        TreasuryWriteOutcome.AccountInactive => Results.Conflict(new { error = "account-inactive" }),
        TreasuryWriteOutcome.AutomaticAccount => Results.Conflict(new { error = "automatic-account" }),
        TreasuryWriteOutcome.BalanceNotZero => Results.Conflict(new { error = "balance-not-zero" }),
        TreasuryWriteOutcome.OnlyAccountEditable => Results.Conflict(new { error = "only-account-editable" }),
        TreasuryWriteOutcome.NothingChanged => Results.Conflict(new { error = "nothing-changed" }),
        _ => Results.Conflict(new { error = "not-voidable" }),
    };

    private static IResult? ValidateRecurrence(TreasuryRecurrenceRequest request, out TreasuryRecurrenceInput? input)
    {
        input = null;
        var errors = new Dictionary<string, string[]>();
        var concept = request.Concept?.Trim() ?? string.Empty;
        var schedule = new Commerce.Domain.Treasury.RecurrenceSchedule(
            request.StartDate, request.Frequency ?? string.Empty, request.Interval ?? 1, request.EndMode ?? Commerce.Domain.Treasury.RecurrenceEnd.Never,
            request.EndDate, request.MaxOccurrences);
        if (request.Direction is not ("In" or "Out")) errors["direction"] = ["direction is In or Out."];
        if (request.Amount <= 0m || decimal.Round(request.Amount, 2) != request.Amount || request.Amount > MaxAmount) errors["amount"] = ["amount is positive with at most two decimals."];
        if (concept.Length is 0 or > MaxTextLength) errors["concept"] = [$"concept has 1 to {MaxTextLength} characters."];
        if (request.Reference?.Trim() is { Length: > MaxReferenceLength }) errors["reference"] = [$"reference has at most {MaxReferenceLength} characters."];
        if (request.StartDate == default) errors["startDate"] = ["startDate is required."];
        else if (!schedule.IsValid()) errors["schedule"] = ["frequency (Weekly, Monthly, Yearly), interval (1 to 24) and the end (Never; OnDate with an endDate not before the start; AfterCount with 1 to 1000 occurrences) are required."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        input = new TreasuryRecurrenceInput(
            request.AccountId, request.Direction!, request.Amount, concept, Blank(request.Reference), schedule.Frequency, schedule.Interval,
            request.StartDate, schedule.EndMode, request.EndDate, request.MaxOccurrences, request.IncludePastDates ?? false);
        return null;
    }

    private static IResult RecurrenceRefusal(RecurrenceWriteOutcome outcome) => outcome switch
    {
        RecurrenceWriteOutcome.NotFound or RecurrenceWriteOutcome.AccountNotFound => Results.NotFound(),
        RecurrenceWriteOutcome.AccountInactive => Results.Conflict(new { error = "account-inactive" }),
        _ => Results.Conflict(new { error = "schedule-locked" }),
    };

    private static void ValidateAccount(Dictionary<string, string[]> errors, string name, string? description)
    {
        if (name.Length is 0 or > MaxNameLength) errors["name"] = [$"name has 1 to {MaxNameLength} characters."];
        if (description is { Length: > MaxTextLength }) errors["description"] = [$"description has at most {MaxTextLength} characters."];
    }

    private static Dictionary<string, string[]> ValidateMoney(decimal amount, DateOnly? date, string? concept, string? reference, DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        if (amount <= 0m || decimal.Round(amount, 2) != amount || amount > MaxAmount)
        {
            errors["amount"] = ["amount is positive with at most two decimals."];
        }

        if (date is { } day && day > today) errors["date"] = ["date cannot be in the future."];
        var text = concept?.Trim() ?? string.Empty;
        if (text.Length is 0 or > MaxTextLength) errors["concept"] = [$"concept has 1 to {MaxTextLength} characters."];
        if (reference?.Trim() is { Length: > MaxReferenceLength }) errors["reference"] = [$"reference has at most {MaxReferenceLength} characters."];
        return errors;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A new account of a type of the organization's catalog (its default type when omitted). Kind: Other (default), Bank,
/// or Safe (the branch safe the POS withdraws "to the safe" into: needs its branch). Without a branch it is company-wide.
/// </summary>
public sealed record CreateTreasuryAccountRequest(string? Kind, string? Name, Guid? BranchId, string? Description, Guid? AccountTypeId = null);

/// <summary>Name, type (null: without type) and description of an account; its branch and kind never change.</summary>
public sealed record UpdateTreasuryAccountRequest(string? Name, Guid? AccountTypeId, string? Description);

public sealed record SetTreasuryAccountActiveRequest(bool IsActive);

/// <summary>Money In or Out of an account by hand. Date: the business date (today when omitted, never in the future).</summary>
public sealed record RecordTreasuryMovementRequest(
    Guid AccountId, string? Direction, decimal Amount, DateOnly? Date, string? Concept, string? Reference);

/// <summary>Money from one account to another.</summary>
public sealed record TreasuryTransferRequest(
    Guid FromAccountId, Guid ToAccountId, decimal Amount, DateOnly? Date, string? Concept, string? Reference);

public sealed record VoidTreasuryMovementRequest(string? Reason);

/// <summary>
/// A recurring movement: In or Out of an account, every Interval weeks/months/years (Frequency) from StartDate, ending
/// Never, OnDate (EndDate) or AfterCount (MaxOccurrences). IncludePastDates on create: also record the dates since the
/// start that already passed (otherwise from today).
/// </summary>
public sealed record TreasuryRecurrenceRequest(
    Guid AccountId,
    string? Direction,
    decimal Amount,
    string? Concept,
    string? Reference,
    string? Frequency,
    int? Interval,
    DateOnly StartDate,
    string? EndMode,
    DateOnly? EndDate,
    int? MaxOccurrences,
    bool? IncludePastDates);

/// <summary>
/// The new values of a movement (omitted: unchanged) and why. ToAccountId: the destination of a transfer (AccountId its
/// origin). A sale or customer payment only takes AccountId. Reference "" clears it.
/// </summary>
public sealed record EditTreasuryMovementRequest(
    Guid? AccountId, Guid? ToAccountId, decimal? Amount, DateOnly? Date, string? Concept, string? Reference, string? Reason);
