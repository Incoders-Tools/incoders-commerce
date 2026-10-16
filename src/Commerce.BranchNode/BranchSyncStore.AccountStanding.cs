using System.Globalization;

namespace Commerce.BranchNode;

/// <summary>
/// The inputs of the organization's account standing as the terminal last received them (organization-account-standing
/// T8): the billing due date (null = not tracked), the grace days and whether a system administrator suspended it by
/// hand. The terminal evaluates them with <c>AccountStandingRules</c> on its own business day, so its notice keeps
/// counting offline. Inputs, not a computed status, on purpose.
/// </summary>
public sealed record AccountStandingReplica(DateOnly? DueOn, int GraceDays, bool Suspended);

/// <summary>
/// One row, replaced by every sync that brings a standing; a sync from a server that predates it leaves the last known
/// row. Its own table, apart from the organization settings, so neither replaces the other. The table is
/// <c>CREATE TABLE IF NOT EXISTS</c>, so a <c>branch.db</c> from before it opens and upgrades with no row.
/// </summary>
public sealed partial class BranchSyncStore
{
    private const string AccountStandingDateFormat = "yyyy-MM-dd";

    private void EnsureAccountStandingReplicaExists()
    {
        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS account_standing_replica (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                due_on TEXT NULL,
                grace_days INTEGER NOT NULL,
                suspended INTEGER NOT NULL
            );
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>Stores the organization's account standing inputs as the last known value.</summary>
    public void ApplyAccountStanding(AccountStandingReplica standing)
    {
        lock (_writeGate)
        {
            using var upsert = _connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO account_standing_replica (id, due_on, grace_days, suspended) VALUES (1, $dueOn, $graceDays, $suspended)
                ON CONFLICT (id) DO UPDATE SET
                    due_on = excluded.due_on, grace_days = excluded.grace_days, suspended = excluded.suspended;
                """;
            upsert.Parameters.AddWithValue("$dueOn",
                standing.DueOn is { } dueOn ? dueOn.ToString(AccountStandingDateFormat, CultureInfo.InvariantCulture) : DBNull.Value);
            upsert.Parameters.AddWithValue("$graceDays", standing.GraceDays);
            upsert.Parameters.AddWithValue("$suspended", standing.Suspended ? 1 : 0);
            upsert.ExecuteNonQuery();
        }
    }

    /// <summary>The last synced account standing inputs, or null when never synced.</summary>
    public AccountStandingReplica? GetAccountStanding()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT due_on, grace_days, suspended FROM account_standing_replica WHERE id = 1;";

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        DateOnly? dueOn = reader.IsDBNull(0)
            ? null
            : DateOnly.ParseExact(reader.GetString(0), AccountStandingDateFormat, CultureInfo.InvariantCulture);
        return new AccountStandingReplica(dueOn, reader.GetInt32(1), reader.GetInt32(2) != 0);
    }
}
