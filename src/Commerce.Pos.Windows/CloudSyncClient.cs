using System.Net.Http;
using System.Net.Http.Json;
using Commerce.Domain.Sync;

namespace Commerce.Pos.Windows;

/// <summary>
/// HttpClient-based sync client from Pos.Windows to Cloud.Api's `/sync`
/// endpoint (design.md "Device auth"). Authenticates with the installation-
/// bound bearer token shape `Bearer {organizationId}.{installationId}` that
/// <c>DeviceBearerAuthenticationHandler</c> parses server-side. No IPC, no
/// separate service — this runs in-process inside the WPF app.
/// </summary>
public sealed class CloudSyncClient
{
    private readonly HttpClient _httpClient;

    public CloudSyncClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SyncPushResult> PushAsync(SyncEnvelope envelope, Guid organizationId, Guid installationId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/sync/inbox")
            {
                Content = JsonContent.Create(envelope)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", $"{organizationId}.{installationId}");

            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return SyncPushResult.Failed($"HTTP {(int)response.StatusCode}: {body}");
            }

            var result = await response.Content.ReadFromJsonAsync<InboundApplyResult>(ct);
            return SyncPushResult.Succeeded(result);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SyncPushResult.Failed($"Unreachable: {ex.Message}");
        }
    }
}

public sealed record SyncPushResult(bool Success, InboundApplyResult? Result, string? Error)
{
    public static SyncPushResult Succeeded(InboundApplyResult? result) => new(true, result, null);

    public static SyncPushResult Failed(string error) => new(false, null, error);
}
