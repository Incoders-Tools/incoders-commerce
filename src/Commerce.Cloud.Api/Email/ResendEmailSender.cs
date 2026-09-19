using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Commerce.Cloud.Api.Email;

/// <summary>
/// Real Resend HTTP implementation (commerce-password-recovery design.md
/// "Email seam"): typed <c>AddHttpClient&lt;ResendEmailSender&gt;</c> client,
/// `POST /emails`, bearer auth. Non-2xx logs and returns `false` — it never
/// throws, so a delivery failure can never change the caller's HTTP response
/// shape (design.md "Send failure": leaking "delivery failed" would confirm
/// the address exists).
/// </summary>
public sealed class ResendEmailSender : IEmailSender
{
    private readonly HttpClient _httpClient;
    private readonly EmailOptions _options;
    private readonly ILogger<ResendEmailSender>? _logger;

    public ResendEmailSender(HttpClient httpClient, EmailOptions options, ILogger<ResendEmailSender>? logger = null)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/emails")
        {
            Content = JsonContent.Create(new
            {
                from = _options.FromAddress,
                to = new[] { message.To },
                subject = message.Subject,
                html = message.HtmlBody,
                text = message.TextBody,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ResendApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogError(ex, "Failed to reach the Resend API while sending an email.");
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogError(
                "Resend API returned a non-success status {StatusCode} while sending an email.", response.StatusCode);
            return false;
        }

        return true;
    }
}
