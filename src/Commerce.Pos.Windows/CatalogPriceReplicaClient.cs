using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// Typed device-bearer `HttpClient` for `GET /device/catalog/sync`
/// (commerce-pricing-engine design.md "BranchNode replication: one channel,
/// not two"). Follows <see cref="CustomerReplicaClient"/>'s exact
/// discriminated-outcome shape. Org scope comes from the stored device
/// credential row on the server side — this client sends only the cursor and
/// the bearer token, never anything the server should not already know.
/// </summary>
public sealed class CatalogPriceReplicaClient
{
    private const string Path = "/device/catalog/sync";

    private readonly HttpClient _httpClient;

    public CatalogPriceReplicaClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CatalogPriceSyncOutcome> PullAsync(DateTimeOffset since, string deviceToken, CancellationToken ct = default)
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
                PosHttp.LogFailure(endpoint, response, "catalog pull failed");
                return CatalogPriceSyncOutcome.Failed($"HTTP {(int)response.StatusCode}");
            }

            var body = await PosHttp.TryReadJsonAsync<CatalogSyncResponseDto>(response, endpoint, ct);
            if (body is null)
            {
                return CatalogPriceSyncOutcome.Failed(PosMessages.UnexpectedResponse);
            }

            return CatalogPriceSyncOutcome.Succeeded(body.Items, body.RemovedPresentationIds, body.ServerTimeUtc);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return CatalogPriceSyncOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }
}

/// <summary>Mirrors `Commerce.Cloud.Api.Endpoints.CatalogReplicaRow`.</summary>
public sealed record CatalogReplicaRowDto(
    Guid PresentationId, Guid ProductId, string ProductName, string PresentationName,
    string? IdentificationCode, string QuantityBehavior, Guid UnitId,
    decimal? UnitPrice, DateOnly? EffectiveFrom, DateTimeOffset UpdatedAtUtc,
    Guid? CategoryId = null, string? CategoryName = null, string? CategoryIconKey = null);

/// <summary>Mirrors `Commerce.Cloud.Api.Endpoints.CatalogSyncResponse`.</summary>
public sealed record CatalogSyncResponseDto(
    IReadOnlyList<CatalogReplicaRowDto> Items,
    IReadOnlyList<Guid> RemovedPresentationIds,
    DateTimeOffset ServerTimeUtc);

/// <summary>
/// Discriminated pull result: a failure (unreachable, non-2xx, empty body)
/// carries no data at all — the caller must leave the replica and cursor
/// byte-identical rather than guess a partial update (design.md "Process
/// integration" threat matrix row), mirroring <see cref="CustomerSyncOutcome"/>.
/// </summary>
public sealed record CatalogPriceSyncOutcome(
    bool Success,
    IReadOnlyList<CatalogReplicaRowDto>? Items,
    IReadOnlyList<Guid>? RemovedPresentationIds,
    DateTimeOffset? ServerTimeUtc,
    string? Error)
{
    public static CatalogPriceSyncOutcome Succeeded(
        IReadOnlyList<CatalogReplicaRowDto> items, IReadOnlyList<Guid> removedPresentationIds, DateTimeOffset serverTimeUtc) =>
        new(true, items, removedPresentationIds, serverTimeUtc, null);

    public static CatalogPriceSyncOutcome Failed(string error) => new(false, null, null, null, error);
}
