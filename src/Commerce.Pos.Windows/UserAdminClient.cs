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
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/account/sign-in", new AdminSignInRequestDto(email, password), ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return AdminSignInOutcome.InvalidCredentials();
            if (!response.IsSuccessStatusCode) return AdminSignInOutcome.Failed($"HTTP {(int)response.StatusCode}");
            var body = await response.Content.ReadFromJsonAsync<AdminSignedInResponseDto>(ct);
            return body is null ? AdminSignInOutcome.Failed("Empty response from server.") : AdminSignInOutcome.SignedIn(body.Permissions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return AdminSignInOutcome.Failed($"Staff management requires connectivity: {ex.Message}"); }
    }

    public async Task<IReadOnlyList<UserAdminRecordDto>?> ListUsersAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/account/users", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<UserAdminRecordDto>>(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    public Task<UserAdminMutationOutcome> CreateUserAsync(CreateUserAdminRequestDto request, CancellationToken ct = default) => SendAsync(() => _httpClient.PostAsJsonAsync("/account/users", request, ct), ct);
    public Task<UserAdminMutationOutcome> ReplaceRolesAsync(Guid id, AssignRolesAdminRequestDto request, CancellationToken ct = default) => SendAsync(() => _httpClient.PutAsJsonAsync($"/account/users/{id}/roles", request, ct), ct);
    public Task<UserAdminMutationOutcome> ResetPasswordAsync(Guid id, AdminResetPasswordRequestDto request, CancellationToken ct = default) => SendAsync(() => _httpClient.PostAsJsonAsync($"/account/users/{id}/reset-password", request, ct), ct);

    private async Task<UserAdminMutationOutcome> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var response = await send();
            if (response.StatusCode == HttpStatusCode.Forbidden) return UserAdminMutationOutcome.Forbidden();
            if (response.StatusCode == HttpStatusCode.NotFound) return UserAdminMutationOutcome.NotFound();
            return response.IsSuccessStatusCode ? UserAdminMutationOutcome.Succeeded() : UserAdminMutationOutcome.Failed($"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return UserAdminMutationOutcome.Failed($"Staff management requires connectivity: {ex.Message}"); }
    }
    public void Dispose() => _httpClient.Dispose();
}

public sealed record UserAdminRecordDto(Guid UserId, string Email, IReadOnlyList<string> RoleNames, bool IsRevoked);
public sealed record CreateUserAdminRequestDto(string Email, string Password, string[] RoleNames, Guid[] BranchIds);
public sealed record AssignRolesAdminRequestDto(string[] RoleNames);
public sealed record AdminResetPasswordRequestDto(string NewPassword);
public sealed record UserAdminMutationOutcome(UserAdminMutationKind Kind, string? ErrorMessage)
{
    public static UserAdminMutationOutcome Succeeded() => new(UserAdminMutationKind.Succeeded, null);
    public static UserAdminMutationOutcome Forbidden() => new(UserAdminMutationKind.Forbidden, "You do not have permission to manage staff.");
    public static UserAdminMutationOutcome NotFound() => new(UserAdminMutationKind.NotFound, "Staff user not found.");
    public static UserAdminMutationOutcome Failed(string message) => new(UserAdminMutationKind.Failed, message);
}
public enum UserAdminMutationKind { Succeeded, Forbidden, NotFound, Failed }