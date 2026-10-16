using Microsoft.Data.Sqlite;

namespace Commerce.Updater;

/// <summary>
/// Real SQLite Backup API implementation (<see cref="SqliteConnection.BackupDatabase"/>)
/// per design.md — the same durable-state technology Unit 3's
/// <c>BranchSyncStore</c> already uses, so no live external service is
/// required to prove this out.
/// </summary>
public sealed class SqliteUpgradeBackup : IUpgradeBackup
{
    public string CreateVerifiedBackup(string sourceDbPath, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"backup-{Guid.NewGuid():N}.db");

        try
        {
            using (var source = new SqliteConnection($"Data Source={sourceDbPath}"))
            using (var destination = new SqliteConnection($"Data Source={backupPath}"))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            if (!VerifyIntegrity(backupPath))
            {
                throw new UpgradeBackupVerificationException($"Backup at '{backupPath}' failed integrity verification.");
            }
        }
        catch (Exception ex) when (ex is not UpgradeBackupVerificationException)
        {
            throw new UpgradeBackupVerificationException($"Failed to create a verified backup: {ex.Message}");
        }

        return backupPath;
    }

    public void RestoreFromBackup(string backupPath, string targetDbPath)
    {
        if (!File.Exists(backupPath) || !VerifyIntegrity(backupPath))
        {
            throw new UpgradeBackupVerificationException($"Backup at '{backupPath}' is missing or failed verification; cannot restore.");
        }

        using var source = new SqliteConnection($"Data Source={backupPath}");
        using var destination = new SqliteConnection($"Data Source={targetDbPath}");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static bool VerifyIntegrity(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = (string?)command.ExecuteScalar();
        return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
    }
}
