using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.Application.Management;
using Commerce.Cloud.Api.Management;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;
using Commerce.Pos.Windows.Management;
using Commerce.Web.Management;

namespace Commerce.Integration;

/// <summary>
/// Covers design.md "Management authority": local (Windows/branch) and web
/// (cloud) adapters must invoke the same use case and produce identical
/// authorized outcomes. Both adapters here are real classes calling the
/// shared <see cref="CatalogManagementService"/> — not test doubles — so
/// parity is proven end-to-end rather than assumed.
/// </summary>
public sealed class ManagementParityTests
{
    private static Product NewProduct(Guid organizationId) => new(
        id: Guid.NewGuid(),
        organizationId: organizationId,
        name: "Original Name",
        categoryId: Guid.NewGuid(),
        defaultUnitId: Guid.NewGuid());

    private static UserAccount NewActor(Guid organizationId, Guid branchId, Permission permissions) => new(
        Guid.NewGuid(),
        organizationId,
        new[] { branchId },
        new[] { new Role("catalog-manager", permissions) });

    [Fact]
    public void LocalAndWebAdapters_ProduceIdenticalAllowedOutcome_ForSameAuthorizedActor()
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var actor = NewActor(organizationId, branchId, Permission.ManageCatalog);
        var product = NewProduct(organizationId);

        var localOutcome = new LocalCatalogManagementAdapter(NewManagementService())
            .RenameProduct(actor, product, branchId, "Renamed via local", isOffline: false, Guid.NewGuid());

        var webOutcome = new WebCatalogManagementAdapter(NewCloudAdapter())
            .RenameProduct(new CloudTenantScope(organizationId), actor, product, branchId, "Renamed via web", isOffline: false, Guid.NewGuid());

        Assert.Equal(ManagementOutcomeStatus.Allowed, localOutcome.Status);
        Assert.Equal(ManagementOutcomeStatus.Allowed, webOutcome.Status);
        Assert.Equal(localOutcome.Reason, webOutcome.Reason);
        Assert.Equal("Renamed via local", localOutcome.UpdatedProduct!.Name);
        Assert.Equal("Renamed via web", webOutcome.UpdatedProduct!.Name);
    }

    [Fact]
    public void LocalAndWebAdapters_DenyCrossBranchAccess_Identically()
    {
        var organizationId = Guid.NewGuid();
        var actorHomeBranchId = Guid.NewGuid();
        var foreignBranchId = Guid.NewGuid();
        var actor = NewActor(organizationId, actorHomeBranchId, Permission.ManageCatalog);
        var product = NewProduct(organizationId);

        var localOutcome = new LocalCatalogManagementAdapter(NewManagementService())
            .RenameProduct(actor, product, foreignBranchId, "Should not apply", isOffline: false, Guid.NewGuid());

        var webOutcome = new WebCatalogManagementAdapter(NewCloudAdapter())
            .RenameProduct(new CloudTenantScope(organizationId), actor, product, foreignBranchId, "Should not apply", isOffline: false, Guid.NewGuid());

        Assert.Equal(ManagementOutcomeStatus.Denied, localOutcome.Status);
        Assert.Equal(ManagementOutcomeStatus.Denied, webOutcome.Status);
        Assert.Equal("not-found", localOutcome.Reason);
        Assert.Equal(localOutcome.Reason, webOutcome.Reason);
        Assert.Null(localOutcome.UpdatedProduct);
        Assert.Null(webOutcome.UpdatedProduct);
    }

    [Fact]
    public void LocalAndWebAdapters_DenyCrossOrganizationScope_Identically()
    {
        var organizationId = Guid.NewGuid();
        var otherOrganizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var actor = NewActor(organizationId, branchId, Permission.ManageCatalog);
        var product = NewProduct(organizationId);

        var localOutcome = new LocalCatalogManagementAdapter(NewManagementService())
            .RenameProduct(actor, product, branchId, "Should not apply", isOffline: false, Guid.NewGuid());

        // Web scope claims a different organization than the actor's own grant
        // (e.g. a spoofed/misrouted claim) — must be denied, never trusted.
        var webOutcome = new WebCatalogManagementAdapter(NewCloudAdapter())
            .RenameProduct(new CloudTenantScope(otherOrganizationId), actor, product, branchId, "Should not apply", isOffline: false, Guid.NewGuid());

        Assert.Equal(ManagementOutcomeStatus.Allowed, localOutcome.Status);
        Assert.Equal(ManagementOutcomeStatus.Denied, webOutcome.Status);
        Assert.Equal("not-found", webOutcome.Reason);
        Assert.Null(webOutcome.UpdatedProduct);
    }

    [Fact]
    public void LocalAndWebAdapters_DenyInsufficientPermission_Identically()
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var actor = NewActor(organizationId, branchId, Permission.None);
        var product = NewProduct(organizationId);

        var localOutcome = new LocalCatalogManagementAdapter(NewManagementService())
            .RenameProduct(actor, product, branchId, "Should not apply", isOffline: false, Guid.NewGuid());

        var webOutcome = new WebCatalogManagementAdapter(NewCloudAdapter())
            .RenameProduct(new CloudTenantScope(organizationId), actor, product, branchId, "Should not apply", isOffline: false, Guid.NewGuid());

        Assert.Equal(ManagementOutcomeStatus.Denied, localOutcome.Status);
        Assert.Equal(ManagementOutcomeStatus.Denied, webOutcome.Status);
        Assert.Equal("insufficient-permission", localOutcome.Reason);
        Assert.Equal(localOutcome.Reason, webOutcome.Reason);
    }

    [Fact]
    public void SensitiveManagementDecision_IsAudited_RegardlessOfChannel()
    {
        var auditSink = new InMemoryAuditSink();
        var authService = new TenantAuthorizationService(auditSink);
        var managementService = new CatalogManagementService(authService);
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var actor = NewActor(organizationId, branchId, Permission.ManageCatalog);
        var product = NewProduct(organizationId);
        var correlationId = Guid.NewGuid();

        new LocalCatalogManagementAdapter(managementService)
            .RenameProduct(actor, product, branchId, "Audited rename", isOffline: false, correlationId);

        var entry = Assert.Single(auditSink.Entries);
        Assert.Equal("update-product-name", entry.Action);
        Assert.Equal("allowed", entry.Outcome);
        Assert.Equal(correlationId, entry.CorrelationId);
    }

    private static CatalogManagementService NewManagementService() =>
        new(new TenantAuthorizationService(new InMemoryAuditSink()));

    private static CloudCatalogManagementAdapter NewCloudAdapter() =>
        new(NewManagementService());
}
