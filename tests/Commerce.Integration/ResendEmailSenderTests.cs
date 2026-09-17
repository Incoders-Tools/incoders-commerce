using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Commerce.Cloud.Api.Email;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-password-recovery task 3.3: <see cref="ResendEmailSender"/>
/// builds the exact `POST /emails` body and bearer header, and non-2xx
/// responses return `false` without throwing. No network is ever reached —
/// requests are captured by a stub <see cref="HttpMessageHandler"/>.
/// No standalone unit-test project exists in this repo (see
/// `SessionVersionCacheTests` remarks for the same convention), so this lives
/// in `tests/Commerce.Integration` even though it needs no live Postgres.
/// </summary>
public sealed class ResendEmailSenderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedBody { get; private set; }

        public StubHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            CapturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode);
        }
    }

    private static ResendEmailSender CreateSender(HttpStatusCode statusCode, out StubHandler handler)
    {
        handler = new StubHandler(statusCode);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com") };
        var options = new EmailOptions("resend-test-key", "no-reply@example.com", "http://localhost:5173");
        return new ResendEmailSender(httpClient, options);
    }

    [Fact]
    public async Task SendAsync_Success_PostsExpectedBodyAndBearerHeader()
    {
        var sender = CreateSender(HttpStatusCode.OK, out var handler);
        var message = new EmailMessage("someone@example.com", "Reset your Commerce password", "<p>html</p>", "text");

        var result = await sender.SendAsync(message, CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(handler.CapturedRequest);
        Assert.Equal(HttpMethod.Post, handler.CapturedRequest!.Method);
        Assert.Equal("/emails", handler.CapturedRequest.RequestUri!.AbsolutePath);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "resend-test-key"), handler.CapturedRequest.Headers.Authorization);

        using var document = JsonDocument.Parse(handler.CapturedBody!);
        var root = document.RootElement;
        Assert.Equal("no-reply@example.com", root.GetProperty("from").GetString());
        Assert.Equal("someone@example.com", root.GetProperty("to").EnumerateArray().Single().GetString());
        Assert.Equal("Reset your Commerce password", root.GetProperty("subject").GetString());
        Assert.Equal("<p>html</p>", root.GetProperty("html").GetString());
        Assert.Equal("text", root.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendAsync_NonSuccessStatus_ReturnsFalse_AndNeverThrows()
    {
        var sender = CreateSender(HttpStatusCode.BadRequest, out _);
        var message = new EmailMessage("someone@example.com", "subject", "html", "text");

        var result = await sender.SendAsync(message, CancellationToken.None);

        Assert.False(result);
    }
}
