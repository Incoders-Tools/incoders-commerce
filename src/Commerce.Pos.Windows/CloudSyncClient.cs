using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Commerce.Domain.Sync;

namespace Commerce.Pos.Windows;

/// <summary>
/// HttpClient-based sync client from Pos.Windows to Cloud.Api's `/sync`
/// endpoint (design.md "Data Flow" — SYNC). Authenticates with the
/// server-issued device credential; the identity claims are read
/// server-side, from `DeviceBearerAuthenticationHandler`'s stored row, never
/// from anything sent here. No IPC, no separate service — this runs
/// in-process inside the WPF app.
///
/// Sync-blocked-not-sales-blocked (design.md "Why revocation cannot block a
/// sale"): this class is referenced ONLY from `MainWindow.SyncButton_Click`.
/// `CommitSaleButton_Click` never reads `DeviceToken` and never calls this
/// class — a revoked/invalid credential can only ever fail a sync, never a
/// local sale.
/// </summary>
public sealed class CloudSyncClient
{
    private readonly HttpClient _httpClient;

    public CloudSyncClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SyncPushResult> PushAsync(SyncEnvelope envelope, string deviceToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/sync/inbox")
            {
                Content = JsonContent.Create(envelope)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deviceToken);

            using var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return SyncPushResult.CredentialRejected(
                    $"HTTP {(int)response.StatusCode}: device credential rejected. Re-pair this terminal.");
            }

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

public sealed record SyncPushResult(bool Success, bool CredentialWasRejected, InboundApplyResult? Result, string? Error)
{
    public static SyncPushResult Succeeded(InboundApplyResult? result) => new(true, false, result, null);

    public static SyncPushResult Failed(string error) => new(false, false, null, error);

    /// <summary>
    /// Distinguishes "credential rejected" (401/403 — re-pair required) from
    /// a generic network failure (design.md "Re-pairing"), so the UI can show
    /// a targeted hint instead of a generic sync-failure message.
    /// </summary>
    public static SyncPushResult CredentialRejected(string error) => new(false, true, null, error);
}
