using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// Shared response handling for every POS HTTP client: read the status first,
/// parse JSON only when a JSON body is actually there, log the technical
/// detail (never credentials), and keep transport failures typed. Clients turn
/// the result into their own outcomes and pick their messages from
/// <see cref="PosMessages"/>.
/// </summary>
internal static class PosHttp
{
    private const string Category = "Http";
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A 401 carrying a `WWW-Authenticate` challenge comes from the device-bearer
    /// authentication itself (unknown or revoked device credential: the terminal
    /// must be paired again). A 401 without it is the endpoint rejecting the
    /// email/password. The server issues that challenge in
    /// <c>DeviceBearerAuthenticationHandler</c>; an older server that does not
    /// simply looks like invalid credentials.
    /// </summary>
    public static bool IsTerminalNotRecognized(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Count > 0;

    public static bool IsServerError(HttpResponseMessage response) => (int)response.StatusCode >= 500;

    /// <summary>
    /// Timeouts and unreachable hosts. A caller-requested cancellation is not a
    /// transport failure and keeps propagating.
    /// </summary>
    public static bool IsTransportFailure(Exception exception, CancellationToken ct) =>
        exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested;

    /// <summary>
    /// Reads the JSON body, or null when there is no body, it is not JSON, or it
    /// does not match <typeparamref name="T"/>. Never throws for a malformed body.
    /// </summary>
    public static async Task<T?> TryReadJsonAsync<T>(HttpResponseMessage response, string endpoint, CancellationToken ct)
        where T : class
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !(mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase)))
        {
            if (response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 0)
            {
                PosLog.Warning(Category, $"{endpoint} -> {(int)response.StatusCode}: body is not JSON (content type '{mediaType ?? "none"}').");
            }

            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            PosLog.Warning(Category, $"{endpoint} -> {(int)response.StatusCode}: body is not the expected JSON.", ex);
            return null;
        }
    }

    public static void LogFailure(string endpoint, HttpResponseMessage response, string detail) =>
        PosLog.Error(Category, $"{endpoint} -> {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");

    /// <summary>
    /// Logs a non-success answer together with the start of its body (server error
    /// pages and typed `{ "error": ... }` codes are what support needs to see) and
    /// returns the body text, so the caller can interpret it without reading the
    /// response a second time. The logger redacts credential-shaped text regardless.
    /// </summary>
    public static async Task<string> LogFailureWithBodyAsync(string endpoint, HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        string snippet;
        try
        {
            text = await response.Content.ReadAsStringAsync(ct);
            snippet = text.Length > 500 ? text[..500] + "..." : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            text = string.Empty;
            snippet = "(body unreadable)";
        }

        LogFailure(endpoint, response, $"body: {snippet}");
        return text;
    }

    /// <summary>The typed `{ "error": "code" }` inside an already-read body, or null.</summary>
    public static string? ParseErrorCode(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ErrorBodyDto>(body, WebOptions)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void LogTransportFailure(string endpoint, Exception exception) =>
        PosLog.Error(Category, $"{endpoint} -> transport failure ({exception.GetType().Name}).", exception);

    /// <summary>The friendly message for a status no client handles specifically.</summary>
    public static string MessageFor(HttpResponseMessage response) =>
        IsServerError(response)
            ? PosMessages.ServerError
            : response.StatusCode == HttpStatusCode.Forbidden
                ? PosMessages.AccessDenied
                : PosMessages.UnexpectedResponse;

    public static string Endpoint(HttpMethod method, string path) => $"{method.Method} {path}";

    private sealed record ErrorBodyDto(string? Error);
}
