using System.Net;
using System.Text;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The POS device clients turn the server's typed `operator-not-permitted`
/// verdict (a user without `OperatePos`, e.g. a seller) into a Failed
/// outcome carrying a Spanish, actionable message.
/// </summary>
[Collection("PosLog")]
public sealed class OperatePosClientTests
{
    private const string ExpectedMessage =
        "Este usuario no tiene permiso para operar el punto de venta. Pedí a un administrador que le asigne el rol Cajero.";

    private sealed class FixedHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static HttpClient Client(HttpStatusCode status, string json) =>
        new(new FixedHandler(status, json)) { BaseAddress = new Uri("https://cloud.invalid") };

    [Fact]
    public async Task Verify_OperatorNotPermitted_IsFailedWithSpanishMessage()
    {
        var client = new OperatorProvisioningClient(Client(
            HttpStatusCode.Forbidden, """{"status":"operator-not-permitted","userId":null,"email":null,"organizationId":null,"permissions":0}"""));

        var outcome = await client.VerifyAsync("a@b.c", "pw", "token");

        Assert.Equal(OperatorVerifyOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(ExpectedMessage, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Pair_OperatorNotPermitted_IsFailedWithSpanishMessage()
    {
        var client = new DevicePairingClient(Client(
            HttpStatusCode.Forbidden, """{"status":"operator-not-permitted"}"""));

        var outcome = await client.PairAsync("a@b.c", "pw", Guid.NewGuid(), null);

        Assert.Equal(PairingOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(ExpectedMessage, outcome.ErrorMessage);
    }
}
