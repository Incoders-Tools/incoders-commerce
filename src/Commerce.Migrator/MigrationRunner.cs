using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Commerce.Migrator;

/// <summary>
/// The I/O half of the runner: reads the migration files, holds the advisory lock, reads the
/// ledger, and executes what <see cref="MigrationPlanner"/> planned. Never prints the
/// connection string or a substituted secret.
/// </summary>
public static class MigrationRunner
{
    // Fixed key so two deploys of the same database serialize instead of racing.
    public const long LockKey = 0x436F6D6D6D696772; // "Commmigr"

    private const int DefaultLockTimeoutSeconds = 600;
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LockNoticeInterval = TimeSpan.FromSeconds(30);

    private const string CreateLedger = """
        CREATE TABLE IF NOT EXISTS public.schema_migrations (
            file_name  text PRIMARY KEY,
            checksum   text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    public static int Run(
        string[] args,
        Func<string, string?> environment,
        TextWriter stdout,
        TextWriter stderr)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "migrations");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--migrations" && i + 1 < args.Length)
            {
                directory = args[++i];
                continue;
            }
            stderr.WriteLine($"error: unknown argument '{args[i]}'. Usage: Commerce.Migrator [--migrations <dir>]");
            return 2;
        }

        var connectionString = environment("ConnectionStrings__Migrations");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            stderr.WriteLine("error: ConnectionStrings__Migrations is not set");
            return 2;
        }
        var lockTimeout = TimeSpan.FromSeconds(DefaultLockTimeoutSeconds);
        if (environment("MIGRATOR_LOCK_TIMEOUT_SECONDS") is { Length: > 0 } rawTimeout)
        {
            if (!int.TryParse(rawTimeout, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 1)
            {
                stderr.WriteLine($"error: MIGRATOR_LOCK_TIMEOUT_SECONDS must be a whole number of seconds >= 1, got '{rawTimeout}'");
                return 2;
            }
            lockTimeout = TimeSpan.FromSeconds(seconds);
        }
        if (!Directory.Exists(directory))
        {
            stderr.WriteLine($"error: migrations directory not found: {directory}");
            return 2;
        }

        // Only lowercase .sql runs; anything else (a .SQL file, a stray note) would be silently
        // ignored on Linux, so it stops the run instead.
        var paths = Directory.GetFiles(directory);
        var foreign = paths.Select(Path.GetFileName).Where(name => !name!.EndsWith(".sql", StringComparison.Ordinal)).ToList();
        if (foreign.Count > 0)
        {
            stderr.WriteLine($"error: {string.Join(", ", foreign)} in {directory} is not a .sql file; " +
                             "migration files must end in lowercase .sql");
            return 2;
        }

        var files = paths
            .Select(path => new MigrationFile(Path.GetFileName(path), File.ReadAllText(path)))
            .ToList();

        string? current = null;
        try
        {
            // No pooling: closing the connection ends the session, which releases the lock
            // even when the run dies halfway through.
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, CommandTimeout = 0 };
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            if (!AcquireLock(connection, lockTimeout, stdout))
            {
                stderr.WriteLine(
                    $"error: timed out after {lockTimeout.TotalSeconds:F0}s waiting for the migration lock; " +
                    "another migrator run still holds it, nothing was applied " +
                    "(raise MIGRATOR_LOCK_TIMEOUT_SECONDS or end the holding session)");
                return 1;
            }
            Execute(connection, CreateLedger);

            var plan = MigrationPlanner.Plan(files, ReadLedger(connection), environment);
            if (plan.Failed)
            {
                stderr.WriteLine($"error: {plan.Error}");
                return 1;
            }

            var applied = 0;
            foreach (var step in plan.Steps)
            {
                if (step.Skip)
                {
                    stdout.WriteLine($"skipped {step.Name}");
                    continue;
                }
                current = step.Name;
                // One command per file, exactly as the integration fixture applies them, so a
                // file carrying its own BEGIN;/COMMIT; keeps its transaction.
                Execute(connection, step.Sql);
                using (var insert = new NpgsqlCommand(
                           "INSERT INTO public.schema_migrations (file_name, checksum) VALUES ($1, $2)", connection))
                {
                    insert.Parameters.AddWithValue(step.Name);
                    insert.Parameters.AddWithValue(step.Checksum);
                    insert.ExecuteNonQuery();
                }
                stdout.WriteLine($"applied {step.Name}");
                applied++;
                current = null;
            }

            Execute(connection, $"SELECT pg_advisory_unlock({LockKey})");
            stdout.WriteLine($"migrations: {applied} applied, {plan.Steps.Count - applied} skipped");
            return 0;
        }
        catch (Exception ex) when (ex is NpgsqlException or ArgumentException or InvalidOperationException)
        {
            stderr.WriteLine(current is null ? $"error: {ex.Message}" : $"error: {current} failed: {ex.Message}");
            return 1;
        }
    }

    // Polls pg_try_advisory_lock so a stuck holder cannot block a deploy forever; the
    // migration statements themselves stay unbounded (CommandTimeout = 0).
    private static bool AcquireLock(NpgsqlConnection connection, TimeSpan timeout, TextWriter stdout)
    {
        var timer = Stopwatch.StartNew();
        TimeSpan? lastNotice = null;
        while (true)
        {
            using (var cmd = new NpgsqlCommand($"SELECT pg_try_advisory_lock({LockKey})", connection))
            {
                if ((bool)cmd.ExecuteScalar()!) return true;
            }
            var elapsed = timer.Elapsed;
            if (elapsed >= timeout) return false;
            if (lastNotice is null || elapsed - lastNotice >= LockNoticeInterval)
            {
                stdout.WriteLine($"waiting for the migration lock (held by another session, {elapsed.TotalSeconds:F0}s of {timeout.TotalSeconds:F0}s)");
                lastNotice = elapsed;
            }
            var remaining = timeout - elapsed;
            Thread.Sleep(remaining < LockPollInterval ? remaining : LockPollInterval);
        }
    }

    private static Dictionary<string, string> ReadLedger(NpgsqlConnection connection)
    {
        var ledger = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = new NpgsqlCommand("SELECT file_name, checksum FROM public.schema_migrations", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ledger[reader.GetString(0)] = reader.GetString(1);
        return ledger;
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }
}
