using System.Net.Http;
using System.Net.Http.Headers;
using Commerce.BranchNode;

namespace Commerce.Pos.Windows;

/// <summary>
/// Typed device-bearer `HttpClient` for `GET /device/pricelists/sync` (customer-price-lists T4, channel `price-lists`).
/// The answer is a whole SNAPSHOT, so there is no `since`: the branch comes from the stored device credential on the
/// server and this client sends only the bearer token. Follows <see cref="StockReplicaClient"/>'s discriminated outcome.
/// </summary>
public sealed class PriceListsReplicaClient
{
    private const string Path = "/device/pricelists/sync";

    private readonly HttpClient _httpClient;

    public PriceListsReplicaClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<PriceListsSyncOutcome> PullAsync(string deviceToken, CancellationToken ct = default)
    {
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, Path);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                PosHttp.LogFailure(endpoint, response, "price lists pull failed");
                return PriceListsSyncOutcome.Failed($"HTTP {(int)response.StatusCode}");
            }

            var body = await PosHttp.TryReadJsonAsync<PriceListsSyncResponseDto>(response, endpoint, ct);
            return body is null || body.Lists is null || body.Entries is null || body.RateSets is null || body.CustomerPriceLists is null
                ? PriceListsSyncOutcome.Failed(PosMessages.UnexpectedResponse)
                : PriceListsSyncOutcome.Succeeded(body);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return PriceListsSyncOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }
}

/// <summary>Mirrors `Commerce.Cloud.Api.Endpoints.PriceListsSyncResponse`; the rows are the branch node replica records.</summary>
public sealed record PriceListsSyncResponseDto(
    IReadOnlyList<PriceListReplica> Lists,
    IReadOnlyList<PriceListEntryReplica> Entries,
    IReadOnlyList<RateSetReplica> RateSets,
    IReadOnlyList<CustomerPriceListReplica> CustomerPriceLists,
    Guid? OrganizationDefaultCustomerPriceListId,
    DateTimeOffset ServerTimeUtc);

/// <summary>A failure carries no data: the caller leaves the replica and cursor byte-identical.</summary>
public sealed record PriceListsSyncOutcome(bool Success, PriceListsSyncResponseDto? Snapshot, DateTimeOffset? ServerTimeUtc, string? Error)
{
    public static PriceListsSyncOutcome Succeeded(PriceListsSyncResponseDto snapshot) => new(true, snapshot, snapshot.ServerTimeUtc, null);

    public static PriceListsSyncOutcome Failed(string error) => new(false, null, null, error);
}
