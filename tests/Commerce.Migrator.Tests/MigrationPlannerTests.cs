using Commerce.Migrator;

namespace Commerce.Migrator.Tests;

public sealed class MigrationPlannerTests
{
    private static readonly Dictionary<string, string> NoLedger = new();

    private static string? NoEnvironment(string name) => null;

    [Fact]
    public void Checksum_is_lowercase_sha256_hex_of_utf8_text()
    {
        // printf 'abc' | sha256sum
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            MigrationPlanner.Checksum("abc"));
    }

    [Fact]
    public void Checksum_is_the_same_for_crlf_and_lf_line_endings()
    {
        Assert.Equal(
            MigrationPlanner.Checksum("SELECT 1;\nSELECT 2;\n"),
            MigrationPlanner.Checksum("SELECT 1;\r\nSELECT 2;\r\n"));
    }

    [Fact]
    public void Files_run_in_ordinal_name_order()
    {
        var plan = MigrationPlanner.Plan(
            [new("0010_b.sql", "b"), new("0002_a.sql", "a"), new("0010_B.sql", "B")],
            NoLedger,
            NoEnvironment);

        Assert.False(plan.Failed);
        Assert.Equal(["0002_a.sql", "0010_B.sql", "0010_b.sql"], plan.Steps.Select(s => s.Name));
        Assert.All(plan.Steps, s => Assert.False(s.Skip));
    }

    [Fact]
    public void Applied_file_with_the_same_checksum_is_skipped()
    {
        var ledger = new Dictionary<string, string> { ["0001_a.sql"] = MigrationPlanner.Checksum("a\n") };

        // Same text checked out with CRLF still matches the ledger.
        var plan = MigrationPlanner.Plan(
            [new("0001_a.sql", "a\r\n"), new("0002_b.sql", "b")],
            ledger,
            NoEnvironment);

        Assert.False(plan.Failed);
        Assert.Equal([true, false], plan.Steps.Select(s => s.Skip));
    }

    [Fact]
    public void Applied_file_with_a_changed_checksum_fails_naming_the_file_and_plans_nothing()
    {
        var ledger = new Dictionary<string, string> { ["0002_b.sql"] = MigrationPlanner.Checksum("b") };

        var plan = MigrationPlanner.Plan(
            [new("0001_a.sql", "a"), new("0002_b.sql", "b edited"), new("0003_c.sql", "c")],
            ledger,
            NoEnvironment);

        Assert.True(plan.Failed);
        Assert.Contains("0002_b.sql", plan.Error);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Placeholder_is_substituted_only_in_the_executed_sql()
    {
        const string raw = "ALTER ROLE app_runtime PASSWORD '__APP_RUNTIME_PASSWORD__';\r\n" +
                           "ALTER ROLE platform_readonly PASSWORD '__PLATFORM_READONLY_PASSWORD__';";
        var env = new Dictionary<string, string?>
        {
            ["APP_RUNTIME_PASSWORD"] = "s3cret",
            ["PLATFORM_READONLY_PASSWORD"] = "r3ad",
        };

        var plan = MigrationPlanner.Plan([new("0001_roles.sql", raw)], NoLedger, name => env.GetValueOrDefault(name));

        var step = Assert.Single(plan.Steps);
        Assert.Equal(MigrationPlanner.Checksum(raw), step.Checksum);
        Assert.Equal(
            "ALTER ROLE app_runtime PASSWORD 's3cret';\r\nALTER ROLE platform_readonly PASSWORD 'r3ad';",
            step.Sql);
    }

    [Fact]
    public void Pending_file_needing_a_missing_variable_fails_and_plans_nothing()
    {
        var plan = MigrationPlanner.Plan(
            [new("0001_a.sql", "a"), new("0002_roles.sql", "PASSWORD '__PLATFORM_READONLY_PASSWORD__'")],
            NoLedger,
            name => name == "APP_RUNTIME_PASSWORD" ? "set" : null);

        Assert.True(plan.Failed);
        Assert.Contains("0002_roles.sql", plan.Error);
        Assert.Contains("PLATFORM_READONLY_PASSWORD", plan.Error);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Substituted_value_with_a_single_quote_fails_naming_the_variable_without_its_value()
    {
        const string value = "pa'ss; DROP TABLE x; --";

        var plan = MigrationPlanner.Plan(
            [new("0001_a.sql", "a"), new("0002_roles.sql", "PASSWORD '__APP_RUNTIME_PASSWORD__'")],
            NoLedger,
            name => name == "APP_RUNTIME_PASSWORD" ? value : null);

        Assert.True(plan.Failed);
        Assert.Contains("0002_roles.sql", plan.Error);
        Assert.Contains("APP_RUNTIME_PASSWORD", plan.Error);
        Assert.DoesNotContain("pa'ss", plan.Error);
        Assert.DoesNotContain("DROP TABLE", plan.Error);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Missing_variable_needed_only_by_an_applied_file_is_not_an_error()
    {
        const string roles = "PASSWORD '__APP_RUNTIME_PASSWORD__'";
        var ledger = new Dictionary<string, string> { ["0001_roles.sql"] = MigrationPlanner.Checksum(roles) };

        var plan = MigrationPlanner.Plan(
            [new("0001_roles.sql", roles), new("0002_b.sql", "b")],
            ledger,
            NoEnvironment);

        Assert.False(plan.Failed);
        Assert.Equal([true, false], plan.Steps.Select(s => s.Skip));
    }
}
