using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.Domain.Identity;

namespace Commerce.Integration;

/// <summary>
/// Covers `openspec/changes/commerce-foundation/specs/tenant-access-foundation/spec.md`:
/// organization/branch isolation, role and revocation enforcement, installation
/// identity, and audit — using a two-branch Organization A plus a second
/// Organization B fixture used only to prove cross-tenant denial.
/// </summary>
public sealed class TenantAccessTests
{
    private static readonly ActionDefinition ReadSale = new(
        Name: "read-sale",
        IsSensitive: false);

    private static readonly ActionDefinition ManageCatalog = new(
        Name: "manage-catalog",
        IsSensitive: true,
        RequiredPermission: Permission.ManageCatalog);

    private static readonly ActionDefinition ManageBranchSettings = new(
        Name: "manage-branch-settings",
        IsSensitive: true,
        RequiredPermission: Permission.ManageBranchSettings,
        RequiresElevatedOfflinePermission: true);

    private static UserAccount CreateUser(
        Guid organizationId,
        IEnumerable<Guid> branchScope,
        Permission permissions = Permission.None)
    {
        var role = new Role("test-role", permissions);
        return new UserAccount(Guid.NewGuid(), organizationId, branchScope, new[] { role });
    }

    [Fact]
    public void AuthorizedBranchAccess_Succeeds_AndRetainsScope()
    {
        var orgA = Guid.NewGuid();
        var branch1 = Guid.NewGuid();
        var user = CreateUser(orgA, new[] { branch1 });
        var service = new TenantAuthorizationService(new InMemoryAuditSink());

        var result = service.Authorize(
            user,
            new AccessRequest(orgA, branch1, ReadSale, IsOffline: false, Guid.NewGuid()));

        Assert.True(result.Allowed);
    }

    [Fact]
    public void CrossBranchAccess_IsDenied_WithoutRevealingExistence()
    {
        var orgA = Guid.NewGuid();
        var branch1 = Guid.NewGuid();
        var branch2 = Guid.NewGuid();
        var user = CreateUser(orgA, new[] { branch1 });
        var service = new TenantAuthorizationService(new InMemoryAuditSink());

        var result = service.Authorize(
            user,
            new AccessRequest(orgA, branch2, ReadSale, IsOffline: false, Guid.NewGuid()));

        Assert.False(result.Allowed);
        Assert.Equal("not-found", result.Reason);
    }

    [Fact]
    public void CrossOrganizationAccess_IsDenied_WithoutRevealingExistence()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var branch1 = Guid.NewGuid();
        var orgBBranch = Guid.NewGuid();
        var user = CreateUser(orgA, new[] { branch1 });
        var service = new TenantAuthorizationService(new InMemoryAuditSink());

        var result = service.Authorize(
            user,
            new AccessRequest(orgB, orgBBranch, ReadSale, IsOffline: false, Guid.NewGuid()));

        Assert.False(result.Allowed);
        Assert.Equal("not-found", result.Reason);
    }

    [Fact]
    public void RoleWithoutRequiredPermission_IsDenied()
    {
        var orgA = Guid.NewGuid();
        var branch1 = Guid.NewGuid();
        var user = CreateUser(orgA, new[] { branch1 }, Permission.ViewSales);
        var service = new TenantAuthorizationService(new InMemoryAuditSink());

        var result = service.Authorize(
            user,
            new AccessRequest(orgA, branch1, ManageCatalog, IsOffline: false, Guid.NewGuid()));

        Assert.False(result.Allowed);
        Assert.Equal("insufficient-permission", result.Reason);
    }

    [Fact]
    public void RevokedCredential_IsDenied_AndAuditable()
    {
        var orgA = Guid.NewGuid();
        var branch1 = Guid.NewGuid();
        var user = CreateUser(orgA, new[] { branch1 }, Permission.ManageCatalog);
        user.Revoke();
        var auditSink = new InMemoryAuditSink();
        var service = new TenantAuthorizationService(auditSink);
        var correlationId = Guid.NewGuid();

        var result = service.Authorize(
            user,
            new AccessRequest(orgA, branch1, ManageCatalog, IsOffline: false, correlationId));

        Assert.False(result.Allowed);
        Assert.Equal("credential-revoked", result.Reason);

        var entry = Assert.Single(auditSink.Entries);
        Assert.Equal(user.Id, entry.ActorId);
        Assert.Equal(orgA, entry.OrganizationId);
        Assert.Equal(branch1, entry.BranchId);
        Assert.Equal("manage-catalog", entry.Action);
        Assert.Equal("denied", entry.Outcome);
        Assert.Equal(correlationId, entry.CorrelationId);
    }

    [Fact]
    public void OfflineAdministrativeAction_WithStaleSnapshot_IsDenied_AndLabelsFreshness()
    {
        var orgA = Guid.NewGuid();
        var branch1 = Guid.NewGuid();
        var user = CreateUser(orgA, new[] { branch1 }, Permission.ManageBranchSettings);
        var now = DateTimeOffset.UtcNow;
        user.CacheAdminSnapshot(new AdminPermissionSnapshot(
            now - TimeSpan.FromHours(48),
            Permission.ManageBranchSettings));
        var service = new TenantAuthorizationService(
            new InMemoryAuditSink(),
            offlineFreshnessWindow: TimeSpan.FromHours(24),
            clock: () => now);

        var result = service.Authorize(
            user,
            new AccessRequest(orgA, branch1, ManageBranchSettings, IsOffline: true, Guid.NewGuid()));

        Assert.False(result.Allowed);
        Assert.Equal("offline-snapshot-stale", result.Reason);
        Assert.Equal(FreshnessLabel.Stale, result.Freshness);
    }

    [Fact]
    public void OfflineAdministrativeAction_WithFreshSnapshot_IsAllowed_AndLabelsFreshness()
    {
        var orgA = Guid.NewGuid();
        var branch1 = Guid.NewGuid();
        var user = CreateUser(orgA, new[] { branch1 }, Permission.ManageBranchSettings);
        var now = DateTimeOffset.UtcNow;
        user.CacheAdminSnapshot(new AdminPermissionSnapshot(
            now - TimeSpan.FromHours(1),
            Permission.ManageBranchSettings));
        var service = new TenantAuthorizationService(
            new InMemoryAuditSink(),
            offlineFreshnessWindow: TimeSpan.FromHours(24),
            clock: () => now);

        var result = service.Authorize(
            user,
            new AccessRequest(orgA, branch1, ManageBranchSettings, IsOffline: true, Guid.NewGuid()));

        Assert.True(result.Allowed);
        Assert.Equal(FreshnessLabel.Fresh, result.Freshness);
    }

    [Fact]
    public void SensitiveAction_ProducesAuditEntry_WithRequiredContext()
    {
        var orgA = Guid.NewGuid();
        var branch1 = Guid.NewGuid();
        var user = CreateUser(orgA, new[] { branch1 }, Permission.ManageCatalog);
        var auditSink = new InMemoryAuditSink();
        var service = new TenantAuthorizationService(auditSink);
        var correlationId = Guid.NewGuid();

        service.Authorize(
            user,
            new AccessRequest(orgA, branch1, ManageCatalog, IsOffline: false, correlationId));

        var entry = Assert.Single(auditSink.Entries);
        Assert.Equal(user.Id, entry.ActorId);
        Assert.Equal(orgA, entry.OrganizationId);
        Assert.Equal(branch1, entry.BranchId);
        Assert.Equal("manage-catalog", entry.Action);
        Assert.Equal("allowed", entry.Outcome);
        Assert.Equal(correlationId, entry.CorrelationId);
        Assert.True(entry.OccurredAtUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void NonSensitiveAction_DoesNotProduceAuditEntry()
    {
        var orgA = Guid.NewGuid();
        var branch1 = Guid.NewGuid();
        var user = CreateUser(orgA, new[] { branch1 });
        var auditSink = new InMemoryAuditSink();
        var service = new TenantAuthorizationService(auditSink);

        service.Authorize(
            user,
            new AccessRequest(orgA, branch1, ReadSale, IsOffline: false, Guid.NewGuid()));

        Assert.Empty(auditSink.Entries);
    }

    [Fact]
    public void InstallationReplacement_MintsNewIdentity_AndKeepsPriorRevocableAndTraceable()
    {
        var branchId = Guid.NewGuid();
        var identityService = new InstallationIdentityService();
        var original = identityService.Register(branchId);

        var replacement = identityService.ReplaceForHardwareChange(original);

        Assert.NotEqual(original.Id, replacement.Id);
        Assert.Equal(branchId, replacement.BranchId);
        Assert.Equal(original.Id, replacement.ReplacesInstallationId);
        Assert.True(original.IsRevoked);
        Assert.False(replacement.IsRevoked);
        Assert.Same(original, identityService.Find(original.Id));
    }
}
