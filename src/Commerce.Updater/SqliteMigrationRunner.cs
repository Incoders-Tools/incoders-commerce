using Microsoft.Data.Sqlite;

namespace Commerce.Updater;

/// <summary>
/// Applies an additive schema-version migration and a post-migration health
/// check against the branch's live SQLite database (the same file
/// <c>BranchSyncStore</c> owns) without touching or losing existing
/// <c>sale_effects</c>/<c>outbox</c>/<c>inbox</c> rows.
/// </summary>
public interface IMigrationRunner
{
    void Migrate(string dbPath, int targetSchemaVersion);

    bool CheckHealth(string dbPath, int expectedSchemaVersion);
}

public sealed class SqliteMigrationRunner : IMigrationRunner
{
    public void Migrate(string dbPath, int targetSchemaVersion)
    {
        // Additive-only: creates the schema-meta table if absent and records
        // the new version. Never touches or drops sale_effects/outbox/inbox,
        // so committed local work is never at risk of this migration step.
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS schema_meta (schema_version INTEGER NOT NULL);";
            create.ExecuteNonQuery();
        }

        using var upsert = connection.CreateCommand();
        upsert.CommandText = "DELETE FROM schema_meta; INSERT INTO schema_meta (schema_version) VALUES ($v);";
        upsert.Parameters.AddWithValue("$v", targetSchemaVersion);
        upsert.ExecuteNonQuery();
    }

    public bool CheckHealth(string dbPath, int expectedSchemaVersion)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals((string?)integrity.ExecuteScalar(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        using var version = connection.CreateCommand();
        version.CommandText = "SELECT schema_version FROM schema_meta;";
        var result = version.ExecuteScalar();
        return result is not null && Convert.ToInt32(result) == expectedSchemaVersion;
    }
}
