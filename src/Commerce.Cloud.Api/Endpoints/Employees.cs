using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Payroll;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// The staff (PRD 9.19) and their internal payroll, administration only (<see cref="Permission.ManageUsers"/>; the actor is
/// always the caller, every write audited):
/// <list type="bullet">
/// <item><c>/employees</c>: the staff of each branch (list, one, create, edit, deactivate), an advance handed out (money out
/// of the treasury, a debit on the employee's account), and the employee's account. The POS (Personal → Empleados) calls
/// these same endpoints with its device credential and the signed-in operator (<c>AllowDeviceOperator</c>), so both layers
/// manage the staff alike (<c>/employees/{id}/account</c>,
/// <see cref="SupplierAccountEndpoints"/>). The positions are a catalog of their own (<c>/employees/roles</c>,
/// <see cref="MasterDataEndpoints"/>).</item>
/// <item><c>/payroll/runs</c>: the payroll of the selected branch: prepare a Draft for a period, edit its payslips, take an
/// employee out, discard it, or pay it (<see cref="PostgresPayrollStore"/>).</item>
/// </list>
/// </summary>
public static class EmployeeEndpoints
{
    private const decimal MaxAmount = 999_999_999_999m;

    public static void MapEmployeeEndpoints(this IEndpointRouteBuilder app)
    {
        var employees = app.MapGroup("/employees").RequireAuthorization().AddEndpointFilter<TenantScopeEndpointFilter>();

        employees.MapGet("", async (
            Guid? branchId, bool? includeInactive, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            return Results.Ok(await store.ListAsync(caller!.Scope, branchId, includeInactive ?? false, ct));
        }).AllowDeviceOperator();

        employees.MapGet("/{id:guid}", async (
            Guid id, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            return await store.GetAsync(caller!.Scope, id, ct) is { } employee ? Results.Ok(employee) : Results.NotFound();
        }).AllowDeviceOperator();

        employees.MapPost("", async (
            EmployeeRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            if (Validate(request, out var input) is { } invalid) return invalid;

            var (outcome, employee) = await store.CreateAsync(caller!.Scope, caller.Id, input!, request.TakesGoods ?? false, ct);
            return outcome == EmployeeWriteOutcome.Done ? Results.Created($"/employees/{employee!.Id}", employee) : Refusal(outcome);
        }).AllowDeviceOperator();

        employees.MapPut("/{id:guid}", async (
            Guid id, EmployeeRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            if (Validate(request, out var input) is { } invalid) return invalid;

            var (outcome, employee) = await store.UpdateAsync(caller!.Scope, caller.Id, id, input!, request.TakesGoods ?? false, ct);
            return outcome == EmployeeWriteOutcome.Done ? Results.Ok(employee) : Refusal(outcome);
        }).AllowDeviceOperator();

        employees.MapPost("/{id:guid}/active", async (
            Guid id, SetEmployeeActiveRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            var outcome = await store.SetActiveAsync(caller!.Scope, caller.Id, id, request.IsActive,
                request.IsActive ? null : request.TerminationDate ?? httpContext.Today(), ct);
            return outcome == EmployeeWriteOutcome.Done ? Results.NoContent() : Refusal(outcome);
        }).AllowDeviceOperator();

        employees.MapPost("/{id:guid}/advances", async (
            Guid id, EmployeeAdvanceRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;

            var errors = new Dictionary<string, string[]>();
            if (!PayslipRules.IsValidAmount(request.Amount)) errors["amount"] = ["amount is positive with at most two decimals."];
            if (request.Date is { } date && date > httpContext.Today()) errors["date"] = ["date cannot be in the future."];
            if (request.Concept is { Length: > PayslipRules.MaxConceptLength }) errors["concept"] = ["At most 200 characters."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var (outcome, advanceId) = await store.GiveAdvanceAsync(
                caller!.Scope, caller.Id, id, request.Amount, request.Date ?? httpContext.Today(), request.AccountId, request.Concept, ct);
            return outcome == EmployeeWriteOutcome.Done ? Results.Ok(new { advanceId }) : Refusal(outcome);
        }).AllowDeviceOperator();

        var payroll = app.MapGroup("/payroll/runs").RequireAuthorization().AddEndpointFilter<TenantScopeEndpointFilter>();

        payroll.MapGet("", async (HttpContext httpContext, PostgresUserAccountStore userStore, PostgresPayrollStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await AuthorizeBranchAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;
            return Results.Ok(await store.ListRunsAsync(caller!.Scope, ct));
        });

        payroll.MapGet("/{id:guid}", async (
            Guid id, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresPayrollStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await AuthorizeBranchAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;
            return await store.GetRunAsync(caller!.Scope, id, ct) is { } run ? Results.Ok(run) : Results.NotFound();
        });

        payroll.MapPost("", async (
            CreatePayrollRunRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresPayrollStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await AuthorizeBranchAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;

            var errors = new Dictionary<string, string[]>();
            if (request.PeriodFrom == default || request.PeriodTo == default) errors["period"] = ["periodFrom and periodTo are required."];
            else if (request.PeriodTo < request.PeriodFrom) errors["periodTo"] = ["periodTo cannot be before periodFrom."];
            if (request.PayFrequency is { } frequency && !PayFrequency.IsValid(frequency)) errors["payFrequency"] = ["Monthly, Biweekly or Weekly."];
            if (request.Notes is { Length: > 500 }) errors["notes"] = ["At most 500 characters."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var (outcome, runId) = await store.CreateRunAsync(
                caller!.Scope, caller.Id, request.PeriodFrom, request.PeriodTo, request.PayFrequency, Blank(request.Notes), ct);
            return outcome == PayrollWriteOutcome.Done ? Results.Created($"/payroll/runs/{runId}", new { runId }) : Refusal(outcome);
        });

        payroll.MapPut("/{id:guid}/payslips/{payslipId:guid}", async (
            Guid id, Guid payslipId, UpdatePayslipRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresPayrollStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await AuthorizeBranchAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;

            var errors = new Dictionary<string, string[]>();
            var lines = new List<PayslipLineInput>();
            foreach (var (line, index) in (request.Lines ?? []).Select((line, index) => (line, index)))
            {
                if (!Enum.TryParse<PayslipLineKind>(line.Kind, out var kind) || !char.IsLetter(line.Kind![0])) errors[$"lines[{index}].kind"] = ["Earning or Deduction."];
                else if (!Enum.TryParse<PayslipLineSource>(line.Source ?? "Manual", out var source) || !char.IsLetter((line.Source ?? "Manual")[0])) errors[$"lines[{index}].source"] = ["BaseSalary, Advances or Manual."];
                else if (!PayslipRules.IsValidConcept(line.Concept)) errors[$"lines[{index}].concept"] = ["1 to 200 characters."];
                else if (!PayslipRules.IsValidAmount(line.Amount)) errors[$"lines[{index}].amount"] = ["Positive with at most two decimals."];
                else lines.Add(new PayslipLineInput(kind, source, line.Concept!.Trim(), line.Amount));
            }

            if (lines.Count > 50) errors["lines"] = ["At most 50 lines."];
            if (!PayslipRules.IsValidDiscountPercent(request.PurchasesDiscountPercent)) errors["purchasesDiscountPercent"] = ["Between 0 and 100."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var outcome = await store.UpdatePayslipAsync(caller!.Scope, caller.Id, id, payslipId, lines, request.PurchasesDiscountPercent, ct);
            return outcome == PayrollWriteOutcome.Done ? Results.NoContent() : Refusal(outcome);
        });

        payroll.MapDelete("/{id:guid}/payslips/{payslipId:guid}", async (
            Guid id, Guid payslipId, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresPayrollStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await AuthorizeBranchAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;
            var outcome = await store.RemovePayslipAsync(caller!.Scope, id, payslipId, ct);
            return outcome == PayrollWriteOutcome.Done ? Results.NoContent() : Refusal(outcome);
        });

        payroll.MapDelete("/{id:guid}", async (
            Guid id, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresPayrollStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await AuthorizeBranchAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;
            var outcome = await store.DeleteRunAsync(caller!.Scope, caller.Id, id, ct);
            return outcome == PayrollWriteOutcome.Done ? Results.NoContent() : Refusal(outcome);
        });

        payroll.MapPost("/{id:guid}/pay", async (
            Guid id, PayPayrollRunRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresPayrollStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await AuthorizeBranchAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;
            if (request.PaidOn is { } paidOn && paidOn > httpContext.Today())
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["paidOn"] = ["paidOn cannot be in the future."] });
            }

            var outcome = await store.PayRunAsync(caller!.Scope, caller.Id, id, request.AccountId, request.PaidOn ?? httpContext.Today(), ct);
            return outcome == PayrollWriteOutcome.Done ? Results.NoContent() : Refusal(outcome);
        });
    }

    /// <summary>ManageUsers and a selected branch (the payroll is the branch's): 400 <c>branch-selection-required</c> without one.</summary>
    private static async Task<(IResult? Denied, StaffCaller? Caller)> AuthorizeBranchAsync(
        HttpContext httpContext, PostgresUserAccountStore userStore, CancellationToken ct)
    {
        var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
        if (denied is not null) return (denied, null);
        return caller!.Scope.BranchId is null
            ? (Results.BadRequest(new { error = "branch-selection-required" }), null)
            : (null, caller);
    }

    private static IResult? Validate(EmployeeRequest request, out EmployeeInput? input)
    {
        input = null;
        var errors = new Dictionary<string, string[]>();
        var first = request.FirstName?.Trim() ?? string.Empty;
        var last = request.LastName?.Trim() ?? string.Empty;
        var document = Digits(request.DocumentNumber);
        var cuil = Digits(request.Cuil);
        if (request.BranchId is null) errors["branchId"] = ["branchId is required."];
        if (first.Length is 0 or > 100) errors["firstName"] = ["1 to 100 characters."];
        if (last.Length is 0 or > 100) errors["lastName"] = ["1 to 100 characters."];
        if (request.FileNumber is <= 0) errors["fileNumber"] = ["A positive number."];
        if (document is not null && document.Length is < 6 or > 11) errors["documentNumber"] = ["6 to 11 digits."];
        if (cuil is not null && cuil.Length != 11) errors["cuil"] = ["11 digits."];
        if (!PayFrequency.IsValid(request.PayFrequency ?? PayFrequency.Monthly)) errors["payFrequency"] = ["Monthly, Biweekly or Weekly."];
        if (request.BaseSalary is < 0m or > MaxAmount || request.BaseSalary is { } salary && decimal.Round(salary, 2) != salary)
        {
            errors["baseSalary"] = ["Zero or more, with at most two decimals."];
        }

        if (request.Phone is { Length: > 40 }) errors["phone"] = ["At most 40 characters."];
        if (request.Email is { Length: > 200 }) errors["email"] = ["At most 200 characters."];
        if (request.Address is { Length: > 200 }) errors["address"] = ["At most 200 characters."];
        if (request.Notes is { Length: > 1000 }) errors["notes"] = ["At most 1000 characters."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        input = new EmployeeInput(
            request.BranchId!.Value, request.FileNumber, first, last, document, cuil, request.RoleId, Blank(request.Phone), Blank(request.Email),
            Blank(request.Address), request.HireDate, request.PayFrequency ?? PayFrequency.Monthly, request.BaseSalary ?? 0m, Blank(request.Notes));
        return null;
    }

    private static string? Digits(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Refusal(EmployeeWriteOutcome outcome) => outcome switch
    {
        EmployeeWriteOutcome.NotFound => Results.NotFound(),
        EmployeeWriteOutcome.BranchNotFound => Results.Conflict(new { error = "branch-not-found" }),
        EmployeeWriteOutcome.RoleNotFound => Results.Conflict(new { error = "role-not-found" }),
        EmployeeWriteOutcome.FileNumberInUse => Results.Conflict(new { error = "file-number-in-use" }),
        EmployeeWriteOutcome.AccountNotFound => Results.Conflict(new { error = "account-not-found" }),
        _ => Results.Conflict(new { error = "account-inactive" }),
    };

    private static IResult Refusal(PayrollWriteOutcome outcome) => outcome switch
    {
        PayrollWriteOutcome.NotFound => Results.NotFound(),
        PayrollWriteOutcome.NotDraft => Results.Conflict(new { error = "payroll-not-draft" }),
        PayrollWriteOutcome.NoEmployees => Results.Conflict(new { error = "no-employees" }),
        PayrollWriteOutcome.AccountNotFound => Results.Conflict(new { error = "account-not-found" }),
        PayrollWriteOutcome.AccountInactive => Results.Conflict(new { error = "account-inactive" }),
        _ => Results.Conflict(new { error = "nothing-to-pay" }),
    };
}

/// <summary>An employee as the staff form sends it. FileNumber omitted on create: the next one. TakesGoods: link a customer for the goods it takes.</summary>
public sealed record EmployeeRequest(
    Guid? BranchId,
    int? FileNumber,
    string? FirstName,
    string? LastName,
    string? DocumentNumber,
    string? Cuil,
    Guid? RoleId,
    string? Phone,
    string? Email,
    string? Address,
    DateOnly? HireDate,
    string? PayFrequency,
    decimal? BaseSalary,
    string? Notes,
    bool? TakesGoods);

public sealed record SetEmployeeActiveRequest(bool IsActive, DateOnly? TerminationDate);

/// <summary>An advance handed out from a treasury account (Date: today when omitted).</summary>
public sealed record EmployeeAdvanceRequest(decimal Amount, DateOnly? Date, Guid AccountId, string? Concept);

public sealed record CreatePayrollRunRequest(DateOnly PeriodFrom, DateOnly PeriodTo, string? PayFrequency, string? Notes);

public sealed record PayslipLineRequest(string? Kind, string? Source, string? Concept, decimal Amount);

/// <summary>A Draft payslip's lines (replace-set) and its goods discount percentage.</summary>
public sealed record UpdatePayslipRequest(IReadOnlyList<PayslipLineRequest>? Lines, decimal PurchasesDiscountPercent);

public sealed record PayPayrollRunRequest(Guid AccountId, DateOnly? PaidOn);
