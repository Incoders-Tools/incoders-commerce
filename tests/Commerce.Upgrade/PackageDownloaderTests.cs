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
    public async Task Download_CleansOnlyItsOwnStalePartialsAndPackages_ForeignFilesSurvive()
    {
        Directory.CreateDirectory(_staging);
        var stalePartial = Path.Combine(_staging, "Commerce.Pos.Windows-0.8.0-win-x64.msix.partial");
        var staleOther = Path.Combine(_staging, "Commerce.Pos.Windows-0.9.0-win-x64.msix");
        var foreignNote = Path.Combine(_staging, "operator-notes.txt");
        var foreignMsix = Path.Combine(_staging, "SomeOtherApp-1.0.msix");
        var foreignPartial = Path.Combine(_staging, "other.zip.partial");
        foreach (var file in new[] { stalePartial, staleOther, foreignNote, foreignMsix, foreignPartial })
        {
            await File.WriteAllTextAsync(file, "x");
        }

        var result = await new PackageDownloader(new HttpClient(new BytesHandler(Payload)))
            .DownloadAsync(Package(), _staging, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(File.Exists(stalePartial));
        Assert.False(File.Exists(staleOther));
        Assert.True(File.Exists(foreignNote));
        Assert.True(File.Exists(foreignMsix));
        Assert.True(File.Exists(foreignPartial));
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

    private sealed class StallingHandler(bool stallBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!stallBody)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_StalledConnection_IsATimedOutFailure_NotACancel_AndLeavesNoPartial(bool stallBody)
    {
        var downloader = new PackageDownloader(new HttpClient(new StallingHandler(stallBody)), stallTimeout: TimeSpan.FromMilliseconds(150));

        var result = await downloader.DownloadAsync(Package(), _staging, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(result.TimedOut);
        Assert.Empty(Directory.GetFiles(_staging));
    }

    [Fact]
    public async Task Download_HttpClientTimeout_IsATimedOutFailure()
    {
        var timeout = new TaskCanceledException("timed out", new TimeoutException());
        var downloader = new PackageDownloader(new HttpClient(new FaultHandler(timeout)));

        var result = await downloader.DownloadAsync(Package(), _staging, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task Download_OperatorCancel_StillThrows_AndLeavesNoPartial()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var downloader = new PackageDownloader(new HttpClient(new StallingHandler(stallBody: true)), stallTimeout: TimeSpan.FromMinutes(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(Package(), _staging, null, cts.Token));

        Assert.Empty(Directory.GetFiles(_staging));
    }

    private sealed class FaultHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw failure;
    }
}
