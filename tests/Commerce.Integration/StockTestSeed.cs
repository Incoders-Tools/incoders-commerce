using Npgsql;

namespace Commerce.Integration;

/// <summary>Raw owner-connection seeding of the catalog rows (branch, product, presentation) the purchasing and stock tests need.</summary>
public static class StockTestSeed
{
    public static NpgsqlConnection OpenOwner()
    {
        var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        return owner;
    }

    public static void Exec(NpgsqlConnection conn, string sql, params object?[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static T Scalar<T>(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? default! : (T)value;
    }

    public static Guid Branch(NpgsqlConnection owner, Guid organizationId, string name = "Sucursal")
    {
        var id = Guid.NewGuid();
        Exec(owner, "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, $3)", id, organizationId, name);
        return id;
    }

    /// <summary>Seeds a product and one presentation of it in the branch; returns the presentation id.</summary>
    public static Guid Presentation(
        Guid organizationId, Guid branchId, string productName = "Media res", string presentationName = "Kg",
        string behavior = "Weighted")
    {
        using var owner = OpenOwner();
        var productId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        Exec(owner,
            "INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id) VALUES ($1, $2, $3, $4, $5, $6, $6)",
            productId, organizationId, branchId, productName, CategoryFixture.Create(organizationId), actor);
        Exec(owner,
            "INSERT INTO presentations (id, organization_id, branch_id, product_id, name, quantity_behavior, unit_id, created_by_user_id) VALUES ($1, $2, $3, $4, $5, $6, $7, $7)",
            presentationId, organizationId, branchId, productId, presentationName, behavior, actor);
        return presentationId;
    }
}
