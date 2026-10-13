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

    /// <summary>employee-lifecycle E4: how many goods item discounts one payslip update may set.</summary>
    private const int MaxGoodsItemDiscounts = 1_000;

    /// <summary>owner feedback P4: how many employees one run may be prepared for by name.</summary>
    private const int MaxRunEmployees = 1_000;

    /// <summary>owner feedback P5: how many accounts one payslip may be paid from.</summary>
    private const int MaxPaymentParts = 10;

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

        // employee-lifecycle E1: the number a new employee will get, for the form to show (a preview, not a reservation).
        employees.MapGet("/next-file-number", async (
            HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            return Results.Ok(new { fileNumber = await store.NextFileNumberAsync(caller!.Scope, ct) });
        }).AllowDeviceOperator();

        // employee-lifecycle E4: goods an employee takes, registered in the web like a sale on the default list (the POS
        // sells them on account); web only, as the payroll that deducts them.
        employees.MapPost("/{id:guid}/goods/quote", async (
            Guid id, GoodsRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeGoodsStore store,
            CancellationToken ct) =>
        {
            // The catalog and its prices are the selected branch's.
            var (denied, caller) = await AuthorizeBranchAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;
            if (ValidateGoods(request, httpContext.Today(), out var lines) is { } invalid) return invalid;
            var (outcome, quote) = await store.QuoteAsync(caller!.Scope, id, lines!, ct, request.OccurredOn ?? httpContext.Today());
            return outcome == EmployeeGoodsOutcome.Done ? Results.Ok(quote) : Refusal(outcome);
        });

        employees.MapPost("/{id:guid}/goods", async (
            Guid id, GoodsRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeGoodsStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await AuthorizeBranchAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;
            if (ValidateGoods(request, httpContext.Today(), out var lines) is { } invalid) return invalid;
            var (outcome, registration) = await store.RegisterAsync(
                caller!.Scope, caller.Id, id, request.OccurredOn ?? httpContext.Today(), lines!, ct, request.GoodsId);
            return outcome == EmployeeGoodsOutcome.Done ? Results.Created($"/employees/{id}/goods", registration) : Refusal(outcome);
        });

        employees.MapGet("/{id:guid}/goods", async (
            Guid id, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeGoodsStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            return Results.Ok(await store.ListAsync(caller!.Scope, id, ct));
        });

        // employee-lifecycle E2: overtime, recorded when it happens; the payroll takes the pending ones by itself.
        employees.MapGet("/{id:guid}/overtime", async (
            Guid id, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            return Results.Ok(await store.ListOvertimeAsync(caller!.Scope, id, ct));
        }).AllowDeviceOperator();

        employees.MapPost("/{id:guid}/overtime", async (
            Guid id, OvertimeRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            var errors = new Dictionary<string, string[]>();
            if (!OvertimeRules.IsValidHours(request.Hours)) errors["hours"] = ["More than 0 and at most 24, with at most two decimals."];
            if (request.Multiplier is { } multiplier && !OvertimeRules.IsValidMultiplier(multiplier)) errors["multiplier"] = ["From 1 to 3, with at most two decimals."];
            if (request.Note is { Length: > OvertimeRules.MaxNoteLength }) errors["note"] = ["At most 200 characters."];
            if (request.WorkDate > httpContext.Today()) errors["workDate"] = ["Not after today: overtime is recorded once worked."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var (outcome, overtime) = await store.RecordOvertimeAsync(caller!.Scope, caller.Id, id,
                new NewOvertime(request.WorkDate ?? httpContext.Today(), request.Hours, request.Multiplier, Blank(request.Note)), ct);
            return outcome == OvertimeWriteOutcome.Done ? Results.Created($"/employees/{id}/overtime/{overtime!.Id}", overtime) : Refusal(outcome);
        }).AllowDeviceOperator();

        employees.MapDelete("/{id:guid}/overtime/{overtimeId:guid}", async (
            Guid id, Guid overtimeId, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresEmployeeStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            var outcome = await store.DeleteOvertimeAsync(caller!.Scope, caller.Id, id, overtimeId, ct);
            return outcome == OvertimeWriteOutcome.Done ? Results.NoContent() : Refusal(outcome);
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
            // employee-lifecycle E3: "Gestionar baja" sends the date and the reason (an older terminal, neither: today, none).
            if (request.TerminationReason is { Length: > FinalSettlementRules.MaxReasonLength })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["terminationReason"] = ["At most 500 characters."] });
            }

            if (!request.IsActive && request.TerminationDate > httpContext.Today())
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["terminationDate"] = ["Not after today."] });
            }

            var outcome = await store.SetActiveAsync(caller!.Scope, caller.Id, id, request.IsActive,
                request.IsActive ? null : request.TerminationDate ?? httpContext.Today(), request.IsActive ? null : Blank(request.TerminationReason), ct);
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

        // employee-lifecycle E3: the final settlement of an employee given the leave, a Draft run of it alone (web only, as
        // the whole payroll).
        payroll.MapPost("/final-settlement", async (
            FinalSettlementRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresPayrollStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await AuthorizeBranchAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;
            var (outcome, runId) = await store.CreateFinalSettlementAsync(caller!.Scope, caller.Id, request.EmployeeId, ct);
            return outcome == PayrollWriteOutcome.Done ? Results.Created($"/payroll/runs/{runId}", new { runId }) : Refusal(outcome);
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
            if (request.EmployeeIds is { Count: 0 }) errors["employeeIds"] = ["Choose at least one employee (or leave it out for every one)."];
            else if (request.EmployeeIds is { Count: > MaxRunEmployees }) errors["employeeIds"] = [$"At most {MaxRunEmployees} employees."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var (outcome, runId) = await store.CreateRunAsync(
                caller!.Scope, caller.Id, request.PeriodFrom, request.PeriodTo, request.PayFrequency, Blank(request.Notes), request.EmployeeIds, ct);
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
                else if (!Enum.TryParse<PayslipLineSource>(line.Source ?? "Manual", out var source) || !char.IsLetter((line.Source ?? "Manual")[0])) errors[$"lines[{index}].source"] = ["BaseSalary, Advances, Manual or Overtime."];
                else if (!PayslipRules.IsValidConcept(line.Concept)) errors[$"lines[{index}].concept"] = ["1 to 200 characters."];
                else if (!PayslipRules.IsValidAmount(line.Amount)) errors[$"lines[{index}].amount"] = ["Positive with at most two decimals."];
                else lines.Add(new PayslipLineInput(kind, source, line.Concept!.Trim(), line.Amount));
            }

            if (lines.Count > 50) errors["lines"] = ["At most 50 lines."];
            if (!PayslipRules.IsValidDiscountPercent(request.PurchasesDiscountPercent)) errors["purchasesDiscountPercent"] = ["Between 0 and 100."];
            var items = request.GoodsItems ?? [];
            if (items.Count > MaxGoodsItemDiscounts) errors["goodsItems"] = [$"At most {MaxGoodsItemDiscounts} items."];
            else if (items.DistinctBy(item => item.ItemId).Count() != items.Count) errors["goodsItems"] = ["Each item at most once."];
            foreach (var (item, index) in items.Select((item, index) => (item, index)))
            {
                if (item.ItemId == Guid.Empty) errors[$"goodsItems[{index}].itemId"] = ["Required."];
                else if (!PayslipRules.IsValidDiscountPercent(item.DiscountPercent)) errors[$"goodsItems[{index}].discountPercent"] = ["Between 0 and 100."];
            }

            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var outcome = await store.UpdatePayslipAsync(caller!.Scope, caller.Id, id, payslipId, lines, request.PurchasesDiscountPercent,
                [.. items.Select(item => new GoodsItemDiscountInput(item.ItemId, item.DiscountPercent))], ct);
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

            // owner feedback P5: a payslip's net split among accounts ("pago mixto").
            var splits = request.Splits ?? [];
            var errors = new Dictionary<string, string[]>();
            if (splits.DistinctBy(split => split.PayslipId).Count() != splits.Count) errors["splits"] = ["Each payslip at most once."];
            foreach (var (split, index) in splits.Select((split, index) => (split, index)))
            {
                // No account would pay the employee nothing without saying so: to leave a payslip unpaid, take it out of the run.
                if ((split.Parts ?? []).Count == 0) errors[$"splits[{index}].parts"] = ["At least one account."];
                else if ((split.Parts ?? []).Count > MaxPaymentParts) errors[$"splits[{index}].parts"] = [$"At most {MaxPaymentParts} accounts."];
                else if ((split.Parts ?? []).Any(part => !PayslipRules.IsValidAmount(part.Amount)))
                {
                    errors[$"splits[{index}].parts"] = ["Each part: positive, with at most two decimals."];
                }
            }

            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var outcome = await store.PayRunAsync(caller!.Scope, caller.Id, id, request.AccountId, request.PaidOn ?? httpContext.Today(),
                request.Splits is null
                    ? null
                    : [.. splits.Select(split => new PayslipPaymentSplit(split.PayslipId,
                        [.. (split.Parts ?? []).Select(part => new PayslipPaymentPart(part.AccountId, part.Amount))]))],
                ct);
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
        if (request.OvertimeHourlyRate is { } rate && rate != 0m && !OvertimeRules.IsValidHourlyRate(rate))
        {
            errors["overtimeHourlyRate"] = ["More than zero with at most two decimals (0 clears it)."];
        }

        if (errors.Count > 0) return Results.ValidationProblem(errors);

        input = new EmployeeInput(
            request.BranchId!.Value, request.FileNumber, first, last, document, cuil, request.RoleId, Blank(request.Phone), Blank(request.Email),
            Blank(request.Address), request.HireDate, request.PayFrequency ?? PayFrequency.Monthly, request.BaseSalary ?? 0m, Blank(request.Notes),
            request.OvertimeHourlyRate);
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
        EmployeeWriteOutcome.InPayrollDraft => Results.Conflict(new { error = "employee-in-payroll-draft" }),
        _ => Results.Conflict(new { error = "account-inactive" }),
    };

    private static IResult? ValidateGoods(GoodsRequest request, DateOnly today, out IReadOnlyList<GoodsLineInput>? lines)
    {
        lines = null;
        var errors = new Dictionary<string, string[]>();
        var requested = request.Lines ?? [];
        if (requested.Count is 0 or > EmployeeGoodsRules.MaxLines) errors["lines"] = ["1 to 50 lines."];
        foreach (var (line, index) in requested.Select((line, index) => (line, index)))
        {
            if (line.PresentationId == Guid.Empty) errors[$"lines[{index}].presentationId"] = ["Required."];
            if (!EmployeeGoodsRules.IsValidQuantity(line.Quantity)) errors[$"lines[{index}].quantity"] = ["Above zero, up to three decimals."];
        }

        if (request.OccurredOn > today) errors["occurredOn"] = ["Not after today."];
        if (request.GoodsId == Guid.Empty) errors["goodsId"] = ["A new identifier per registration (omit it to let the server pick one)."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        lines = [.. requested.Select(line => new GoodsLineInput(line.PresentationId, line.Quantity))];
        return null;
    }

    private static IResult Refusal(EmployeeGoodsOutcome outcome) => outcome switch
    {
        EmployeeGoodsOutcome.NotFound => Results.NotFound(),
        EmployeeGoodsOutcome.NoGoodsAccount => Results.Conflict(new { error = "employee-no-goods-account" }),
        EmployeeGoodsOutcome.KeyInUse => Results.Conflict(new { error = "goods-key-in-use" }),
        _ => Results.Conflict(new { error = "no-effective-price" }),
    };

    private static IResult Refusal(OvertimeWriteOutcome outcome) => outcome switch
    {
        OvertimeWriteOutcome.NotFound => Results.NotFound(),
        OvertimeWriteOutcome.RateMissing => Results.Conflict(new { error = "overtime-rate-missing" }),
        _ => Results.Conflict(new { error = "overtime-settled" }),
    };

    private static IResult Refusal(PayrollWriteOutcome outcome) => outcome switch
    {
        PayrollWriteOutcome.NotFound => Results.NotFound(),
        PayrollWriteOutcome.NotDraft => Results.Conflict(new { error = "payroll-not-draft" }),
        PayrollWriteOutcome.NoEmployees => Results.Conflict(new { error = "no-employees" }),
        PayrollWriteOutcome.AccountNotFound => Results.Conflict(new { error = "account-not-found" }),
        PayrollWriteOutcome.AccountInactive => Results.Conflict(new { error = "account-inactive" }),
        PayrollWriteOutcome.EmployeeActive => Results.Conflict(new { error = "employee-active" }),
        PayrollWriteOutcome.EmployeeInDraft => Results.Conflict(new { error = "employee-in-draft" }),
        PayrollWriteOutcome.OtherBranch => Results.Conflict(new { error = "employee-other-branch" }),
        PayrollWriteOutcome.AlreadySettled => Results.Conflict(new { error = "employee-already-settled" }),
        PayrollWriteOutcome.GoodsItemNotInPayslip => Results.Conflict(new { error = "goods-item-not-in-payslip" }),
        PayrollWriteOutcome.EmployeesNotEligible => Results.Conflict(new { error = "employees-not-eligible" }),
        PayrollWriteOutcome.PaymentExceedsNet => Results.Conflict(new { error = "payment-exceeds-net" }),
        PayrollWriteOutcome.PaymentNotInRun => Results.Conflict(new { error = "payment-not-in-run" }),
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
    bool? TakesGoods,
    // employee-lifecycle E2: omitted keeps the stored rate on an update (older terminals), 0 clears it.
    decimal? OvertimeHourlyRate = null);

/// <summary>
/// employee-lifecycle E4: goods to quote or register (OccurredOn: today when omitted). GoodsId: the client's idempotency
/// key, so a retried registration is written once.
/// </summary>
public sealed record GoodsRequest(DateOnly? OccurredOn, IReadOnlyList<GoodsLineRequest>? Lines, Guid? GoodsId = null);

public sealed record GoodsLineRequest(Guid PresentationId, decimal Quantity);

/// <summary>An overtime record (WorkDate: today when omitted; Multiplier: what the day proposes when omitted, x2 a Sunday).</summary>
public sealed record OvertimeRequest(DateOnly? WorkDate, decimal Hours, decimal? Multiplier, string? Note);

/// <summary>The leave (date: today when omitted; reason: optional for older terminals) or the reinstatement (clears both).</summary>
public sealed record SetEmployeeActiveRequest(bool IsActive, DateOnly? TerminationDate, string? TerminationReason = null);

/// <summary>employee-lifecycle E3: the employee given the leave whose last salary is settled.</summary>
public sealed record FinalSettlementRequest(Guid EmployeeId);

/// <summary>An advance handed out from a treasury account (Date: today when omitted).</summary>
public sealed record EmployeeAdvanceRequest(decimal Amount, DateOnly? Date, Guid AccountId, string? Concept);

/// <summary>A new payroll run; owner feedback P4: EmployeeIds, the employees chosen (omitted: every one of the branch).</summary>
public sealed record CreatePayrollRunRequest(
    DateOnly PeriodFrom, DateOnly PeriodTo, string? PayFrequency, string? Notes, IReadOnlyList<Guid>? EmployeeIds = null);

public sealed record PayslipLineRequest(string? Kind, string? Source, string? Concept, decimal Amount);

/// <summary>
/// A Draft payslip's lines (replace-set), its goods discount percentage and, optionally, the discount of some of its goods
/// items (omitted: every item keeps its own).
/// </summary>
public sealed record UpdatePayslipRequest(
    IReadOnlyList<PayslipLineRequest>? Lines, decimal PurchasesDiscountPercent, IReadOnlyList<GoodsItemDiscountRequest>? GoodsItems = null);

public sealed record GoodsItemDiscountRequest(Guid ItemId, decimal DiscountPercent);

/// <summary>
/// Paying a run: from AccountId on PaidOn; owner feedback P5: Splits pays named payslips part by part from several
/// accounts (the others whole from AccountId).
/// </summary>
public sealed record PayPayrollRunRequest(Guid AccountId, DateOnly? PaidOn, IReadOnlyList<PayslipSplitRequest>? Splits = null);

public sealed record PayslipSplitRequest(Guid PayslipId, IReadOnlyList<PayslipPartRequest>? Parts);

public sealed record PayslipPartRequest(Guid AccountId, decimal Amount);
