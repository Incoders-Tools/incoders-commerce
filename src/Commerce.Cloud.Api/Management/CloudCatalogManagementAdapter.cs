using Commerce.Application.Management;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;

namespace Commerce.Cloud.Api.Management;

/// <summary>
/// Web/cloud path into the shared management contract. Scope is derived from
/// authenticated cloud credentials (<see cref="CloudTenantScope"/>), never
/// from a caller-submitted organization ID, mirroring <see cref="CloudSyncReceiver"/>
/// (Unit 3). This adapter contains no authorization or business-outcome logic
/// of its own — it only forwards to <see cref="CatalogManagementService"/>.
/// </summary>
public sealed class CloudCatalogManagementAdapter
{
    private readonly CatalogManagementService _managementService;

    public CloudCatalogManagementAdapter(CatalogManagementService managementService)
    {
        _managementService = managementService;
    }

    public ManagementOutcome RenameProduct(
        CloudTenantScope scope,
        UserAccount actor,
        Product product,
        Guid targetBranchId,
        string newName,
        bool isOffline,
        Guid correlationId) =>
        _managementService.RenameProduct(
            actor,
            product,
            new ManagementRequest(scope.OrganizationId, targetBranchId, product.Id, newName, isOffline, correlationId));
}
