using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// The Personal → Empleados calls (PRD 9.19, cross-layer parity with the web's <c>EmployeesScreen</c>): the SAME
/// <c>/employees</c> endpoints <c>Commerce.Web</c> uses (list, one, create, edit, deactivate / reinstate, an advance and the
/// employee's account), plus the positions catalog (<c>/employees/roles</c>) and the treasury accounts an advance is paid
/// from (<c>/treasury/accounts</c>). Runs over the shared <see cref="ManagementConnection"/>; the server re-checks the
/// operator and <c>ManageUsers</c> on EVERY call, this client adds no authorization logic of its own.
/// </summary>
public sealed class EmployeeAdminClient
{
    private readonly HttpClient _httpClient;

    /// <summary>Over the shared management client (or a caller-owned one in request-contract tests).</summary>
    public EmployeeAdminClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>The staff of <paramref name="branchId"/>, the ones dado de baja included (the list filters them); null when unreadable.</summary>
    public Task<IReadOnlyList<EmployeeRecordDto>?> ListEmployeesAsync(Guid branchId, CancellationToken ct = default) =>
        ReadListAsync<EmployeeRecordDto>("/employees", $"/employees?branchId={branchId}&includeInactive=true", ct);

    /// <summary>The positions ("puestos"), the inactive ones included so a stored one still shows; null when unreadable.</summary>
    public Task<IReadOnlyList<EmployeeRoleDto>?> ListRolesAsync(CancellationToken ct = default) =>
        ReadListAsync<EmployeeRoleDto>("/employees/roles", "/employees/roles?includeInactive=true", ct);

    /// <summary>Every treasury account of the organization; the advance form keeps the active ones of this branch or company-wide.</summary>
    public Task<IReadOnlyList<TreasuryAccountDto>?> ListTreasuryAccountsAsync(CancellationToken ct = default) =>
        ReadListAsync<TreasuryAccountDto>("/treasury/accounts", "/treasury/accounts", ct);

    /// <summary>The employee's account statement (every movement with its running balance); null when unreadable.</summary>
    public async Task<EmployeeStatementDto?> GetStatementAsync(Guid employeeId, CancellationToken ct = default)
    {
        var path = $"/employees/{employeeId}/account/statement";
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, path);
        try
        {
            using var response = await _httpClient.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
                return null;
            }

            return await PosHttp.TryReadJsonAsync<EmployeeStatementDto>(response, endpoint, ct);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return null;
        }
    }

    public async Task<EmployeeAdminMutationOutcome> CreateEmployeeAsync(EmployeeRequestDto request, CancellationToken ct = default)
    {
        var (outcome, saved) = await SendAsync(HttpMethod.Post, "/employees",
            () => _httpClient.PostAsJsonAsync("/employees", request, ct), ct, ReadEmployee(HttpMethod.Post, "/employees", ct));
        return outcome with { Employee = saved };
    }

    public async Task<EmployeeAdminMutationOutcome> UpdateEmployeeAsync(Guid id, EmployeeRequestDto request, CancellationToken ct = default)
    {
        var path = $"/employees/{id}";
        var (outcome, saved) = await SendAsync(HttpMethod.Put, path,
            () => _httpClient.PutAsJsonAsync(path, request, ct), ct, ReadEmployee(HttpMethod.Put, path, ct));
        return outcome with { Employee = saved };
    }

    /// <summary>Dar de baja (<paramref name="isActive"/> false; the server dates it today) or Reincorporar.</summary>
    public async Task<EmployeeAdminMutationOutcome> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        var path = $"/employees/{id}/active";
        return (await SendAsync<object>(HttpMethod.Post, path,
            () => _httpClient.PostAsJsonAsync(path, new SetEmployeeActiveRequestDto(isActive), ct), ct, read: null)).Outcome;
    }

    /// <summary>An advance: money out of <see cref="EmployeeAdvanceRequestDto.AccountId"/>, a debit on the employee's account.</summary>
    public async Task<EmployeeAdminMutationOutcome> GiveAdvanceAsync(Guid id, EmployeeAdvanceRequestDto request, CancellationToken ct = default)
    {
        var path = $"/employees/{id}/advances";
        return (await SendAsync<object>(HttpMethod.Post, path,
            () => _httpClient.PostAsJsonAsync(path, request, ct), ct, read: null)).Outcome;
    }

    /// <summary>
    /// The Spanish text of a refused employee write: the same wording as the web's <c>employees.errors.codes</c>; an
    /// unknown code falls back to the generic conflict text.
    /// </summary>
    public static string ConflictMessage(string? code) => code switch
    {
        "file-number-in-use" => PosMessages.EmployeeFileNumberInUse,
        "role-not-found" => PosMessages.EmployeeRoleNotFound,
        "branch-not-found" => PosMessages.EmployeeBranchNotFound,
        "account-not-found" => PosMessages.TreasuryAccountNotFound,
        "account-inactive" => PosMessages.TreasuryAccountInactive,
        _ => PosMessages.Conflict,
    };

    private Func<HttpResponseMessage, Task<EmployeeRecordDto?>> ReadEmployee(HttpMethod method, string path, CancellationToken ct) =>
        response => PosHttp.TryReadJsonAsync<EmployeeRecordDto>(response, PosHttp.Endpoint(method, path), ct);

    private async Task<IReadOnlyList<T>?> ReadListAsync<T>(string logPath, string query, CancellationToken ct)
    {
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, logPath);
        try
        {
            using var response = await _httpClient.GetAsync(query, ct);
            if (!response.IsSuccessStatusCode)
            {
                await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
                return null;
            }

            return await PosHttp.TryReadJsonAsync<List<T>>(response, endpoint, ct);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return null;
        }
    }

    private static async Task<(EmployeeAdminMutationOutcome Outcome, T? Body)> SendAsync<T>(
        HttpMethod method, string path, Func<Task<HttpResponseMessage>> send, CancellationToken ct,
        Func<HttpResponseMessage, Task<T?>>? read)
        where T : class
    {
        var endpoint = PosHttp.Endpoint(method, path);
        try
        {
            using var response = await send();
            if (response.IsSuccessStatusCode)
            {
                return (EmployeeAdminMutationOutcome.Succeeded(), read is null ? null : await read(response));
            }

            var body = await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
            return (response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => EmployeeAdminMutationOutcome.Failed(PosMessages.TerminalNotRecognized),
                HttpStatusCode.Forbidden when PosHttp.ParseErrorCode(body) == ManagementConnection.OperatorNotAuthorizedError =>
                    EmployeeAdminMutationOutcome.Forbidden(PosMessages.OperatorNotAuthorized),
                HttpStatusCode.Forbidden => EmployeeAdminMutationOutcome.Forbidden(),
                HttpStatusCode.NotFound => EmployeeAdminMutationOutcome.NotFound(),
                HttpStatusCode.BadRequest => EmployeeAdminMutationOutcome.Failed(PosMessages.InvalidData),
                HttpStatusCode.Conflict => EmployeeAdminMutationOutcome.Failed(ConflictMessage(PosHttp.ParseErrorCode(body))),
                _ => EmployeeAdminMutationOutcome.Failed(PosHttp.MessageFor(response)),
            }, null);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return (EmployeeAdminMutationOutcome.Unreachable(), null);
        }
    }
}

/// <summary>Mirrors `Commerce.Cloud.Api.Persistence.EmployeeRecord`'s wire shape (`FullName` is "Apellido, Nombre").</summary>
public sealed record EmployeeRecordDto(
    Guid Id, Guid BranchId, string? BranchName, int FileNumber, string FirstName, string LastName, string FullName,
    string? DocumentNumber, string? Cuil, Guid? RoleId, string? RoleName, string? Phone, string? Email, string? Address,
    DateOnly? HireDate, DateOnly? TerminationDate, string PayFrequency, decimal BaseSalary, Guid? CustomerId, string? Notes,
    bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, decimal Balance = 0m, decimal PurchasesOwed = 0m);

/// <summary>
/// Mirrors `Commerce.Cloud.Api.Endpoints.EmployeeRequest`. `FileNumber` null on create: the next one of the organization.
/// `TakesGoods`: link a customer so the POS sells goods to the employee on account (it cannot be unlinked).
/// </summary>
public sealed record EmployeeRequestDto(
    Guid BranchId, int? FileNumber, string FirstName, string LastName, string? DocumentNumber, string? Cuil, Guid? RoleId,
    string? Phone, string? Email, string? Address, DateOnly? HireDate, string PayFrequency, decimal BaseSalary, string? Notes,
    bool TakesGoods);

/// <summary>Mirrors `SetEmployeeActiveRequest`; the termination date is left to the server (today).</summary>
public sealed record SetEmployeeActiveRequestDto(bool IsActive);

/// <summary>Mirrors `EmployeeAdvanceRequest` (Date: today when omitted).</summary>
public sealed record EmployeeAdvanceRequestDto(decimal Amount, DateOnly? Date, Guid AccountId, string? Concept);

/// <summary>One position of `GET /employees/roles` (a master-data entry).</summary>
public sealed record EmployeeRoleDto(Guid Id, string Name, bool IsActive = true);

/// <summary>The fields of `Commerce.Cloud.Api.Persistence.TreasuryAccountRecord` the advance form uses.</summary>
public sealed record TreasuryAccountDto(Guid AccountId, Guid? BranchId, string? BranchName, string Kind, string Name, bool IsActive = true);

/// <summary>Mirrors `StatementResponse` of `/employees/{id}/account/statement`: positive balance = what the business owes the employee.</summary>
public sealed record EmployeeStatementDto(decimal OpeningBalance, IReadOnlyList<EmployeeStatementLineDto> Movements, decimal ClosingBalance);

/// <summary>Mirrors `StatementLine`: a Credit raises what the business owes the employee, a Debit lowers it.</summary>
public sealed record EmployeeStatementLineDto(
    Guid Id, string Kind, string Direction, decimal Amount, DateOnly OccurredOn, string? DocumentReference, string Concept,
    decimal RunningBalance, bool Reversed);

public sealed record EmployeeAdminMutationOutcome(EmployeeAdminMutationKind Kind, string? ErrorMessage)
{
    public static EmployeeAdminMutationOutcome Succeeded() => new(EmployeeAdminMutationKind.Succeeded, null);

    public static EmployeeAdminMutationOutcome Forbidden(string? message = null) =>
        new(EmployeeAdminMutationKind.Forbidden, message ?? PosMessages.NoPermissionToManageStaff);

    public static EmployeeAdminMutationOutcome NotFound() => new(EmployeeAdminMutationKind.NotFound, PosMessages.EmployeeNotFound);

    public static EmployeeAdminMutationOutcome Failed(string message) => new(EmployeeAdminMutationKind.Failed, message);

    public static EmployeeAdminMutationOutcome Unreachable() => new(EmployeeAdminMutationKind.Unreachable, PosMessages.ServerUnreachable);

    /// <summary>The employee as the server saved it (create / edit), when its answer could be read.</summary>
    public EmployeeRecordDto? Employee { get; init; }
}

public enum EmployeeAdminMutationKind
{
    Succeeded,
    Forbidden,
    NotFound,
    Failed,

    /// <summary>The server could not be reached (timeout, no network): there is no server answer.</summary>
    Unreachable,
}
