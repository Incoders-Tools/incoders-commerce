using System.Net.Http;
using System.Net.Http.Headers;

namespace Commerce.Pos.Windows;

/// <summary>
/// Typed device-bearer `HttpClient` for `GET /device/stock/sync` (purchases-receptions-and-stock T5). Follows
/// <see cref="CatalogPriceReplicaClient"/>'s discriminated-outcome shape. The branch comes from the stored device
/// credential on the server, so this client sends only the cursor and the bearer token.
/// </summary>
public sealed class StockReplicaClient
{
    private const string Path = "/device/stock/sync";

    private readonly HttpClient _httpClient;

    public StockReplicaClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<StockSyncOutcome> PullAsync(DateTimeOffset since, string deviceToken, CancellationToken ct = default)
    {
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, Path);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{Path}?since={Uri.EscapeDataString(since.ToString("O"))}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                PosHttp.LogFailure(endpoint, response, "stock pull failed");
                return StockSyncOutcome.Failed($"HTTP {(int)response.StatusCode}");
            }

            var body = await PosHttp.TryReadJsonAsync<StockSyncResponseDto>(response, endpoint, ct);
            return body is null
                ? StockSyncOutcome.Failed(PosMessages.UnexpectedResponse)
                : StockSyncOutcome.Succeeded(body.Items, body.ServerTimeUtc);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return StockSyncOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }
}

/// <summary>Mirrors `Commerce.Cloud.Api.Persistence.StockReplicaRow`.</summary>
public sealed record StockReplicaRowDto(Guid PresentationId, decimal OnHand);

/// <summary>Mirrors `Commerce.Cloud.Api.Endpoints.StockSyncResponse`.</summary>
public sealed record StockSyncResponseDto(IReadOnlyList<StockReplicaRowDto> Items, DateTimeOffset ServerTimeUtc);

/// <summary>A failure carries no data: the caller leaves the replica and cursor byte-identical.</summary>
public sealed record StockSyncOutcome(
    bool Success, IReadOnlyList<StockReplicaRowDto>? Items, DateTimeOffset? ServerTimeUtc, string? Error)
{
    public static StockSyncOutcome Succeeded(IReadOnlyList<StockReplicaRowDto> items, DateTimeOffset serverTimeUtc) =>
        new(true, items, serverTimeUtc, null);

    public static StockSyncOutcome Failed(string error) => new(false, null, null, error);
}
