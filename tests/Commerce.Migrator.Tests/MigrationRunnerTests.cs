using System.Diagnostics;
using Commerce.Migrator;
using Npgsql;
using Xunit.Abstractions;

namespace Commerce.Migrator.Tests;

/// <summary>
/// Runs the migrator end to end against a live Postgres 16. Every test gets its own scratch
/// database, created here and dropped in <see cref="Dispose"/>, so nothing touches
/// `commerce_dev` or `commerce_test`. Without a reachable server the tests print `SKIPPED:`
/// and return, like the live-Postgres tests in Commerce.Integration.
/// </summary>
public sealed class MigrationRunnerTests : IDisposable
{
    private const string DefaultServer =
        "Host=localhost;Port=5432;Username=commerce_owner;Password=dev-only-password;Database=postgres";

    // Roles are cluster-wide, so applying 0001/0007 to a scratch database resets the shared
    // dev roles' passwords; keep the values the rest of the dev stack uses.
    private const string AppRuntimePassword = "dev-only-password";
    private const string PlatformReadonlyPassword = "dev-only-platform-readonly-password";

    private readonly ITestOutputHelper _output;
    private readonly string _server;
    private readonly string _database = "migrator_test_" + Guid.NewGuid().ToString("N");
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "commerce-migrator-tests", Guid.NewGuid().ToString("N"));
    private readonly bool _available;

    public MigrationRunnerTests(ITestOutputHelper output)
    {
        _output = output;
        _server = Environment.GetEnvironmentVariable("COMMERCE_MIGRATOR_TEST_SERVER") is { Length: > 0 } server
            ? server
            : DefaultServer;
        _available = TryPing(_server);
        if (!_available) return;

        Execute(_server, $"CREATE DATABASE \"{_database}\"");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "fixtures", "migrations"), _workDir);
    }

    private string ScratchConnectionString =>
        new NpgsqlConnectionStringBuilder(_server) { Database = _database, Pooling = false }.ConnectionString;

    [Fact]
    public void First_run_applies_every_file_and_records_it_in_the_ledger()
    {
        if (!_available) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var run = Run(_workDir);

        Assert.Equal(0, run.Exit);
        Assert.Equal(
            ["applied 0001_widgets.sql", "applied 0002_seed_widgets.sql", "applied 0003_widget_secret.sql",
             "migrations: 3 applied, 0 skipped"],
            run.StdoutLines);
        Assert.Equal("", run.Stderr);
        Assert.Equal(
            [("0001_widgets.sql", ChecksumOf("0001_widgets.sql")),
             ("0002_seed_widgets.sql", ChecksumOf("0002_seed_widgets.sql")),
             ("0003_widget_secret.sql", ChecksumOf("0003_widget_secret.sql"))],
            Ledger());
        Assert.Equal(2L, Scalar("SELECT count(*) FROM public.widgets"));
        Assert.Equal(AppRuntimePassword, Scalar("SELECT value FROM public.widget_secrets"));
        Assert.DoesNotContain(AppRuntimePassword, run.Stdout);
    }

    [Fact]
    public void Second_run_skips_every_file_and_leaves_the_ledger_unchanged()
    {
        if (!_available) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        Assert.Equal(0, Run(_workDir).Exit);
        var before = LedgerWithTimes();

        var run = Run(_workDir);

        Assert.Equal(0, run.Exit);
        Assert.Equal(
            ["skipped 0001_widgets.sql", "skipped 0002_seed_widgets.sql", "skipped 0003_widget_secret.sql",
             "migrations: 0 applied, 3 skipped"],
            run.StdoutLines);
        Assert.Equal(before, LedgerWithTimes());
    }

    [Fact]
    public void Edited_applied_file_fails_naming_it_and_executes_nothing()
    {
        if (!_available) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        Assert.Equal(0, Run(_workDir).Exit);
        var before = LedgerWithTimes();
        File.AppendAllText(Path.Combine(_workDir, "0002_seed_widgets.sql"), "-- edited\n");
        File.WriteAllText(Path.Combine(_workDir, "0004_more.sql"), "CREATE TABLE public.more (id int);\n");

        var run = Run(_workDir);

        Assert.NotEqual(0, run.Exit);
        Assert.Contains("0002_seed_widgets.sql", run.Stderr);
        Assert.Empty(run.StdoutLines);
        Assert.Equal(before, LedgerWithTimes());
        Assert.Null(Scalar("SELECT to_regclass('public.more')::text"));
    }

    [Fact]
    public void Failing_file_stops_the_run_without_a_ledger_row_and_earlier_files_keep_theirs()
    {
        if (!_available) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        File.WriteAllText(Path.Combine(_workDir, "0002_seed_widgets.sql"),
            "BEGIN;\nINSERT INTO public.widgets (id) VALUES (1);\nINSERT INTO public.no_such_table VALUES (1);\nCOMMIT;\n");

        var run = Run(_workDir);

        Assert.NotEqual(0, run.Exit);
        Assert.Equal(["applied 0001_widgets.sql"], run.StdoutLines);
        Assert.Contains("0002_seed_widgets.sql", run.Stderr);
        Assert.Equal([("0001_widgets.sql", ChecksumOf("0001_widgets.sql"))], Ledger());
        Assert.Equal(0L, Scalar("SELECT count(*) FROM public.widgets"));
    }

    [Fact]
    public void Missing_password_for_a_pending_placeholder_fails_before_anything_runs()
    {
        if (!_available) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var run = Run(_workDir, appRuntimePassword: null);

        Assert.NotEqual(0, run.Exit);
        Assert.Contains("0003_widget_secret.sql", run.Stderr);
        Assert.Contains("APP_RUNTIME_PASSWORD", run.Stderr);
        Assert.Empty(run.StdoutLines);
        Assert.Null(Scalar("SELECT to_regclass('public.widgets')::text"));
    }

    [Fact]
    public void Missing_connection_string_fails()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = MigrationRunner.Run(["--migrations", _workDir], _ => null, stdout, stderr);

        Assert.NotEqual(0, exit);
        Assert.Contains("ConnectionStrings__Migrations", stderr.ToString());
        Assert.Equal("", stdout.ToString());
    }

    [Fact]
    public void Repository_migrations_apply_twice_on_an_empty_database()
    {
        if (!_available) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var migrations = Path.Combine(RepoRoot(), "deploy", "db", "migrations");
        var count = Directory.GetFiles(migrations, "*.sql").Length;
        var timer = Stopwatch.StartNew();

        var first = Run(migrations);
        var firstElapsed = timer.Elapsed;
        var second = Run(migrations);

        _output.WriteLine($"first run {firstElapsed.TotalSeconds:F1}s, both runs {timer.Elapsed.TotalSeconds:F1}s, {count} files");
        Assert.True(first.Exit == 0, first.Stderr);
        Assert.Equal($"migrations: {count} applied, 0 skipped", first.StdoutLines[^1]);
        Assert.True(second.Exit == 0, second.Stderr);
        Assert.Equal($"migrations: 0 applied, {count} skipped", second.StdoutLines[^1]);
        Assert.All(second.StdoutLines[..^1], line => Assert.StartsWith("skipped ", line));
        Assert.Equal(count, Ledger().Count);
    }

    private sealed record RunResult(int Exit, string Stdout, string Stderr)
    {
        public string[] StdoutLines => Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private RunResult Run(string migrations, string? appRuntimePassword = AppRuntimePassword)
    {
        var env = new Dictionary<string, string?>
        {
            ["ConnectionStrings__Migrations"] = ScratchConnectionString,
            ["APP_RUNTIME_PASSWORD"] = appRuntimePassword,
            ["PLATFORM_READONLY_PASSWORD"] = PlatformReadonlyPassword,
        };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MigrationRunner.Run(["--migrations", migrations], name => env.GetValueOrDefault(name), stdout, stderr);
        return new RunResult(exit, stdout.ToString(), stderr.ToString());
    }

    private string ChecksumOf(string file) => MigrationPlanner.Checksum(File.ReadAllText(Path.Combine(_workDir, file)));

    private List<(string, string)> Ledger()
    {
        var rows = new List<(string, string)>();
        using var connection = new NpgsqlConnection(ScratchConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT file_name, checksum FROM public.schema_migrations ORDER BY file_name COLLATE \"C\"", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private string LedgerWithTimes() => string.Join("\n", Ledger().Select(r => $"{r.Item1} {r.Item2}")) + "\n" +
        Scalar("SELECT string_agg(applied_at::text, ',' ORDER BY file_name) FROM public.schema_migrations");

    private object? Scalar(string sql)
    {
        using var connection = new NpgsqlConnection(ScratchConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand(sql, connection);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static bool TryPing(string connectionString)
    {
        try
        {
            Execute(new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 3 }.ConnectionString, "SELECT 1");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Commerce.sln from " + AppContext.BaseDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
        if (_available) Execute(_server, $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }
}
