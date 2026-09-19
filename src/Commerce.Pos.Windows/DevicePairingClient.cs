using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// Typed anonymous `HttpClient` for `POST /device/pair` (design.md "File
/// Changes"). Separate from <see cref="CloudSyncClient"/> — pairing is
/// anonymous, sync is bearer-authenticated.
/// </summary>
public sealed class DevicePairingClient
{
    private readonly HttpClient _httpClient;

    public DevicePairingClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<PairingOutcome> PairAsync(
        string email, string password, Guid installationId, Guid? branchId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "/device/pair",
                new DevicePairRequestDto(email, password, installationId, branchId),
                ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                return PairingOutcome.InvalidCredentials();
            }

            var body = await response.Content.ReadFromJsonAsync<DevicePairResponseDto>(ct);
            if (body is null)
            {
                return PairingOutcome.Failed("Empty response from server.");
            }

            return body.Status switch
            {
                "paired" => PairingOutcome.Paired(
                    body.OrganizationId!.Value, body.BranchId!.Value, body.BranchName!, email, body.DeviceToken!),
                "branch-selection-required" => PairingOutcome.BranchSelectionRequired(body.Branches ?? []),
                "no-branches-assigned" => PairingOutcome.Failed("This operator has no branches assigned."),
                "branch-not-in-scope" => PairingOutcome.Failed("The selected branch is not assigned to this operator."),
                _ => PairingOutcome.Failed($"Unrecognized pairing status: {body.Status}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return PairingOutcome.Failed($"Unreachable: {ex.Message}");
        }
    }
}

public sealed record DevicePairRequestDto(string Email, string Password, Guid InstallationId, Guid? BranchId);

public sealed record DeviceBranchOptionDto(Guid Id, string Name);

public sealed record DevicePairResponseDto(
    string Status,
    IReadOnlyList<DeviceBranchOptionDto>? Branches,
    Guid? OrganizationId,
    Guid? BranchId,
    string? BranchName,
    Guid? InstallationId,
    string? DeviceToken);

/// <summary>
/// Discriminated pairing result (design.md "Interfaces / Contracts") — the
/// UI branches on <see cref="Kind"/> rather than parsing HTTP status codes
/// itself.
/// </summary>
public sealed record PairingOutcome(
    PairingOutcomeKind Kind,
    DevicePairing? Pairing,
    IReadOnlyList<DeviceBranchOptionDto>? Branches,
    string? ErrorMessage)
{
    public static PairingOutcome Paired(Guid organizationId, Guid branchId, string branchName, string operatorEmail, string deviceToken) =>
        new(PairingOutcomeKind.Paired,
            new DevicePairing(organizationId, branchId, branchName, operatorEmail, deviceToken),
            null, null);

    public static PairingOutcome BranchSelectionRequired(IReadOnlyList<DeviceBranchOptionDto> branches) =>
        new(PairingOutcomeKind.BranchSelectionRequired, null, branches, null);

    public static PairingOutcome InvalidCredentials() =>
        new(PairingOutcomeKind.InvalidCredentials, null, null, "Invalid email or password.");

    public static PairingOutcome Failed(string message) =>
        new(PairingOutcomeKind.Failed, null, null, message);
}

public enum PairingOutcomeKind
{
    Paired,
    BranchSelectionRequired,
    InvalidCredentials,
    Failed,
}
