using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pos-installation-identity task 3.1: `PostgresDeviceCredentialStore`
/// against a LIVE Postgres instance (`deploy/dev/compose.yaml`). If Postgres
/// is not reachable, these tests report the gap clearly and return without
/// asserting pass/fail, matching the existing fixture convention
/// (`PostgresTestFixture`).
///
/// Also covers task 2.2: the hardware-replacement scenario relocated from
/// `TenantAccessTests.InstallationReplacement_MintsNewIdentity_...` — the
/// deleted in-memory `InstallationIdentityService` test — now proven against
/// the real `device_credentials` row instead of an in-memory dictionary.
/// </summary>
[Collection("Postgres")]
public sealed class DeviceCredentialStoreTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public DeviceCredentialStoreTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        ApplyMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root.");
        }
        return dir.FullName;
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var repoRoot = RepoRoot();

        var initSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        var usersSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0002_users.sql"));
        using (var cmd = new NpgsqlCommand(usersSql, owner)) cmd.ExecuteNonQuery();

        var orgsSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0003_organizations_branches.sql"));
        using (var cmd = new NpgsqlCommand(orgsSql, owner)) cmd.ExecuteNonQuery();

        var deviceSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0004_device_credentials.sql"));
        using (var cmd = new NpgsqlCommand(deviceSql, owner)) cmd.ExecuteNonQuery();

        var recoverySql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0005_password_recovery.sql"));
        using (var cmd = new NpgsqlCommand(recoverySql, owner)) cmd.ExecuteNonQuery();

        // CASCADE covers `customers` (0008), which may already exist in this
        // shared database from another test class in the same run even
        // though this class never applies 0008 itself.
        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task<(Guid OrgId, Guid BranchId, Guid UserId)> SeedOrgAndBranchAsync(NpgsqlDataSource dataSource)
    {
        var userStore = new PostgresUserAccountStore(dataSource);
        var orgStore = new PostgresOrganizationStore(dataSource, userStore);

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);

        var outcome = await orgStore.TryCreateBootstrapAsync(
            scope,
            new NewOrganization(orgId, "Device Test Org"),
            new NewBranch(branchId, "Main"),
            new NewUserAccount(userId, $"device-owner-{orgId}@example.com", "hash", [branchId], [new RoleDto("admin", Permission.ViewSales)]),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, outcome);

        return (orgId, branchId, userId);
    }

    [Fact]
    public async Task IssueAsync_ThenFindByTokenHash_ReturnsTheExactRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, userId) = await SeedOrgAndBranchAsync(_dataSource!);
        var store = new PostgresDeviceCredentialStore(_dataSource!);
        var installationId = Guid.NewGuid();

        var issued = await store.IssueAsync(new CloudTenantScope(orgId), installationId, branchId, userId, CancellationToken.None);

        Assert.NotNull(issued.PlaintextToken);
        var hash = DeviceTokenHasher.Hash(issued.PlaintextToken);
        var found = await store.FindByTokenHashAsync(hash, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(orgId, found!.OrganizationId);
        Assert.Equal(branchId, found.BranchId);
        Assert.Equal(installationId, found.InstallationId);
        Assert.False(found.IsRevoked);
    }

    [Fact]
    public async Task FindByTokenHashAsync_UnknownHash_ReturnsNull()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresDeviceCredentialStore(_dataSource!);

        var found = await store.FindByTokenHashAsync("never-issued-hash", CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task RevokeAsync_RevokedRow_StillReturnedByFind_WithIsRevokedTrue()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, userId) = await SeedOrgAndBranchAsync(_dataSource!);
        var store = new PostgresDeviceCredentialStore(_dataSource!);
        var scope = new CloudTenantScope(orgId);

        var issued = await store.IssueAsync(scope, Guid.NewGuid(), branchId, userId, CancellationToken.None);
        var revoked = await store.RevokeAsync(scope, issued.Record.Id, CancellationToken.None);
        Assert.True(revoked);

        var found = await store.FindByTokenHashAsync(DeviceTokenHasher.Hash(issued.PlaintextToken), CancellationToken.None);

        Assert.NotNull(found);
        Assert.True(found!.IsRevoked);
    }

    /// <summary>
    /// Hardware-replacement lineage, relocated from
    /// `TenantAccessTests.InstallationReplacement_MintsNewIdentity_...`
    /// (ADR-002): re-issuing for the SAME installationId revokes the prior
    /// row and records `replaces_credential_id`, exactly what
    /// `InstallationIdentityService.ReplaceForHardwareChange` used to do in
    /// memory — now durable.
    /// </summary>
    [Fact]
    public async Task IssueAsync_SameInstallationId_RevokesPriorRow_AndRecordsLineage()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, userId) = await SeedOrgAndBranchAsync(_dataSource!);
        var store = new PostgresDeviceCredentialStore(_dataSource!);
        var scope = new CloudTenantScope(orgId);
        var installationId = Guid.NewGuid();

        var original = await store.IssueAsync(scope, installationId, branchId, userId, CancellationToken.None);
        var replacement = await store.IssueAsync(scope, installationId, branchId, userId, CancellationToken.None);

        Assert.NotEqual(original.Record.Id, replacement.Record.Id);
        Assert.Equal(original.Record.Id, replacement.Record.ReplacesCredentialId);
        Assert.False(replacement.Record.IsRevoked);

        var priorAfterReplacement = await store.FindByTokenHashAsync(
            DeviceTokenHasher.Hash(original.PlaintextToken), CancellationToken.None);
        Assert.NotNull(priorAfterReplacement);
        Assert.True(priorAfterReplacement!.IsRevoked);
    }

    /// <summary>
    /// Cross-org re-pairing: re-issuing for the same installationId into a
    /// DIFFERENT organization must still revoke the prior organization's row
    /// (the unscoped-but-revoked UPDATE trick from `device_credentials_revoke`),
    /// never leaving org A's binding alive after the terminal moves to org B.
    /// </summary>
    [Fact]
    public async Task IssueAsync_SameInstallationId_DifferentOrganization_RevokesPriorOrgRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgAId, branchAId, userAId) = await SeedOrgAndBranchAsync(_dataSource!);
        var (orgBId, branchBId, userBId) = await SeedOrgAndBranchAsync(_dataSource!);
        var store = new PostgresDeviceCredentialStore(_dataSource!);
        var installationId = Guid.NewGuid();

        var orgACredential = await store.IssueAsync(new CloudTenantScope(orgAId), installationId, branchAId, userAId, CancellationToken.None);
        var orgBCredential = await store.IssueAsync(new CloudTenantScope(orgBId), installationId, branchBId, userBId, CancellationToken.None);

        Assert.Equal(orgBId, orgBCredential.Record.OrganizationId);
        Assert.False(orgBCredential.Record.IsRevoked);

        var orgARowAfter = await store.FindByTokenHashAsync(
            DeviceTokenHasher.Hash(orgACredential.PlaintextToken), CancellationToken.None);
        Assert.NotNull(orgARowAfter);
        Assert.True(orgARowAfter!.IsRevoked);
    }
}
