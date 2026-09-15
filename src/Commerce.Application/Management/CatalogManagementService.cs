using Commerce.Application.Access;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;

namespace Commerce.Application.Management;

/// <summary>
/// The single shared management contract. Both the local (branch/Windows) and
/// web (cloud) adapters MUST call this instead of reimplementing per-channel
/// authorization or business-outcome logic (Component Reuse Policy). It
/// reuses <see cref="TenantAuthorizationService"/> (Unit 2) for the entire
/// authorization/audit decision — this class contains no authorization logic
/// of its own.
/// </summary>
public sealed class CatalogManagementService
{
    private static readonly ActionDefinition UpdateProductName = new(
        Name: "update-product-name",
        IsSensitive: true,
        RequiredPermission: Permission.ManageCatalog,
        RequiresElevatedOfflinePermission: true);

    private readonly TenantAuthorizationService _authorizationService;

    public CatalogManagementService(TenantAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    public ManagementOutcome RenameProduct(UserAccount actor, Product product, ManagementRequest request)
    {
        var accessResult = _authorizationService.Authorize(
            actor,
            new AccessRequest(
                request.TargetOrganizationId,
                request.TargetBranchId,
                UpdateProductName,
                request.IsOffline,
                request.CorrelationId));

        if (!accessResult.Allowed)
        {
            return new ManagementOutcome(ManagementOutcomeStatus.Denied, accessResult.Reason, UpdatedProduct: null);
        }

        var updatedProduct = new Product(
            product.Id,
            product.OrganizationId,
            request.NewName,
            product.CategoryId,
            product.DefaultUnitId,
            product.VerticalExtension);

        return new ManagementOutcome(ManagementOutcomeStatus.Allowed, accessResult.Reason, updatedProduct);
    }
}
