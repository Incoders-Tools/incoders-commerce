using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// Typed device-bearer `HttpClient` for `GET /device/customers/sync`
/// (commerce-customer-identity design.md "BranchNode cloud->local customer
/// replication"). Follows <see cref="OperatorProvisioningClient"/>'s
/// discriminated-outcome shape. Org/branch scope come from the stored device
/// credential row on the server side — this client sends only the cursor and
/// the bearer token, never anything the server should not already know.
/// </summary>
public sealed class CustomerReplicaClient
{
    private readonly HttpClient _httpClient;

    public CustomerReplicaClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CustomerSyncOutcome> PullAsync(DateTimeOffset since, string deviceToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"/device/customers/sync?since={Uri.EscapeDataString(since.ToString("O"))}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return CustomerSyncOutcome.Failed($"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<CustomerSyncResponseDto>(ct);
            if (body is null)
            {
                return CustomerSyncOutcome.Failed("Empty response from server.");
            }

            return CustomerSyncOutcome.Succeeded(body.Customers, body.DisabledIds, body.ServerTimeUtc);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CustomerSyncOutcome.Failed($"Unreachable: {ex.Message}");
        }
    }
}

/// <summary>Mirrors `Commerce.Cloud.Api.Persistence.CustomerReplicaRow`.</summary>
public sealed record CustomerReplicaRowDto(
    Guid CustomerId, string DisplayName, string CustomerKind,
    string? TaxId, string? Phone, string? Locality, DateTimeOffset UpdatedAtUtc);

/// <summary>Mirrors `Commerce.Cloud.Api.Endpoints.CustomerSyncResponse`.</summary>
public sealed record CustomerSyncResponseDto(
    IReadOnlyList<CustomerReplicaRowDto> Customers,
    IReadOnlyList<Guid> DisabledIds,
    DateTimeOffset ServerTimeUtc);

/// <summary>
/// Discriminated pull result: a failure (unreachable, non-2xx, empty body)
/// carries no data at all — the caller must leave the replica and cursor
/// byte-identical rather than guess a partial update (design.md "Process
/// integration" threat matrix row).
/// </summary>
public sealed record CustomerSyncOutcome(
    bool Success,
    IReadOnlyList<CustomerReplicaRowDto>? Customers,
    IReadOnlyList<Guid>? DisabledIds,
    DateTimeOffset? ServerTimeUtc,
    string? Error)
{
    public static CustomerSyncOutcome Succeeded(
        IReadOnlyList<CustomerReplicaRowDto> customers, IReadOnlyList<Guid> disabledIds, DateTimeOffset serverTimeUtc) =>
        new(true, customers, disabledIds, serverTimeUtc, null);

    public static CustomerSyncOutcome Failed(string error) => new(false, null, null, null, error);
}
