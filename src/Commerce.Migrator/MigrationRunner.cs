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
    private const long LockKey = 0x436F6D6D6D696772; // "Commmigr"

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
        if (!Directory.Exists(directory))
        {
            stderr.WriteLine($"error: migrations directory not found: {directory}");
            return 2;
        }

        var files = Directory.GetFiles(directory, "*.sql")
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
            Execute(connection, $"SELECT pg_advisory_lock({LockKey})");
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
