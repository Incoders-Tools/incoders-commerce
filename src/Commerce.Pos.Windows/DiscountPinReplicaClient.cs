using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Commerce.BranchNode;
using Commerce.Domain.Discounts;

namespace Commerce.Pos.Windows;

/// <summary>
/// Typed device-bearer client for `GET /device/branch/discount-pin`
/// (branch-discount-pin spec). The branch comes from the stored device
/// credential on the server, so a terminal only ever receives its own branch
/// verifier; the body carries the salted hash and its parameters, never the PIN.
/// Follows <see cref="CatalogPriceReplicaClient"/>: a failure carries no data, so
/// the caller leaves the cached verifier untouched and discounts keep working
/// offline against it.
/// </summary>
public sealed class DiscountPinReplicaClient
{
    private const string Path = "/device/branch/discount-pin";

    private readonly HttpClient _httpClient;

    public DiscountPinReplicaClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<DiscountPinPullOutcome> PullAsync(string deviceToken, CancellationToken ct = default)
    {
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, Path);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                PosHttp.LogFailure(endpoint, response, "discount PIN pull failed");
                return DiscountPinPullOutcome.Failed($"HTTP {(int)response.StatusCode}");
            }

            var body = await PosHttp.TryReadJsonAsync<DeviceDiscountPinDto>(response, endpoint, ct);
            return body is null
                ? DiscountPinPullOutcome.Failed(PosMessages.UnexpectedResponse)
                : DiscountPinPullOutcome.Succeeded(body);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return DiscountPinPullOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }
}

/// <summary>Mirrors `Commerce.Cloud.Api.Persistence.DeviceDiscountPinResponse`; byte fields are base64.</summary>
public sealed record DeviceDiscountPinDto(
    bool IsSet, long? Version, string? Algorithm, int? Iterations, string? Salt, string? Hash, DateTimeOffset? ChangedAtUtc);

public sealed record DiscountPinPullOutcome(bool Success, DeviceDiscountPinDto? Body, string? Error)
{
    public static DiscountPinPullOutcome Succeeded(DeviceDiscountPinDto body) => new(true, body, null);

    public static DiscountPinPullOutcome Failed(string error) => new(false, null, error);

    /// <summary>The verifier to cache for <paramref name="branchId"/>, or null when the branch has none (or the pull failed).</summary>
    public DiscountPinReplica? ToReplica(Guid branchId) =>
        Body is { IsSet: true, Version: { } version, Algorithm: { } algorithm, Iterations: { } iterations, Salt: { } salt, Hash: { } hash }
            ? new DiscountPinReplica(
                branchId, version,
                new DiscountPinVerifier(algorithm, iterations, Convert.FromBase64String(salt), Convert.FromBase64String(hash)),
                Body.ChangedAtUtc ?? DateTimeOffset.UtcNow)
            : null;
}
