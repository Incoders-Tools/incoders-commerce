using System.Net;
using System.Security.Cryptography;
using Commerce.Updater;

namespace Commerce.Upgrade;

public sealed class PackageDownloaderTests : IDisposable
{
    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"upd-staging-{Guid.NewGuid():N}");
    private static readonly byte[] Payload = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    public void Dispose()
    {
        if (Directory.Exists(_staging))
        {
            Directory.Delete(_staging, recursive: true);
        }
    }

    private static UpdatePackage Package(string url = "https://example.test/dl/Commerce.Pos.Windows-1.4.0-win-x64.msix", byte[]? content = null) => new()
    {
        Format = PackageFormat.Msix,
        Architecture = "x64",
        Url = url,
        Sha256 = Convert.ToHexString(SHA256.HashData(content ?? Payload)).ToLowerInvariant(),
        SizeBytes = (content ?? Payload).Length,
        PublisherId = "CN=Test"
    };

    private sealed class BytesHandler(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task Download_WritesTheFileToStaging_AndReportsProgress()
    {
        var reported = new List<double>();
        var downloader = new PackageDownloader(new HttpClient(new BytesHandler(Payload)));

        var result = await downloader.DownloadAsync(Package(), _staging, new Progress<double>(reported.Add), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(Path.Combine(_staging, "Commerce.Pos.Windows-1.4.0-win-x64.msix"), result.Path);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path!));
        Assert.Empty(Directory.GetFiles(_staging, "*.partial"));
    }

    [Fact]
    public async Task Download_ReusesAnAlreadyVerifiedStagedFile_WithoutNetwork()
    {
        var handler = new BytesHandler(Payload);
        var downloader = new PackageDownloader(new HttpClient(handler));
        await downloader.DownloadAsync(Package(), _staging, null, CancellationToken.None);

        var second = await downloader.DownloadAsync(Package(), _staging, null, CancellationToken.None);

        Assert.True(second.Ok);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Download_CleansStalePartialsAndOtherStagedFiles()
    {
        Directory.CreateDirectory(_staging);
        var stalePartial = Path.Combine(_staging, "old.msix.partial");
        var staleOther = Path.Combine(_staging, "Commerce.Pos.Windows-0.9.0-win-x64.msix");
        await File.WriteAllTextAsync(stalePartial, "x");
        await File.WriteAllTextAsync(staleOther, "x");

        var result = await new PackageDownloader(new HttpClient(new BytesHandler(Payload)))
            .DownloadAsync(Package(), _staging, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(File.Exists(stalePartial));
        Assert.False(File.Exists(staleOther));
    }

    [Fact]
    public async Task Download_ReplacesACorruptStagedFile()
    {
        Directory.CreateDirectory(_staging);
        var target = Path.Combine(_staging, "Commerce.Pos.Windows-1.4.0-win-x64.msix");
        await File.WriteAllBytesAsync(target, [9, 9, 9]);

        var result = await new PackageDownloader(new HttpClient(new BytesHandler(Payload)))
            .DownloadAsync(Package(), _staging, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task Download_HttpError_FailsAndLeavesNoPartial()
    {
        var result = await new PackageDownloader(new HttpClient(new BytesHandler([], HttpStatusCode.NotFound)))
            .DownloadAsync(Package(), _staging, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("404", result.Detail);
        Assert.Empty(Directory.GetFiles(_staging));
    }

    [Fact]
    public async Task Download_NetworkFailure_IsATypedFailure_NotAnException()
    {
        var result = await new PackageDownloader(new HttpClient(new ThrowingHandler()))
            .DownloadAsync(Package(), _staging, null, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData("http://example.test/a.msix")]
    [InlineData("https://example.test/a.exe")]
    [InlineData("https://example.test/a%20b.msix")]
    [InlineData("not a url")]
    public async Task Download_RejectsInsecureOrUnsafeUrls(string url)
    {
        var handler = new BytesHandler(Payload);

        var result = await new PackageDownloader(new HttpClient(handler))
            .DownloadAsync(Package(url), _staging, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, handler.Calls);
    }
}
