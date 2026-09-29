using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `products.category_id` references `categories` (migration 0018), so any test
/// that persists a product needs a real category of the same organization.
/// Creates one directly through the owner connection (bypassing RLS) so the
/// setup does not depend on the endpoints under test.
/// </summary>
public static class CategoryFixture
{
    public static Guid Create(CloudTenantScope scope) => Create(scope.OrganizationId);

    public static Guid Create(Guid organizationId, string iconKey = "generic")
    {
        var id = Guid.NewGuid();
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO categories (id, organization_id, name, icon_key) VALUES ($1, $2, $3, $4)", owner);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue("Test category " + id.ToString("N"));
        cmd.Parameters.AddWithValue(iconKey);
        cmd.ExecuteNonQuery();
        return id;
    }
}
