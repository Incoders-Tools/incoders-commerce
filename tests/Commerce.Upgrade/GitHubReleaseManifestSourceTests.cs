using System.Net;
using System.Text;
using Commerce.Updater;

namespace Commerce.Upgrade;

public sealed class GitHubReleaseManifestSourceTests
{
    private const string Repo = "Incoders-Tools/incoders-commerce";
    private const string ReleasesUrl = "https://api.github.com/repos/Incoders-Tools/incoders-commerce/releases?per_page=30";

    private static string Release(string tag, bool prerelease, string? manifestUrl, bool draft = false)
    {
        var manifestAsset = manifestUrl is null
            ? string.Empty
            : $",{{\"name\":\"commerce-pos-release-manifest.json\",\"browser_download_url\":\"{manifestUrl}\"}}";
        var assets = "[{\"name\":\"Commerce.Pos.Windows-x.msix\",\"browser_download_url\":\"https://example.test/x.msix\"}" + manifestAsset + "]";
        return $"{{\"tag_name\":\"{tag}\",\"prerelease\":{prerelease.ToString().ToLowerInvariant()},\"draft\":{draft.ToString().ToLowerInvariant()},\"assets\":{assets}}}";
    }

    private static string Releases(params string[] releases) => "[" + string.Join(",", releases) + "]";

    private static string ManifestFor(string version) =>
        "{\"schemaVersion\":1,\"product\":\"Commerce.Pos.Windows\",\"channel\":\"stable\",\"version\":\"" + version + "\"," +
        "\"compatibility\":{\"minimumWindowsBuild\":19041,\"architectures\":[\"x64\"],\"syncContractVersion\":1,\"schemaVersion\":1}," +
        "\"packages\":[{\"format\":\"msix\",\"architecture\":\"x64\",\"minimumWindowsBuild\":19041,\"url\":\"https://example.test/x.msix\"," +
        "\"sha256\":\"abc\",\"publisherId\":\"CN=Test\",\"signatureRequired\":true,\"attestationRequired\":false}]}";

    private static GitHubReleaseManifestSource Build(string channel, FakeHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), Repo, channel, timeout ?? TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Stable_PicksNewestNonPrereleaseByVersion_NotByListOrder()
    {
        var handler = new FakeHandler()
            .On(ReleasesUrl, Releases(
                Release("v1.2.0", false, "https://example.test/m-120.json"),
                Release("v1.10.0", false, "https://example.test/m-1100.json"),
                Release("v1.11.0-internal.1", true, "https://example.test/m-pre.json")))
            .On("https://example.test/m-1100.json", ManifestFor("1.10.0"));

        var result = await Build("stable", handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ManifestFetchKind.Found, result.Kind);
        Assert.Contains("\"1.10.0\"", result.Json);
        Assert.DoesNotContain(handler.Requested, url => url.Contains("m-pre"));
    }

    [Fact]
    public async Task Internal_IncludesPrereleases_AndOrdersTheirNumericSuffix()
    {
        var handler = new FakeHandler()
            .On(ReleasesUrl, Releases(
                Release("v1.3.0-internal.2", true, "https://example.test/m-2.json"),
                Release("v1.3.0-internal.10", true, "https://example.test/m-10.json"),
                Release("v1.2.0", false, "https://example.test/m-stable.json")))
            .On("https://example.test/m-10.json", ManifestFor("1.3.0.10"));

        var result = await Build("internal", handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ManifestFetchKind.Found, result.Kind);
        Assert.Contains("1.3.0.10", result.Json);
    }

    [Fact]
    public async Task DraftsAndPrereleasesOnStable_AreIgnored_WhenNothingRemains_NotAvailable()
    {
        var handler = new FakeHandler()
            .On(ReleasesUrl, Releases(
                Release("v9.0.0", false, "https://example.test/m.json", draft: true),
                Release("v1.3.0-internal.1", true, "https://example.test/m-pre.json")));

        var result = await Build("stable", handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ManifestFetchKind.NotAvailable, result.Kind);
    }

    [Fact]
    public async Task NewestReleaseWithoutManifestAsset_IsNotAvailable_NotAnError()
    {
        var handler = new FakeHandler().On(ReleasesUrl, Releases(Release("v1.2.0", false, null)));

        var result = await Build("stable", handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ManifestFetchKind.NotAvailable, result.Kind);
    }

    [Fact]
    public async Task RateLimited403_Fails()
    {
        var handler = new FakeHandler().On(ReleasesUrl, "{\"message\":\"API rate limit exceeded\"}", HttpStatusCode.Forbidden);

        var result = await Build("stable", handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ManifestFetchKind.Failed, result.Kind);
        Assert.Contains("403", result.Detail);
    }

    [Fact]
    public async Task ManifestDownloadFailure_Fails()
    {
        var handler = new FakeHandler()
            .On(ReleasesUrl, Releases(Release("v1.2.0", false, "https://example.test/m.json")))
            .On("https://example.test/m.json", "nope", HttpStatusCode.InternalServerError);

        var result = await Build("stable", handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ManifestFetchKind.Failed, result.Kind);
    }

    [Fact]
    public async Task MalformedReleaseListJson_Fails()
    {
        var handler = new FakeHandler().On(ReleasesUrl, "<html>not json</html>");

        var result = await Build("stable", handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ManifestFetchKind.Failed, result.Kind);
    }

    [Fact]
    public async Task NetworkException_Fails_WithoutThrowing()
    {
        var handler = new FakeHandler { Throw = new HttpRequestException("no route") };

        var result = await Build("stable", handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ManifestFetchKind.Failed, result.Kind);
    }

    [Fact]
    public async Task Timeout_Fails_WithinTheConfiguredBudget()
    {
        var handler = new FakeHandler { Hang = true };

        var started = DateTime.UtcNow;
        var result = await Build("stable", handler, TimeSpan.FromMilliseconds(150)).FetchAsync(CancellationToken.None);

        Assert.Equal(ManifestFetchKind.Failed, result.Kind);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Request_SendsUserAgent_AndNoAuthorization()
    {
        var handler = new FakeHandler().On(ReleasesUrl, Releases());

        await Build("stable", handler).FetchAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.False(string.IsNullOrWhiteSpace(request.Headers.UserAgent.ToString()));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task UpdateChecker_MapsFetchOutcomesToTypedStatuses()
    {
        var env = new UpdateEnvironment(19045, "x64");
        var local = new Version(1, 0, 0);

        var available = await new UpdateChecker(new StubSource(new ManifestFetchResult(ManifestFetchKind.Found, ManifestFor("1.5.0"))), new ReleaseDiscovery())
            .CheckAsync(local, env);
        var none = await new UpdateChecker(new StubSource(new ManifestFetchResult(ManifestFetchKind.NotAvailable)), new ReleaseDiscovery())
            .CheckAsync(local, env);
        var failed = await new UpdateChecker(new StubSource(new ManifestFetchResult(ManifestFetchKind.Failed, Detail: "boom")), new ReleaseDiscovery())
            .CheckAsync(local, env);
        var thrown = await new UpdateChecker(new ThrowingSource(), new ReleaseDiscovery()).CheckAsync(local, env);

        Assert.Equal(UpdateCheckStatus.Available, available.Status);
        Assert.Equal(UpdateCheckStatus.ManifestNotConfigured, none.Status);
        Assert.Equal(UpdateCheckStatus.CheckFailedInvalid, failed.Status);
        Assert.Equal("boom", failed.Detail);
        Assert.Equal(UpdateCheckStatus.CheckFailedInvalid, thrown.Status);
    }

    [Fact]
    public async Task LocalFileSource_MissingFileIsNotAvailable_ExistingFileIsFound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"m-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(ManifestFetchKind.NotAvailable, (await new LocalFileManifestSource(path).FetchAsync(CancellationToken.None)).Kind);
            await File.WriteAllTextAsync(path, ManifestFor("1.5.0"));
            var found = await new LocalFileManifestSource(path).FetchAsync(CancellationToken.None);
            Assert.Equal(ManifestFetchKind.Found, found.Kind);
            Assert.Contains("1.5.0", found.Json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class StubSource(ManifestFetchResult result) : IUpdateManifestSource
    {
        public Task<ManifestFetchResult> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingSource : IUpdateManifestSource
    {
        public Task<ManifestFetchResult> FetchAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("bug");
    }

    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _routes = new();

        public List<string> Requested { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];
        public Exception? Throw { get; init; }
        public bool Hang { get; init; }

        public FakeHandler On(string url, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes[url] = (body, status);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Requested.Add(request.RequestUri!.ToString());
            if (Throw is not null)
            {
                throw Throw;
            }

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return _routes.TryGetValue(request.RequestUri!.ToString(), out var route)
                ? new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
