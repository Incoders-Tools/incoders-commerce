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
    private const string PairPath = "/device/pair";

    private readonly HttpClient _httpClient;

    public DevicePairingClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<PairingOutcome> PairAsync(
        string email, string password, Guid installationId, Guid? branchId, CancellationToken ct = default)
    {
        var endpoint = PosHttp.Endpoint(HttpMethod.Post, PairPath);
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                PairPath,
                new DevicePairRequestDto(email, password, installationId, branchId),
                ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                PosHttp.LogFailure(endpoint, response, "email or password rejected");
                return PairingOutcome.InvalidCredentials();
            }

            var body = await PosHttp.TryReadJsonAsync<DevicePairResponseDto>(response, endpoint, ct);
            if (body is null)
            {
                PosHttp.LogFailure(endpoint, response, "no usable response body");
                return PairingOutcome.Failed(PosHttp.MessageFor(response));
            }

            switch (body.Status)
            {
                case "paired" when response.IsSuccessStatusCode
                    && body.OrganizationId is not null && body.BranchId is not null
                    && body.BranchName is not null && body.DeviceToken is not null:
                    return PairingOutcome.Paired(
                        body.OrganizationId.Value, body.BranchId.Value, body.BranchName, email, body.DeviceToken);
                case "branch-selection-required":
                    return PairingOutcome.BranchSelectionRequired(body.Branches ?? []);
                case "no-branches-assigned":
                    return PairingOutcome.Failed(PosMessages.NoBranchesAssigned);
                case "branch-not-in-scope":
                    return PairingOutcome.Failed(PosMessages.SelectedBranchNotInScope);
                case "operator-not-permitted":
                    return PairingOutcome.Failed(PosMessages.OperatorNotPermitted);
                default:
                    PosHttp.LogFailure(endpoint, response, $"unrecognized pairing status '{body.Status}'");
                    return PairingOutcome.Failed(PosMessages.UnexpectedResponse);
            }
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return PairingOutcome.Failed(PosMessages.ServerUnreachable);
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
        new(PairingOutcomeKind.InvalidCredentials, null, null, PosMessages.InvalidCredentials);

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
