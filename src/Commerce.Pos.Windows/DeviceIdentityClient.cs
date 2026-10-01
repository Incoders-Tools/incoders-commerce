using System.Net.Http;
using System.Net.Http.Headers;

namespace Commerce.Pos.Windows;

/// <summary>
/// Typed device-bearer client for `GET /device/identity` (pos-installation-identity
/// "Register Number"). The server answers from the STORED device credential, so a
/// terminal only ever learns its own branch code and register number; a terminal
/// paired before registers existed gets its number allocated by this call.
/// Follows <see cref="DiscountPinReplicaClient"/>: a failure carries no data, so the
/// caller keeps whatever it already knows and the POS keeps working offline.
/// </summary>
public sealed class DeviceIdentityClient
{
    private const string Path = "/device/identity";

    private readonly HttpClient _httpClient;

    public DeviceIdentityClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<DeviceIdentityOutcome> FetchAsync(string deviceToken, CancellationToken ct = default)
    {
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, Path);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                PosHttp.LogFailure(endpoint, response, "terminal identity fetch failed");
                if (PosHttp.IsTerminalNotRecognized(response)) return DeviceIdentityOutcome.Failed(PosMessages.TerminalNotRecognized);
                return DeviceIdentityOutcome.Failed(response.StatusCode == System.Net.HttpStatusCode.Conflict
                    ? PosMessages.RegisterNumbersExhausted
                    : $"HTTP {(int)response.StatusCode}");
            }

            var body = await PosHttp.TryReadJsonAsync<DeviceIdentityDto>(response, endpoint, ct);
            return body is null
                ? DeviceIdentityOutcome.Failed(PosMessages.UnexpectedResponse)
                : DeviceIdentityOutcome.Succeeded(body);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return DeviceIdentityOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }
}

/// <summary>Mirrors `Commerce.Cloud.Api.Endpoints.DeviceIdentityResponse`.</summary>
public sealed record DeviceIdentityDto(Guid OrganizationId, Guid BranchId, string BranchName, int BranchCode, int RegisterNumber);

public sealed record DeviceIdentityOutcome(bool Success, DeviceIdentityDto? Body, string? Error)
{
    public static DeviceIdentityOutcome Succeeded(DeviceIdentityDto body) => new(true, body, null);

    public static DeviceIdentityOutcome Failed(string error) => new(false, null, error);
}
