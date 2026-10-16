using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0037_customer_price_lists.sql`: a customer's own price list, the organization's default list for new customers,
/// and the floor list of a price list. Every reference stays inside one organization (customers and organizations are
/// organization scoped, price lists are branch scoped) and a floor list stays inside the branch.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerPriceListsMigrationTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    public void Dispose()
    {
        if (!_postgresAvailable) return;
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        Exec(owner, "DELETE FROM organizations WHERE name LIKE 'cpl %'");
    }

    private static NpgsqlConnection OpenOwnerWithMigrations()
    {
        var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }

        return owner;
    }

    private static void Exec(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return (T)cmd.ExecuteScalar()!;
    }

    private sealed record Tenant(Guid OrgId, Guid BranchId, Guid ListA, Guid ListB);

    private static Tenant NewTenant(NpgsqlConnection owner)
    {
        var org = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, $2)", org, "cpl " + org);
        Exec(owner, "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", branch, org);
        Exec(owner, "INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'A', true, $4)", a, org, branch, Guid.NewGuid());
        Exec(owner, "INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'B', false, $4)", b, org, branch, Guid.NewGuid());
        return new Tenant(org, branch, a, b);
    }

    private static Guid NewCustomer(NpgsqlConnection owner, Guid orgId, Guid? priceListId = null)
    {
        var id = Guid.NewGuid();
        Exec(owner,
            "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id, price_list_id) VALUES ($1, $2, 'Wholesale', 'Cliente', $3, $4)",
            id, orgId, Guid.NewGuid(), (object?)priceListId ?? DBNull.Value);
        return id;
    }

    [Fact]
    public void Migration_AddsTheThreeReferences_AllNullableAndRerunsSafely()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwnerWithMigrations();
        PostgresTestFixture.ApplyMigration(owner, "0037_customer_price_lists.sql");

        var t = NewTenant(owner);
        var customer = NewCustomer(owner, t.OrgId);

        Assert.True(Scalar<bool>(owner, "SELECT price_list_id IS NULL FROM customers WHERE id = $1", customer));
        Assert.True(Scalar<bool>(owner, "SELECT default_customer_price_list_id IS NULL FROM organizations WHERE id = $1", t.OrgId));
        Assert.True(Scalar<bool>(owner, "SELECT floor_price_list_id IS NULL FROM price_lists WHERE id = $1", t.ListA));
    }

    [Fact]
    public void CustomerPriceList_MustBelongToTheCustomersOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwnerWithMigrations();
        var mine = NewTenant(owner);
        var other = NewTenant(owner);

        NewCustomer(owner, mine.OrgId, mine.ListB); // same organization: fine
        var ex = Assert.Throws<PostgresException>(() => NewCustomer(owner, mine.OrgId, other.ListB));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public void OrganizationDefaultCustomerList_MustBelongToTheOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwnerWithMigrations();
        var mine = NewTenant(owner);
        var other = NewTenant(owner);

        Exec(owner, "UPDATE organizations SET default_customer_price_list_id = $1 WHERE id = $2", mine.ListB, mine.OrgId);
        var ex = Assert.Throws<PostgresException>(() =>
            Exec(owner, "UPDATE organizations SET default_customer_price_list_id = $1 WHERE id = $2", other.ListB, mine.OrgId));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public void FloorList_StaysInTheBranch_AndIsNeverTheListItself()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwnerWithMigrations();
        var t = NewTenant(owner);
        var otherBranch = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        Exec(owner, "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Second')", otherBranch, t.OrgId);
        Exec(owner, "INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'Foreign', false, $4)", foreignList, t.OrgId, otherBranch, Guid.NewGuid());

        Exec(owner, "UPDATE price_lists SET floor_price_list_id = $1 WHERE id = $2", t.ListB, t.ListA);
        Assert.Equal(t.ListB, Scalar<Guid>(owner, "SELECT floor_price_list_id FROM price_lists WHERE id = $1", t.ListA));

        var self = Assert.Throws<PostgresException>(() =>
            Exec(owner, "UPDATE price_lists SET floor_price_list_id = id WHERE id = $1", t.ListB));
        Assert.Equal(PostgresErrorCodes.CheckViolation, self.SqlState);

        var crossBranch = Assert.Throws<PostgresException>(() =>
            Exec(owner, "UPDATE price_lists SET floor_price_list_id = $1 WHERE id = $2", foreignList, t.ListB));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossBranch.SqlState);
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheCustomerPriceListsMigrationVerbatim()
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        var init = Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")));
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", "0037_customer_price_lists.sql"))), init);
    }
}
