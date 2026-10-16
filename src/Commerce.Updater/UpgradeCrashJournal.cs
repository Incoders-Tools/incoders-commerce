namespace Commerce.Updater;

/// <summary>
/// The phase boundary an interrupted upgrade is recovered from. Anything
/// before <see cref="WritesReopened"/> is the pre-reopen boundary (restore
/// the snapshot); <see cref="WritesReopened"/> or later is the post-reopen
/// boundary (never restore — roll binaries back or forward-repair) per
/// ADR-004's load-bearing safety invariant.
/// </summary>
public enum UpgradePhase
{
    NotStarted,
    BackupVerified,
    WritesQuiesced,
    MigrationApplied,
    HealthChecked,
    WritesReopened,
    Committed
}

/// <summary>
/// A file-backed crash journal: durable across a simulated process restart
/// (a fresh <see cref="UpgradeCrashJournal"/> instance reading the same
/// path), the same technique Unit 3 used to prove atomicity across a
/// simulated restart for <c>BranchSyncStore</c>.
/// </summary>
public sealed class UpgradeCrashJournal
{
    private readonly string _journalPath;

    public UpgradeCrashJournal(string journalPath)
    {
        _journalPath = journalPath;
    }

    public void RecordPhase(UpgradePhase phase, string? backupPath = null) =>
        File.WriteAllText(_journalPath, $"{phase}|{backupPath}");

    public (UpgradePhase Phase, string? BackupPath) ReadLastPhase()
    {
        if (!File.Exists(_journalPath))
        {
            return (UpgradePhase.NotStarted, null);
        }

        var parts = File.ReadAllText(_journalPath).Split('|', 2);
        var phase = Enum.Parse<UpgradePhase>(parts[0]);
        var backupPath = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        return (phase, backupPath);
    }

    public void Clear()
    {
        if (File.Exists(_journalPath))
        {
            File.Delete(_journalPath);
        }
    }
}
