using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// pos-installation-identity "Register Number" on the terminal: the pairing and
/// identity clients carry the branch code and register number, they survive a
/// restart in `installation.json` (old files without them still load), a missing
/// identity is fetched once and persisted, offline keeps it unknown without
/// failing, and the label never shows a GUID.
/// </summary>
[Collection("PosLog")]
public sealed class TerminalIdentityTests : IDisposable
{
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), "commerce-pos-tests", Guid.NewGuid().ToString(), "installation.json");

    public TerminalIdentityTests() => Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient Client(RecordingHandler handler) => new(handler) { BaseAddress = new Uri("https://cloud.invalid") };

    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Branch = Guid.NewGuid();

    private static DevicePairing OldPairing() => new(Org, Branch, "Ruta 51", "op@example.com", "token-1");

    private static string IdentityJson(Guid branchId, int code = 1, int register = 2) =>
        JsonSerializer.Serialize(new { organizationId = Org, branchId, branchName = "Ruta 51", branchCode = code, registerNumber = register });

    [Fact]
    public async Task Pair_CarriesTheBranchCodeAndRegisterNumber()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                status = "paired", organizationId = Org, branchId = Branch, branchName = "Ruta 51",
                installationId = Guid.NewGuid(), deviceToken = "secret", branchCode = 1, registerNumber = 3,
            })));

        var outcome = await new DevicePairingClient(Client(handler)).PairAsync("op@example.com", "pw", Guid.NewGuid(), null);

        Assert.Equal(PairingOutcomeKind.Paired, outcome.Kind);
        Assert.Equal(1, outcome.Pairing!.BranchCode);
        Assert.Equal(3, outcome.Pairing.RegisterNumber);
    }

    [Fact]
    public async Task Pair_AgainstAnOlderServer_LeavesTheIdentityUnknown()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                status = "paired", organizationId = Org, branchId = Branch, branchName = "Ruta 51",
                installationId = Guid.NewGuid(), deviceToken = "secret",
            })));

        var outcome = await new DevicePairingClient(Client(handler)).PairAsync("op@example.com", "pw", Guid.NewGuid(), null);

        Assert.Equal(PairingOutcomeKind.Paired, outcome.Kind);
        Assert.Null(outcome.Pairing!.BranchCode);
        Assert.Null(outcome.Pairing.RegisterNumber);
    }

    [Fact]
    public async Task Pair_WhenTheBranchRanOutOfRegisterNumbers_IsFailedWithASpanishMessage()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Conflict, """{"status":"register-numbers-exhausted"}"""));

        var outcome = await new DevicePairingClient(Client(handler)).PairAsync("op@example.com", "pw", Guid.NewGuid(), null);

        Assert.Equal(PairingOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PosMessages.RegisterNumbersExhausted, outcome.ErrorMessage);
    }

    [Fact]
    public async Task IdentityClient_SendsTheBearer_AndParsesTheIdentity()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, IdentityJson(Branch)));

        var outcome = await new DeviceIdentityClient(Client(handler)).FetchAsync("the-token");

        Assert.True(outcome.Success);
        Assert.Equal(1, outcome.Body!.BranchCode);
        Assert.Equal(2, outcome.Body.RegisterNumber);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/device/identity", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("the-token", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task IdentityClient_ForAnUnreachableServer_FailsWithoutThrowing()
    {
        var client = new DeviceIdentityClient(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://cloud.invalid") });

        var outcome = await client.FetchAsync("the-token");

        Assert.False(outcome.Success);
        Assert.Equal(PosMessages.ServerUnreachable, outcome.Error);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    [Fact]
    public void Store_RoundTripsTheBranchCodeAndRegisterNumber()
    {
        var store = new LocalInstallationStore(_filePath);
        var first = store.LoadOrCreate();

        store.Save(new LocalInstallationRecord(first.InstallationId, OldPairing() with { BranchCode = 1, RegisterNumber = 2 }));
        var reloaded = store.LoadOrCreate();

        Assert.Equal(1, reloaded.Pairing!.BranchCode);
        Assert.Equal(2, reloaded.Pairing.RegisterNumber);
    }

    [Fact]
    public void Store_LoadsAnOldFileWithoutTheNewFields_AsUnknownIdentity()
    {
        var store = new LocalInstallationStore(_filePath);
        var first = store.LoadOrCreate();
        store.Save(new LocalInstallationRecord(first.InstallationId, OldPairing() with { BranchCode = 1, RegisterNumber = 2 }));
        // Simulate a file written before registers existed: drop the two fields.
        var json = JsonNode.Parse(File.ReadAllText(_filePath))!.AsObject();
        json.Remove("BranchCode");
        json.Remove("RegisterNumber");
        File.WriteAllText(_filePath, json.ToJsonString());

        var reloaded = store.LoadOrCreate();

        Assert.NotNull(reloaded.Pairing);
        Assert.Equal("token-1", reloaded.Pairing!.DeviceToken);
        Assert.Null(reloaded.Pairing.BranchCode);
        Assert.Null(reloaded.Pairing.RegisterNumber);
    }

    private (TerminalIdentityRefresher Refresher, RecordingHandler Handler, LocalInstallationStore Store, Guid InstallationId) Refresher(
        Func<HttpRequestMessage, HttpResponseMessage> respond, DevicePairing paired)
    {
        var store = new LocalInstallationStore(_filePath);
        var installationId = store.LoadOrCreate().InstallationId;
        store.Save(new LocalInstallationRecord(installationId, paired));
        var handler = new RecordingHandler(respond);
        return (new TerminalIdentityRefresher(new DeviceIdentityClient(Client(handler)), store), handler, store, installationId);
    }

    [Fact]
    public async Task Refresher_WhenTheIdentityIsComplete_MakesNoRequest()
    {
        var complete = OldPairing() with { BranchCode = 1, RegisterNumber = 2 };
        var (refresher, handler, _, installationId) = Refresher(_ => Json(HttpStatusCode.OK, IdentityJson(Branch)), complete);

        var result = await refresher.EnsureAsync(installationId, complete);

        Assert.Same(complete, result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Refresher_WhenTheIdentityIsMissing_FetchesItAndPersistsIt()
    {
        var (refresher, handler, store, installationId) = Refresher(_ => Json(HttpStatusCode.OK, IdentityJson(Branch, code: 4, register: 7)), OldPairing());

        var result = await refresher.EnsureAsync(installationId, OldPairing());

        Assert.Equal(4, result.BranchCode);
        Assert.Equal(7, result.RegisterNumber);
        Assert.Single(handler.Requests);
        var persisted = store.LoadOrCreate().Pairing!;
        Assert.Equal(4, persisted.BranchCode);
        Assert.Equal(7, persisted.RegisterNumber);
    }

    [Fact]
    public async Task Refresher_WhenOffline_KeepsThePairingAsItIs_AndPersistsNothing()
    {
        var store = new LocalInstallationStore(_filePath);
        var installationId = store.LoadOrCreate().InstallationId;
        var pairing = OldPairing();
        store.Save(new LocalInstallationRecord(installationId, pairing));
        var refresher = new TerminalIdentityRefresher(
            new DeviceIdentityClient(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://cloud.invalid") }), store);

        var result = await refresher.EnsureAsync(installationId, pairing);

        Assert.Same(pairing, result);
        Assert.Null(store.LoadOrCreate().Pairing!.RegisterNumber);
    }

    [Fact]
    public async Task Refresher_IgnoresAnIdentityOfAnotherBranch()
    {
        var (refresher, _, store, installationId) = Refresher(_ => Json(HttpStatusCode.OK, IdentityJson(Guid.NewGuid())), OldPairing());

        var result = await refresher.EnsureAsync(installationId, OldPairing());

        Assert.Null(result.RegisterNumber);
        Assert.Null(store.LoadOrCreate().Pairing!.RegisterNumber);
    }

    [Fact]
    public async Task Refresher_DoesNotOverwriteAPairingThatChangedWhileTheRequestWasInFlight()
    {
        var store = new LocalInstallationStore(_filePath);
        var installationId = store.LoadOrCreate().InstallationId;
        var stale = OldPairing();
        store.Save(new LocalInstallationRecord(installationId, stale));
        var repaired = stale with { DeviceToken = "token-2", BranchCode = 1, RegisterNumber = 9 };
        var handler = new RecordingHandler(_ =>
        {
            // The terminal is re-paired while the identity request is on its way.
            store.Save(new LocalInstallationRecord(installationId, repaired));
            return Json(HttpStatusCode.OK, IdentityJson(Branch, code: 1, register: 2));
        });
        var refresher = new TerminalIdentityRefresher(new DeviceIdentityClient(Client(handler)), store);

        await refresher.EnsureAsync(installationId, stale);

        var persisted = store.LoadOrCreate().Pairing!;
        Assert.Equal("token-2", persisted.DeviceToken);
        Assert.Equal(9, persisted.RegisterNumber);
    }

    [Theory]
    [InlineData(1, 2, "Sucursal 01 · Ruta 51 · Caja 2")]
    [InlineData(12, 15, "Sucursal 12 · Ruta 51 · Caja 15")]
    [InlineData(100, 3, "Sucursal 100 · Ruta 51 · Caja 3")]
    [InlineData(1, null, "Sucursal 01 · Ruta 51")]
    [InlineData(null, 2, "Ruta 51 · Caja 2")]
    [InlineData(null, null, "Ruta 51")]
    public void Label_ShowsOnlyWhatIsKnown_AndNeverAGuid(int? code, int? register, string expected)
    {
        var label = TerminalLabel.Format(OldPairing() with { BranchCode = code, RegisterNumber = register });

        Assert.Equal(expected, label);
        Assert.DoesNotContain(Branch.ToString(), label);
    }
}
