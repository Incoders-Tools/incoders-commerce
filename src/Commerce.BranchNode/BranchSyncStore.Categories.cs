using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

/// <summary>A product category as the cloud defines it, with whether (and where) the POS category rail offers it.</summary>
public sealed record CategoryReplica(Guid Id, string Name, string IconKey, bool ShowInPos, int PosSortOrder);

/// <summary>
/// The categories of the organization (<c>categories_replica</c>), replaced whole with every `price-lists` snapshot (a
/// handful of rows), so the rail is built from local data at startup and only changes when a sync brings changes. Until a
/// snapshot carrying categories has been applied (a server that predates them), the rail falls back to the categories of
/// the local catalog.
/// </summary>
public sealed partial class BranchSyncStore
{
    /// <summary>Marks that a snapshot with categories was applied: from then on the replica decides the rail.</summary>
    private const string CategoriesChannel = "categories";

    private void EnsureCategoryStorageExists()
    {
        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS categories_replica (
                category_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                icon_key TEXT NOT NULL,
                show_in_pos INTEGER NOT NULL,
                pos_sort_order INTEGER NOT NULL
            );
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>Replaces the categories (part of the `price-lists` snapshot transaction). Null: the server sent none, keep them.</summary>
    private void WriteCategoriesSnapshot(IReadOnlyList<CategoryReplica>? categories, SqliteTransaction transaction)
    {
        if (categories is null)
        {
            return;
        }

        Exec(transaction, "DELETE FROM categories_replica;");
        foreach (var category in categories)
        {
            Exec(transaction,
                """
                INSERT INTO categories_replica (category_id, name, icon_key, show_in_pos, pos_sort_order)
                VALUES ($id, $name, $icon, $show, $order);
                """,
                ("$id", category.Id.ToString()), ("$name", category.Name), ("$icon", category.IconKey),
                ("$show", category.ShowInPos ? 1 : 0), ("$order", category.PosSortOrder));
        }

        UpsertCursor(CategoriesChannel, DateTimeOffset.UtcNow, transaction);
    }

    /// <summary>
    /// The categories the POS rail offers: the ones set to show on the POS, by their POS order then name, products or not
    /// (<c>Configured</c> true). Before any snapshot brought categories: the categories of the local catalog, by name
    /// (<c>Configured</c> false, and the caller applies its own default).
    /// </summary>
    public (IReadOnlyList<CatalogCategory> Categories, bool Configured) ListPosRailCategories(Guid organizationId)
    {
        if (ReadCursor(CategoriesChannel) is null)
        {
            return (ListCatalogCategories(organizationId), false);
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT category_id, name, icon_key FROM categories_replica
            WHERE show_in_pos = 1
            ORDER BY pos_sort_order, name COLLATE NOCASE, category_id;
            """;
        var categories = new List<CatalogCategory>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            categories.Add(new CatalogCategory(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        }

        return (categories, true);
    }
}
