using Commerce.Domain.Identity;

namespace Commerce.Integration;

public sealed class AdminConsoleUnitTests
{
    [Fact]
    public void UserAccount_IsSystemAdmin_RoundTripsThroughConstruction()
    {
        var account = new UserAccount(Guid.NewGuid(), Guid.NewGuid(), [], [], isSystemAdmin: true);
        Assert.True(account.IsSystemAdmin);
    }

    [Fact]
    public void UserAccount_DefaultsToNonSystemAdmin()
    {
        var account = new UserAccount(Guid.NewGuid(), Guid.NewGuid(), [], []);
        Assert.False(account.IsSystemAdmin);
    }

    [Fact]
    public void AdminConsoleMigration_DeclaresIdempotentUnifiedIdentityTransition()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Commerce.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "deploy", "db", "migrations", "0012_admin_console.sql");
        var sql = File.ReadAllText(path);
        Assert.Contains("is_system_admin boolean NOT NULL DEFAULT false", sql);
        Assert.Contains("DROP TABLE platform_admins", sql);
        Assert.Contains("to_regclass('public.platform_admins')", sql);
    }
}

// placeholder
