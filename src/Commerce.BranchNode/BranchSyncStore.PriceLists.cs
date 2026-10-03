using System.Globalization;
using Commerce.Domain.Pricing;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

/// <summary>One price list as known to this branch (customer-price-lists T4): id, name, whether it is the branch default, its floor.</summary>
public sealed record PriceListReplica(Guid Id, string Name, bool IsDefault, Guid? FloorPriceListId);

/// <summary>The BASE price effective for a presentation in one list (never the composed price).</summary>
public sealed record PriceListEntryReplica(Guid PriceListId, Guid PresentationId, decimal UnitPrice, DateOnly EffectiveFrom);

/// <summary>One rate component of a replicated set; <see cref="CalculationBase"/> is the enum name (`Base`, `Subtotal`).</summary>
public sealed record RateComponentReplica(string Code, string Label, decimal Percentage, string CalculationBase, int Order);

/// <summary>A rate component set; <see cref="PriceListId"/> is null for the organization default set.</summary>
public sealed record RateSetReplica(Guid Id, Guid? PriceListId, DateOnly EffectiveFrom, IReadOnlyList<RateComponentReplica> Components);

/// <summary>The price list a customer is priced from.</summary>
public sealed record CustomerPriceListReplica(Guid CustomerId, Guid PriceListId);

/// <summary>
/// One whole `price-lists` snapshot (channel `price-lists`): applying it REPLACES the replica, so what the cloud no longer
/// has disappears here. `OrganizationId` is stamped by the caller (the terminal's paired organization), like
/// <see cref="CustomerReplica"/>.
/// </summary>
public sealed record PriceListsReplicaSnapshot(
    Guid OrganizationId,
    IReadOnlyList<PriceListReplica> Lists,
    IReadOnlyList<PriceListEntryReplica> Entries,
    IReadOnlyList<RateSetReplica> RateSets,
    IReadOnlyList<CustomerPriceListReplica> CustomerPriceLists,
    Guid? OrganizationDefaultCustomerPriceListId);

public sealed partial class BranchSyncStore
{
    /// <summary>The cursor channel of the `price-lists` snapshot; its value is the server time of the last applied snapshot.</summary>
    public const string PriceListsChannel = "price-lists";

    /// <summary>
    /// customer-price-lists T4. Every table is `CREATE TABLE IF NOT EXISTS`, so a `branch.db` from before price lists gains
    /// them empty and reopening is idempotent. An empty replica means "never synced": the POS then prices from the single
    /// default list in `price_replica`, exactly as before.
    /// </summary>
    private void EnsurePriceListsReplicaExists()
    {
        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS price_lists_replica (
                price_list_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                is_default INTEGER NOT NULL,
                floor_price_list_id TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS price_list_entries_replica (
                price_list_id TEXT NOT NULL,
                presentation_id TEXT NOT NULL,
                unit_price TEXT NOT NULL,
                effective_from TEXT NOT NULL,
                PRIMARY KEY (price_list_id, presentation_id)
            );
            CREATE TABLE IF NOT EXISTS rate_sets_replica (
                set_id TEXT PRIMARY KEY,
                organization_id TEXT NOT NULL,
                price_list_id TEXT NULL,
                effective_from TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS rate_components_replica (
                set_id TEXT NOT NULL,
                code TEXT NOT NULL,
                label TEXT NOT NULL,
                percentage TEXT NOT NULL,
                calculation_base TEXT NOT NULL,
                component_order INTEGER NOT NULL,
                PRIMARY KEY (set_id, component_order)
            );
            CREATE TABLE IF NOT EXISTS customer_price_lists_replica (
                customer_id TEXT PRIMARY KEY,
                price_list_id TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS price_list_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                default_customer_price_list_id TEXT NULL
            );
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>
    /// The real pull entry point: replaces the whole price list replica and advances the cursor in ONE transaction, so a
    /// failure never leaves the replica ahead of the cursor or vice versa, a redelivery is idempotent, and a list, price,
    /// set or assignment removed in the cloud is gone here.
    /// </summary>
    public void ApplyPriceListsSync(PriceListsReplicaSnapshot snapshot, DateTimeOffset serverTimeUtc)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            WritePriceListsSnapshot(snapshot, serverTimeUtc, transaction);
            transaction.Commit();
        }
    }

    /// <summary>Test-only atomicity proof: the same writes as <see cref="ApplyPriceListsSync"/>, never committed.</summary>
    public void SimulateInterruptedPriceListsSync(PriceListsReplicaSnapshot snapshot, DateTimeOffset serverTimeUtc)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            WritePriceListsSnapshot(snapshot, serverTimeUtc, transaction);
            // Deliberately abandoned: no Commit().
        }
    }

    private void WritePriceListsSnapshot(PriceListsReplicaSnapshot snapshot, DateTimeOffset serverTimeUtc, SqliteTransaction transaction)
    {
        Exec(transaction, """
            DELETE FROM price_lists_replica;
            DELETE FROM price_list_entries_replica;
            DELETE FROM rate_sets_replica;
            DELETE FROM rate_components_replica;
            DELETE FROM customer_price_lists_replica;
            DELETE FROM price_list_settings;
            """);

        foreach (var list in snapshot.Lists)
        {
            Exec(transaction,
                "INSERT INTO price_lists_replica (price_list_id, name, is_default, floor_price_list_id) VALUES ($id, $name, $isDefault, $floor);",
                ("$id", list.Id.ToString()), ("$name", list.Name), ("$isDefault", list.IsDefault ? 1 : 0),
                ("$floor", list.FloorPriceListId?.ToString()));
        }

        foreach (var entry in snapshot.Entries)
        {
            Exec(transaction,
                "INSERT INTO price_list_entries_replica (price_list_id, presentation_id, unit_price, effective_from) VALUES ($list, $presentation, $price, $from);",
                ("$list", entry.PriceListId.ToString()), ("$presentation", entry.PresentationId.ToString()),
                ("$price", entry.UnitPrice.ToString(CultureInfo.InvariantCulture)), ("$from", entry.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        foreach (var set in snapshot.RateSets)
        {
            Exec(transaction,
                "INSERT INTO rate_sets_replica (set_id, organization_id, price_list_id, effective_from) VALUES ($id, $org, $list, $from);",
                ("$id", set.Id.ToString()), ("$org", snapshot.OrganizationId.ToString()), ("$list", set.PriceListId?.ToString()),
                ("$from", set.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            foreach (var component in set.Components)
            {
                Exec(transaction,
                    "INSERT INTO rate_components_replica (set_id, code, label, percentage, calculation_base, component_order) VALUES ($set, $code, $label, $percentage, $base, $order);",
                    ("$set", set.Id.ToString()), ("$code", component.Code), ("$label", component.Label),
                    ("$percentage", component.Percentage.ToString(CultureInfo.InvariantCulture)),
                    ("$base", component.CalculationBase), ("$order", component.Order));
            }
        }

        foreach (var assignment in snapshot.CustomerPriceLists)
        {
            Exec(transaction,
                "INSERT INTO customer_price_lists_replica (customer_id, price_list_id) VALUES ($customer, $list);",
                ("$customer", assignment.CustomerId.ToString()), ("$list", assignment.PriceListId.ToString()));
        }

        Exec(transaction,
            "INSERT INTO price_list_settings (id, default_customer_price_list_id) VALUES (1, $list);",
            ("$list", snapshot.OrganizationDefaultCustomerPriceListId?.ToString()));
        UpsertCursor(PriceListsChannel, serverTimeUtc, transaction);
    }

    private void Exec(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    public DateTimeOffset? GetPriceListsCursor()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT last_synced_utc FROM sync_cursors WHERE channel = $channel;";
        command.Parameters.AddWithValue("$channel", PriceListsChannel);

        var raw = command.ExecuteScalar();
        return raw is null or DBNull ? null : DateTimeOffset.Parse((string)raw, CultureInfo.InvariantCulture);
    }

    /// <summary>Every price list this branch knows, by name; empty when the `price-lists` channel never synced.</summary>
    public IReadOnlyList<PriceListReplica> ListPriceLists()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT price_list_id, name, is_default, floor_price_list_id FROM price_lists_replica ORDER BY name;";

        var results = new List<PriceListReplica>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new PriceListReplica(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2) != 0,
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3))));
        }

        return results;
    }

    /// <summary>
    /// The BASE price of a presentation in one list on a date (the replica keeps the entry effective when the snapshot was
    /// taken, never history). Not yet effective, or no entry, is `null`: never a zero fallback.
    /// </summary>
    public decimal? GetEffectivePrice(Guid priceListId, Guid presentationId, DateOnly effectiveOn)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT unit_price, effective_from FROM price_list_entries_replica
            WHERE price_list_id = $list AND presentation_id = $presentation;
            """;
        command.Parameters.AddWithValue("$list", priceListId.ToString());
        command.Parameters.AddWithValue("$presentation", presentationId.ToString());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture) <= effectiveOn
            ? decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// The rate component set effective on a date for a list, exactly as the cloud resolves it: the list's own latest set on
    /// or before the date, or, only when the list has none, the organization default set, or `null` for no set anywhere
    /// (which composes to the base price). Inheritance is all-or-nothing, never a per-component merge.
    /// </summary>
    public RateComponentSet? GetEffectiveRateSet(Guid priceListId, DateOnly effectiveOn) =>
        ReadLatestRateSet("price_list_id = $list", effectiveOn, ("$list", priceListId.ToString()))
        ?? ReadLatestRateSet("price_list_id IS NULL", effectiveOn);

    private RateComponentSet? ReadLatestRateSet(string ownerFilter, DateOnly effectiveOn, params (string Name, object Value)[] parameters)
    {
        Guid setId, organizationId;
        Guid? priceListId;
        DateOnly effectiveFrom;
        using (var header = _connection.CreateCommand())
        {
            header.CommandText = $"""
                SELECT set_id, organization_id, price_list_id, effective_from FROM rate_sets_replica
                WHERE {ownerFilter} AND effective_from <= $on
                ORDER BY effective_from DESC LIMIT 1;
                """;
            header.Parameters.AddWithValue("$on", effectiveOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            foreach (var (name, value) in parameters)
            {
                header.Parameters.AddWithValue(name, value);
            }

            using var reader = header.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            setId = Guid.Parse(reader.GetString(0));
            organizationId = Guid.Parse(reader.GetString(1));
            priceListId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));
            effectiveFrom = DateOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
        }

        var components = new List<RateComponent>();
        using (var rows = _connection.CreateCommand())
        {
            rows.CommandText = """
                SELECT code, label, percentage, calculation_base, component_order FROM rate_components_replica
                WHERE set_id = $set ORDER BY component_order;
                """;
            rows.Parameters.AddWithValue("$set", setId.ToString());
            using var reader = rows.ExecuteReader();
            while (reader.Read())
            {
                components.Add(new RateComponent(
                    reader.GetString(0), reader.GetString(1), decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    Enum.Parse<RateCalculationBase>(reader.GetString(3)), reader.GetInt32(4)));
            }
        }

        return priceListId is { } list
            ? RateComponentSet.ForPriceList(setId, organizationId, list, effectiveFrom, components)
            : RateComponentSet.ForOrganizationDefault(setId, organizationId, effectiveFrom, components);
    }

    /// <summary>The price list a customer is assigned (`null`: none replicated, the buyer rule falls to the organization default).</summary>
    public Guid? GetCustomerPriceListId(Guid customerId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT price_list_id FROM customer_price_lists_replica WHERE customer_id = $customer;";
        command.Parameters.AddWithValue("$customer", customerId.ToString());

        return command.ExecuteScalar() is string raw ? Guid.Parse(raw) : null;
    }

    /// <summary>The organization default price list for customers, or `null` when none is set or the channel never synced.</summary>
    public Guid? GetOrganizationDefaultCustomerPriceListId()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT default_customer_price_list_id FROM price_list_settings WHERE id = 1;";

        return command.ExecuteScalar() is string raw ? Guid.Parse(raw) : null;
    }
}
