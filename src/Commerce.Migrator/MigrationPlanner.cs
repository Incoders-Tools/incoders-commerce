using System.Security.Cryptography;
using System.Text;

namespace Commerce.Migrator;

public sealed record MigrationFile(string Name, string Content);

/// <summary>One file in run order: skipped (already applied) or pending with the SQL to execute.</summary>
public sealed record PlannedMigration(string Name, string Checksum, bool Skip, string Sql);

/// <summary>Either the ordered steps of a run or the error that stops it before anything executes.</summary>
public sealed record MigrationPlan(IReadOnlyList<PlannedMigration> Steps, string? Error)
{
    public bool Failed => Error is not null;

    public static MigrationPlan Fail(string error) => new([], error);
}

/// <summary>
/// The pure half of the runner: decides what a run does from the files on disk,
/// the ledger rows and the environment, without touching a database.
/// </summary>
public static class MigrationPlanner
{
    private static readonly (string Placeholder, string Variable)[] Placeholders =
    [
        ("__APP_RUNTIME_PASSWORD__", "APP_RUNTIME_PASSWORD"),
        ("__PLATFORM_READONLY_PASSWORD__", "PLATFORM_READONLY_PASSWORD"),
    ];

    /// <summary>
    /// SHA-256 of the raw text with CRLF normalized to LF, so a Windows checkout and a
    /// Linux checkout of the same file match the same ledger row.
    /// </summary>
    public static string Checksum(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n"))));

    public static MigrationPlan Plan(
        IEnumerable<MigrationFile> files,
        IReadOnlyDictionary<string, string> ledger,
        Func<string, string?> environment)
    {
        var steps = new List<PlannedMigration>();
        foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var checksum = Checksum(file.Content);
            if (ledger.TryGetValue(file.Name, out var applied))
            {
                if (applied != checksum)
                {
                    return MigrationPlan.Fail(
                        $"{file.Name} was applied with checksum {applied} but now has {checksum}; " +
                        "applied migrations must not be edited, add a new migration instead");
                }
                steps.Add(new(file.Name, checksum, Skip: true, Sql: ""));
                continue;
            }

            var sql = file.Content;
            foreach (var (placeholder, variable) in Placeholders)
            {
                if (!sql.Contains(placeholder, StringComparison.Ordinal)) continue;
                var value = environment(variable);
                if (string.IsNullOrEmpty(value))
                {
                    return MigrationPlan.Fail($"{file.Name} needs {variable}, which is not set");
                }
                sql = sql.Replace(placeholder, value, StringComparison.Ordinal);
            }
            steps.Add(new(file.Name, checksum, Skip: false, Sql: sql));
        }
        return new(steps, null);
    }
}
