namespace Commerce.BranchNode;

/// <summary>
/// The organization settings a terminal keeps offline (operator-ux-adjustments T5): today the quantity decimal
/// separator (<c>Comma</c> or <c>Dot</c>) the web settings edit. One row, replaced by every successful sync, so the
/// terminal keeps the last known value; the table is <c>CREATE TABLE IF NOT EXISTS</c>, so a <c>branch.db</c> from
/// before it opens and upgrades with no row ("never synced").
/// </summary>
public sealed partial class BranchSyncStore
{
    private void EnsureOrganizationSettingsReplicaExists()
    {
        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS organization_settings_replica (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                quantity_decimal_separator TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>Stores the organization's quantity decimal separator as the last known value.</summary>
    public void ApplyOrganizationSettings(string quantityDecimalSeparator)
    {
        lock (_writeGate)
        {
            using var upsert = _connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO organization_settings_replica (id, quantity_decimal_separator) VALUES (1, $separator)
                ON CONFLICT (id) DO UPDATE SET quantity_decimal_separator = excluded.quantity_decimal_separator;
                """;
            upsert.Parameters.AddWithValue("$separator", quantityDecimalSeparator);
            upsert.ExecuteNonQuery();
        }
    }

    /// <summary>The last synced quantity decimal separator (<c>Comma</c> or <c>Dot</c>), or null when never synced.</summary>
    public string? GetQuantityDecimalSeparator()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT quantity_decimal_separator FROM organization_settings_replica WHERE id = 1;";

        return command.ExecuteScalar() as string;
    }
}
