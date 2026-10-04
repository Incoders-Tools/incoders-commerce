using System.Net.Http;
using System.Net.Http.Headers;

namespace Commerce.Pos.Windows;

/// <summary>
/// Typed device-bearer client for `GET /device/organization/settings` (operator-ux-adjustments T5). The organization
/// comes from the stored device credential on the server, so this client sends only the bearer token. Follows
/// <see cref="DiscountPinReplicaClient"/>: a failure (or a separator this terminal does not know) carries no data, so the
/// caller keeps the last known value and the POS keeps formatting quantities offline.
/// </summary>
public sealed class OrganizationSettingsReplicaClient
{
    private const string Path = "/device/organization/settings";

    private readonly HttpClient _httpClient;

    public OrganizationSettingsReplicaClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<OrganizationSettingsPullOutcome> PullAsync(string deviceToken, CancellationToken ct = default)
    {
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, Path);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                PosHttp.LogFailure(endpoint, response, "organization settings pull failed");
                return OrganizationSettingsPullOutcome.Failed($"HTTP {(int)response.StatusCode}");
            }

            var body = await PosHttp.TryReadJsonAsync<DeviceOrganizationSettingsDto>(response, endpoint, ct);
            return QuantityFormat.IsKnownSeparator(body?.QuantityDecimalSeparator)
                ? OrganizationSettingsPullOutcome.Succeeded(body!.QuantityDecimalSeparator!)
                : OrganizationSettingsPullOutcome.Failed(PosMessages.UnexpectedResponse);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return OrganizationSettingsPullOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }
}

/// <summary>Mirrors `Commerce.Cloud.Api.Endpoints.DeviceOrganizationSettingsResponse`.</summary>
public sealed record DeviceOrganizationSettingsDto(string? QuantityDecimalSeparator);

/// <summary>A failure carries no data: the caller leaves the stored settings as they were.</summary>
public sealed record OrganizationSettingsPullOutcome(bool Success, string? QuantityDecimalSeparator, string? Error)
{
    public static OrganizationSettingsPullOutcome Succeeded(string quantityDecimalSeparator) => new(true, quantityDecimalSeparator, null);

    public static OrganizationSettingsPullOutcome Failed(string error) => new(false, null, error);
}
