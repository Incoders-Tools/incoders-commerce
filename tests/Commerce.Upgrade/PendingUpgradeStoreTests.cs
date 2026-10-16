using Commerce.Updater;

namespace Commerce.Upgrade;

public sealed class PendingUpgradeStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pending-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static PendingUpgrade Pending() => new("1.3.0", "1.4.0", "C:\\backups\\b.db", DateTimeOffset.Parse("2026-10-01T10:00:00Z"));

    [Fact]
    public void NoFile_ResolvesToNone()
    {
        Assert.Equal(PendingUpgradeStatus.None, new PendingUpgradeStore(_path).ResolveOnStartup(new Version(1, 3, 0)).Status);
    }

    [Fact]
    public void RecordThenRead_RoundTrips()
    {
        var store = new PendingUpgradeStore(_path);
        store.Record(Pending());

        Assert.Equal(Pending(), store.Read());
    }

    [Theory]
    [InlineData("1.4.0")]
    [InlineData("1.5.0")]
    public void StartupAtOrAboveTarget_ReportsCompleted_AndClears(string running)
    {
        var store = new PendingUpgradeStore(_path);
        store.Record(Pending());

        var report = store.ResolveOnStartup(Version.Parse(running));

        Assert.Equal(PendingUpgradeStatus.Completed, report.Status);
        Assert.Contains("1.4.0", report.Message);
        Assert.Null(store.Read());
    }

    [Fact]
    public void StartupBelowTarget_ReportsNotCompleted_WithTheBackupPath_AndClears()
    {
        var store = new PendingUpgradeStore(_path);
        store.Record(Pending());

        var report = store.ResolveOnStartup(new Version(1, 3, 0));

        Assert.Equal(PendingUpgradeStatus.NotCompleted, report.Status);
        Assert.Contains("C:\\backups\\b.db", report.Message);
        Assert.Null(store.Read());
    }

    [Fact]
    public void CorruptFile_IsTreatedAsNoPendingUpgrade_NeverThrows()
    {
        File.WriteAllText(_path, "{ not json");
        var store = new PendingUpgradeStore(_path);

        Assert.Equal(PendingUpgradeStatus.None, store.ResolveOnStartup(new Version(1, 3, 0)).Status);
        Assert.False(File.Exists(_path));
    }
}
