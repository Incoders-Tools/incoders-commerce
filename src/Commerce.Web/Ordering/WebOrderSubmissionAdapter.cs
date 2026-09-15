using Commerce.BranchNode;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;

namespace Commerce.Web.Ordering;

/// <summary>
/// Thin web-facing surface forwarding into
/// <see cref="CloudOrderSubmissionService"/> — the actual claim-scoped
/// authorized call — proving the web channel is a real caller, not a test
/// double (Component Reuse Policy, mirrors <c>WebCatalogManagementAdapter</c>).
/// </summary>
public sealed class WebOrderSubmissionAdapter
{
    private readonly CloudOrderSubmissionService _submissionService;

    public WebOrderSubmissionAdapter(CloudOrderSubmissionService submissionService) => _submissionService = submissionService;

    public OrderSubmissionOutcome Submit(
        CloudTenantScope scope,
        CustomerOrderingAccess access,
        Guid orderId,
        Guid destinationBranchId,
        Guid actorId,
        IReadOnlyList<OrderLineSnapshot> lines,
        Guid correlationId,
        BranchSyncStore? destination,
        bool hasAvailableStock) =>
        _submissionService.Submit(scope, access, orderId, destinationBranchId, actorId, lines, correlationId, destination, hasAvailableStock);
}
