using Commerce.Application.Ordering;
using Commerce.BranchNode;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;

namespace Commerce.Cloud.Api.Ordering;

/// <summary>
/// Composes the customer's bound-access check with order acceptance/delivery
/// so a cross-organization or revoked-credential submission is denied before
/// any order is created (spec "Bound and Revocable Customer Access" applies
/// to order access, not only catalogue access).
/// </summary>
public sealed class CloudOrderSubmissionService
{
    private readonly CustomerCatalogAccessService _accessService;
    private readonly CloudOrderStore _orderStore;

    public CloudOrderSubmissionService(CustomerCatalogAccessService accessService, CloudOrderStore orderStore)
    {
        _accessService = accessService;
        _orderStore = orderStore;
    }

    public OrderSubmissionOutcome Submit(
        CloudTenantScope scope,
        CustomerOrderingAccess access,
        Guid orderId,
        Guid destinationBranchId,
        Guid actorId,
        IReadOnlyList<OrderLineSnapshot> lines,
        Guid correlationId,
        BranchSyncStore? destination,
        bool hasAvailableStock)
    {
        var accessResult = _accessService.Authorize(access, scope.OrganizationId, correlationId);
        if (!accessResult.Allowed)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, accessResult.Reason, Order: null, WasNewlyAccepted: false);
        }

        return _orderStore.Submit(scope, orderId, access.CustomerId, destinationBranchId, actorId, lines, correlationId, destination, hasAvailableStock);
    }
}
