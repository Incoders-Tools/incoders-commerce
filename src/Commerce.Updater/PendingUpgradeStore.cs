using System.Text.Json;

namespace Commerce.Updater;

public sealed record PendingUpgrade(string FromVersion, string TargetVersion, string? BackupPath, DateTimeOffset StartedAtUtc);

public enum PendingUpgradeStatus
{
    None,
    Completed,
    NotCompleted
}

/// <param name="Message">Operator-facing Spanish text.</param>
public sealed record PendingUpgradeReport(PendingUpgradeStatus Status, string Message);

/// <summary>
/// Remembers an install that was handed to the OS so the next start can report
/// its result (the package update terminates the running process, so the
/// wizard itself never observes the outcome). This is separate from
/// <see cref="UpgradeCrashJournal"/>, whose phases describe database-migration
/// recovery, not a package swap.
/// </summary>
public sealed class PendingUpgradeStore(string path)
{
    public void Record(PendingUpgrade pending)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(pending));
    }

    public PendingUpgrade? Read()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PendingUpgrade>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale marker is harmless; it is re-evaluated on the next start.
        }
    }

    /// <summary>Compares the running version with the recorded target, then clears the marker.</summary>
    public PendingUpgradeReport ResolveOnStartup(Version running)
    {
        var pending = Read();
        Clear();
        if (pending is null)
        {
            return new PendingUpgradeReport(PendingUpgradeStatus.None, string.Empty);
        }

        if (Version.TryParse(pending.TargetVersion, out var target) && running >= target)
        {
            return new PendingUpgradeReport(PendingUpgradeStatus.Completed,
                $"Actualización a la versión {pending.TargetVersion} completada.");
        }

        var backup = string.IsNullOrWhiteSpace(pending.BackupPath) ? string.Empty : $" Copia de seguridad: {pending.BackupPath}";
        return new PendingUpgradeReport(PendingUpgradeStatus.NotCompleted,
            $"La actualización a {pending.TargetVersion} no se completó; la terminal sigue en {running}.{backup}");
    }
}
