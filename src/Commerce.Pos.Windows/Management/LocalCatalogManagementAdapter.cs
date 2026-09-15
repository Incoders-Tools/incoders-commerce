using Commerce.Application.Management;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;

namespace Commerce.Pos.Windows.Management;

/// <summary>
/// Local (same-machine, branch/Windows) path into the shared management
/// contract. The actor is already authenticated on this branch node, so the
/// target organization is read directly from the actor rather than a
/// separately derived scope — this is the only structural difference from
/// the web path; the authorization/business outcome is identical because
/// both paths call the same <see cref="CatalogManagementService"/>. This
/// adapter contains no authorization or business-outcome logic of its own.
/// </summary>
public sealed class LocalCatalogManagementAdapter
{
    private readonly CatalogManagementService _managementService;

    public LocalCatalogManagementAdapter(CatalogManagementService managementService)
    {
        _managementService = managementService;
    }

    public ManagementOutcome RenameProduct(
        UserAccount actor,
        Product product,
        Guid targetBranchId,
        string newName,
        bool isOffline,
        Guid correlationId) =>
        _managementService.RenameProduct(
            actor,
            product,
            new ManagementRequest(actor.OrganizationId, targetBranchId, product.Id, newName, isOffline, correlationId));
}
