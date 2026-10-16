namespace Commerce.Updater;

public sealed class UpgradeBackupVerificationException : Exception
{
    public UpgradeBackupVerificationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Backup and restore for the branch's live SQLite state (design.md:
/// "verifies a SQLite Backup API snapshot"). A backup MUST be verifiable
/// (readable and passing an integrity check) before it is trusted, and a
/// restore MUST re-verify the same way before overwriting the live database.
/// </summary>
public interface IUpgradeBackup
{
    /// <returns>The path of the verified backup file.</returns>
    /// <exception cref="UpgradeBackupVerificationException">
    /// Thrown if the backup cannot be created or fails integrity verification.
    /// </exception>
    string CreateVerifiedBackup(string sourceDbPath, string backupDirectory);

    /// <exception cref="UpgradeBackupVerificationException">
    /// Thrown if the backup is missing or fails re-verification; the target
    /// database is left untouched in that case.
    /// </exception>
    void RestoreFromBackup(string backupPath, string targetDbPath);
}
