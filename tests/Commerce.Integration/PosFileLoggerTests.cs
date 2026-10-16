using System.Net;
using System.Text;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The POS keeps a technical log for support: one file per day under the data
/// directory, never containing credentials, and never able to crash the app.
/// </summary>
[Collection("PosLog")]
public sealed class PosFileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pos-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        PosLog.Configure(null);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string ReadAll(string dir) =>
        string.Concat(Directory.GetFiles(dir, "pos-*.log").OrderBy(x => x).Select(File.ReadAllText));

    [Fact]
    public void Log_WritesTimestampLevelCategoryMessageAndExceptionDetails_ToTheDailyFile()
    {
        var now = new DateTimeOffset(2026, 9, 30, 14, 5, 6, TimeSpan.Zero);
        var logger = new PosFileLogger(_dir, () => now);

        Exception thrown;
        try { throw new InvalidOperationException("boom"); } catch (Exception ex) { thrown = ex; }
        logger.Log(PosLogLevel.Error, "Cat", "Something failed", thrown);

        var file = Path.Combine(_dir, "pos-20260930.log");
        Assert.True(File.Exists(file));
        var text = File.ReadAllText(file);
        Assert.Contains("2026-09-30T14:05:06", text);
        Assert.Contains("ERROR", text);
        Assert.Contains("Cat", text);
        Assert.Contains("Something failed", text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("boom", text);
        Assert.Contains("PosFileLoggerTests", text);
    }

    [Fact]
    public void Log_RedactsSecrets_InMessageAndExceptionText()
    {
        var logger = new PosFileLogger(_dir);

        logger.Log(PosLogLevel.Error, "Cat",
            "Authorization: Bearer abc.DEF-123 and {\"password\":\"hunter2\",\"pin\":\"123456\"} deviceToken=tok999 password=hunter3",
            new Exception("failed with Bearer zzz-999 and \"newPassword\":\"hunter4\""));

        var text = ReadAll(_dir);
        foreach (var secret in new[] { "abc.DEF-123", "hunter2", "123456", "tok999", "hunter3", "zzz-999", "hunter4" })
        {
            Assert.DoesNotContain(secret, text);
        }
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public void Log_WhenTheDirectoryCannotBeWritten_DoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var logger = new PosFileLogger(blocker);

        var ex = Record.Exception(() => logger.Log(PosLogLevel.Error, "Cat", "msg", new Exception("e")));

        Assert.Null(ex);
    }

    [Fact]
    public void Log_KeepsOnlyTheLatestFourteenFiles()
    {
        Directory.CreateDirectory(_dir);
        for (var day = 1; day <= 20; day++)
        {
            File.WriteAllText(Path.Combine(_dir, $"pos-202609{day:00}.log"), "old");
        }
        File.WriteAllText(Path.Combine(_dir, "unrelated.txt"), "keep");
        var logger = new PosFileLogger(_dir, () => new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero));

        logger.Log(PosLogLevel.Information, "Cat", "today");

        var logs = Directory.GetFiles(_dir, "pos-*.log").Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(14, logs.Count);
        Assert.Contains("pos-20260930.log", logs);
        Assert.DoesNotContain("pos-20260901.log", logs);
        Assert.True(File.Exists(Path.Combine(_dir, "unrelated.txt")));
    }

    [Fact]
    public void Log_IsThreadSafe_NoLostLines()
    {
        var logger = new PosFileLogger(_dir);

        Parallel.For(0, 200, i => logger.Log(PosLogLevel.Information, "Cat", $"line-{i}"));

        var lines = ReadAll(_dir).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(200, lines.Count(l => l.Contains("line-")));
    }

    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") });
    }

    [Fact]
    public async Task AFailingDeviceCall_IsLoggedWithEndpointAndStatus_ButNeverTheCredentials()
    {
        PosLog.Configure(new PosFileLogger(_dir));
        var client = new OperatorProvisioningClient(
            new HttpClient(new FixedHandler(HttpStatusCode.InternalServerError, "<html>oops</html>")) { BaseAddress = new Uri("https://cloud.invalid") });

        await client.VerifyAsync("a@b.c", "s3cret-pw-777", "device-token-888");

        var text = ReadAll(_dir);
        Assert.Contains("/device/operators/verify", text);
        Assert.Contains("500", text);
        Assert.DoesNotContain("s3cret-pw-777", text);
        Assert.DoesNotContain("device-token-888", text);
    }
}
