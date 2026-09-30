using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Commerce.Pos.Windows;

public sealed class UserAdminClient : IDisposable
{
    private readonly HttpClient _httpClient;
    public UserAdminClient(string baseUrl)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        _httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
    }

    /// <summary>Injects a caller-owned client for focused request-contract tests.</summary>
    public UserAdminClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AdminSignInOutcome> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        const string path = "/account/sign-in";
        var endpoint = PosHttp.Endpoint(HttpMethod.Post, path);
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(path, new AdminSignInRequestDto(email, password), ct);
            return await AdminSignInOutcome.FromResponseAsync(response, endpoint, ct);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return AdminSignInOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }

    public async Task<IReadOnlyList<UserAdminRecordDto>?> ListUsersAsync(CancellationToken ct = default)
    {
        const string path = "/account/users";
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, path);
        try
        {
            using var response = await _httpClient.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
                return null;
            }

            return await PosHttp.TryReadJsonAsync<List<UserAdminRecordDto>>(response, endpoint, ct);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return null;
        }
    }

    public Task<UserAdminMutationOutcome> CreateUserAsync(CreateUserAdminRequestDto request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/account/users", () => _httpClient.PostAsJsonAsync("/account/users", request, ct), ct);

    public Task<UserAdminMutationOutcome> ReplaceRolesAsync(Guid id, AssignRolesAdminRequestDto request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/account/users/{id}/roles", () => _httpClient.PutAsJsonAsync($"/account/users/{id}/roles", request, ct), ct);

    /// <summary>Deactivates (<paramref name="revoked"/> true) or reactivates a staff user: <c>PUT /account/users/{id}/status</c>.</summary>
    public Task<UserAdminMutationOutcome> SetStatusAsync(Guid id, bool revoked, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/account/users/{id}/status", () => _httpClient.PutAsJsonAsync($"/account/users/{id}/status", new UserStatusAdminRequestDto(revoked), ct), ct);

    public Task<UserAdminMutationOutcome> ResetPasswordAsync(Guid id, AdminResetPasswordRequestDto request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/account/users/{id}/reset-password", () => _httpClient.PostAsJsonAsync($"/account/users/{id}/reset-password", request, ct), ct);

    private static async Task<UserAdminMutationOutcome> SendAsync(
        HttpMethod method, string path, Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        var endpoint = PosHttp.Endpoint(method, path);
        try
        {
            using var response = await send();
            if (response.IsSuccessStatusCode)
            {
                return UserAdminMutationOutcome.Succeeded();
            }

            var body = await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    return UserAdminMutationOutcome.Failed(PosMessages.SessionExpired);
                case HttpStatusCode.Forbidden:
                    return PosHttp.ParseErrorCode(body) switch
                    {
                        "permissions-exceed-caller" => UserAdminMutationOutcome.Forbidden(PosMessages.PermissionsExceedCaller),
                        "branch-not-in-scope" => UserAdminMutationOutcome.Forbidden(PosMessages.StaffBranchNotInScope),
                        _ => UserAdminMutationOutcome.Forbidden(),
                    };
                case HttpStatusCode.NotFound:
                    return UserAdminMutationOutcome.NotFound();
                case HttpStatusCode.BadRequest:
                    return UserAdminMutationOutcome.Failed(
                        PosHttp.ParseErrorCode(body) switch
                        {
                            "branch-required" => PosMessages.BranchRequired,
                            "branch-not-in-organization" => PosMessages.BranchNotInOrganization,
                            "cannot-revoke-self" => PosMessages.CannotDeactivateSelf,
                            "not-a-staff-user" => PosMessages.NotAStaffUser,
                            _ => PosMessages.InvalidData,
                        });
                default:
                    return UserAdminMutationOutcome.Failed(PosHttp.MessageFor(response));
            }
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return UserAdminMutationOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed record UserAdminRecordDto(
    Guid UserId, string Email, IReadOnlyList<string> RoleNames, bool IsRevoked, IReadOnlyList<Guid>? BranchIds = null);
public sealed record UserStatusAdminRequestDto(bool Revoked);
public sealed record CreateUserAdminRequestDto(string Email, string Password, string[] RoleNames, Guid[] BranchIds);
public sealed record AssignRolesAdminRequestDto(string[] RoleNames);
public sealed record AdminResetPasswordRequestDto(string NewPassword);
public sealed record UserAdminMutationOutcome(UserAdminMutationKind Kind, string? ErrorMessage)
{
    public static UserAdminMutationOutcome Succeeded() => new(UserAdminMutationKind.Succeeded, null);
    public static UserAdminMutationOutcome Forbidden(string? message = null) =>
        new(UserAdminMutationKind.Forbidden, message ?? PosMessages.NoPermissionToManageStaff);
    public static UserAdminMutationOutcome NotFound() => new(UserAdminMutationKind.NotFound, PosMessages.StaffUserNotFound);
    public static UserAdminMutationOutcome Failed(string message) => new(UserAdminMutationKind.Failed, message);
}
public enum UserAdminMutationKind { Succeeded, Forbidden, NotFound, Failed }