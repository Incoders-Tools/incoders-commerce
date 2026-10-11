using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `005_vaca_verde_employee_roles.sql` (owner-requests-quick-adjustments Q5): Vaca Verde's staff positions, Titular
/// (the owner), Encargado (the owner's children), Carnicero, Chofer, Vendedor and Cajero. Safe to re-run and never over
/// a position edited or created in the app.
/// </summary>
public sealed partial class VacaVerdeSuppliersCatalogSeedTests
{
    private static readonly string[] SeededPositions = ["Titular", "Encargado", "Carnicero", "Chofer", "Vendedor", "Cajero"];

    private static void ApplyEmployeeRoles(NpgsqlConnection owner) => Exec(owner, SeedSql("005_vaca_verde_employee_roles.sql"));

    private static List<(string Name, string Key, int SortOrder, bool IsActive)> Positions(NpgsqlConnection owner, Guid orgId)
    {
        using var cmd = new NpgsqlCommand(
            "SELECT name, key, sort_order, is_active FROM employee_roles WHERE organization_id = $1 ORDER BY sort_order, name", owner);
        cmd.Parameters.AddWithValue(orgId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string, int, bool)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3)));
        return rows;
    }

    [Fact]
    public void EmployeeRolesSeed_LoadsTheSixPositions_InTheOwnersOrder_AndARerunAddsNothing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            ApplyEmployeeRoles(owner);
            var positions = Positions(owner, org.OrgId);
            Assert.Equal(SeededPositions, positions.Select(p => p.Name));
            Assert.Equal(["titular", "encargado", "carnicero", "chofer", "vendedor", "cajero"], positions.Select(p => p.Key));
            // Spaced by 10 so a position created later in the app (MAX + 10) lands after them.
            Assert.Equal([10, 20, 30, 40, 50, 60], positions.Select(p => p.SortOrder));
            Assert.All(positions, p => Assert.True(p.IsActive));

            ApplyEmployeeRoles(owner);
            Assert.Equal(6, Count(owner, "employee_roles", org.OrgId));
        }
        finally
        {
            RemoveVacaVerde(owner);
        }
    }

    [Fact]
    public void EmployeeRolesSeed_NeverOverwritesOrDuplicatesAPositionEditedOrCreatedInTheApp()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            // Created in the app before the seed, with its own id: kept, not duplicated, and the seed does not fail.
            var ownCashier = Guid.NewGuid();
            Exec(owner, "INSERT INTO employee_roles (id, organization_id, name, key, sort_order) VALUES ($1, $2, 'Cajero', 'cajero', 0)",
                ownCashier, org.OrgId);
            ApplyEmployeeRoles(owner);
            Assert.Equal(6, Count(owner, "employee_roles", org.OrgId));
            Assert.Equal(ownCashier, Scalar<Guid>(owner, "SELECT id FROM employee_roles WHERE organization_id = $1 AND key = 'cajero'", org.OrgId));

            // Renamed and deactivated in the app after the seed: a re-run leaves it as the owner left it.
            Exec(owner, "UPDATE employee_roles SET name = 'Repartidor', is_active = false WHERE organization_id = $1 AND key = 'chofer'", org.OrgId);
            ApplyEmployeeRoles(owner);
            var driver = Positions(owner, org.OrgId).Single(p => p.Key == "chofer");
            Assert.Equal(("Repartidor", false), (driver.Name, driver.IsActive));
            Assert.Equal(6, Count(owner, "employee_roles", org.OrgId));
        }
        finally
        {
            RemoveVacaVerde(owner);
        }
    }

    [Fact]
    public void EmployeeRolesSeed_GivesANewVacaVerdeItsOwnPositions_WhenAnEarlierOneWasRenamed()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var earlier = ProvisionVacaVerde(owner);
        try
        {
            // The seeded ids are global primary keys: they must include the organization, or the earlier one's rows
            // would silently block the new one's.
            ApplyEmployeeRoles(owner);
            Exec(owner, "UPDATE organizations SET name = 'Vaca Verde (anterior)' WHERE id = $1", earlier.OrgId);
            var current = ProvisionVacaVerde(owner);
            ApplyEmployeeRoles(owner);
            Assert.Equal(SeededPositions, Positions(owner, current.OrgId).Select(p => p.Name));
            Assert.Equal(6, Count(owner, "employee_roles", earlier.OrgId));
        }
        finally
        {
            RemoveVacaVerde(owner);
            Exec(owner, "DELETE FROM users WHERE organization_id = $1", earlier.OrgId);
            Exec(owner, "DELETE FROM organizations WHERE id = $1", earlier.OrgId);
        }
    }

    [Fact]
    public void EmployeeRolesSeed_WithoutTheOrganization_ChangesNothing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        RemoveVacaVerde(owner);
        var before = Scalar<long>(owner, "SELECT count(*) FROM employee_roles");
        ApplyEmployeeRoles(owner);
        Assert.Equal(before, Scalar<long>(owner, "SELECT count(*) FROM employee_roles"));
    }
}
