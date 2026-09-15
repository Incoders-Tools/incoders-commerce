using Commerce.Application.Management;
using Commerce.Cloud.Api.Management;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;

namespace Commerce.Web.Management;

/// <summary>
/// Thin web-facing surface that forwards straight into the cloud-side
/// <see cref="CloudCatalogManagementAdapter"/> — the actual claim-scoped
/// authorized call into the shared management contract. This project holds
/// no authorization or business-outcome logic of its own; it exists to prove
/// the web channel is a real caller of the same shared contract, not a test
/// double, per the Component Reuse Policy.
/// </summary>
public sealed class WebCatalogManagementAdapter
{
    private readonly CloudCatalogManagementAdapter _cloudAdapter;

    public WebCatalogManagementAdapter(CloudCatalogManagementAdapter cloudAdapter)
    {
        _cloudAdapter = cloudAdapter;
    }

    public ManagementOutcome RenameProduct(
        CloudTenantScope scope,
        UserAccount actor,
        Product product,
        Guid targetBranchId,
        string newName,
        bool isOffline,
        Guid correlationId) =>
        _cloudAdapter.RenameProduct(scope, actor, product, targetBranchId, newName, isOffline, correlationId);
}
