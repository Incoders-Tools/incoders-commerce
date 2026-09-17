using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// Typed device-bearer `HttpClient` for `POST /device/operators/verify` and
/// `GET /device/operators/{userId}/status` (design.md "File Changes").
/// Follows <see cref="DevicePairingClient"/>'s discriminated-outcome shape.
/// No branching logic beyond deserializing these two responses — covered by
/// Unit 2's server-side integration tests and Unit 3's manual walkthrough.
/// </summary>
public sealed class OperatorProvisioningClient
{
    private readonly HttpClient _httpClient;

    public OperatorProvisioningClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<OperatorVerifyOutcome> VerifyAsync(
        string email, string password, string deviceToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/device/operators/verify")
            {
                Content = JsonContent.Create(new OperatorVerifyRequestDto(email, password)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadFromJsonAsync<OperatorVerifyResponseDto>(ct);
            if (body is null)
            {
                return OperatorVerifyOutcome.Failed("Empty response from server.");
            }

            return body.Status switch
            {
                "verified" => OperatorVerifyOutcome.Verified(body.UserId!.Value, body.Email!, body.OrganizationId!.Value),
                "branch-not-in-scope" => OperatorVerifyOutcome.Failed("This operator is not assigned to this terminal's branch."),
                _ => OperatorVerifyOutcome.InvalidCredentials(),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return OperatorVerifyOutcome.Failed($"Unreachable: {ex.Message}");
        }
    }

    public async Task<OperatorStatusOutcome> GetStatusAsync(Guid userId, string deviceToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/device/operators/{userId}/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadFromJsonAsync<OperatorStatusResponseDto>(ct);
            if (body is null)
            {
                return OperatorStatusOutcome.Unreachable;
            }

            return body.Status == "active" ? OperatorStatusOutcome.Active : OperatorStatusOutcome.Inactive;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return OperatorStatusOutcome.Unreachable;
        }
    }
}

public sealed record OperatorVerifyRequestDto(string Email, string Password);

public sealed record OperatorVerifyResponseDto(string Status, Guid? UserId, string? Email, Guid? OrganizationId);

public sealed record OperatorStatusResponseDto(string Status);

public sealed record OperatorVerifyOutcome(
    OperatorVerifyOutcomeKind Kind, Guid? UserId, string? Email, Guid? OrganizationId, string? ErrorMessage)
{
    public static OperatorVerifyOutcome Verified(Guid userId, string email, Guid organizationId) =>
        new(OperatorVerifyOutcomeKind.Verified, userId, email, organizationId, null);

    public static OperatorVerifyOutcome InvalidCredentials() =>
        new(OperatorVerifyOutcomeKind.InvalidCredentials, null, null, null, "Invalid email or password.");

    public static OperatorVerifyOutcome Failed(string message) =>
        new(OperatorVerifyOutcomeKind.Failed, null, null, null, message);
}

public enum OperatorVerifyOutcomeKind
{
    Verified,
    InvalidCredentials,
    Failed,
}

public enum OperatorStatusOutcome
{
    Active,
    Inactive,
    Unreachable,
}
