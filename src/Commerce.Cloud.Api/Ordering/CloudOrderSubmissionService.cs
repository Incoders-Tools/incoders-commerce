using Commerce.Application.Ordering;
using Commerce.BranchNode;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;

namespace Commerce.Cloud.Api.Ordering;

/// <summary>
/// Composes the customer's bound-access check with order acceptance/delivery
/// so a cross-organization or revoked-credential submission is denied before
/// any order is created (spec "Bound and Revocable Customer Access" applies
/// to order access, not only catalogue access).
///
/// commerce-customer-identity design.md "Order.CustomerId referential
/// integrity, given no orders table exists": every denial happens BEFORE
/// <see cref="CloudOrderStore.Submit"/> is reached, so an <see cref="Order"/>
/// with a dangling/unbound <c>CustomerId</c> is never constructed. Order of
/// checks: (1) credential resolves to an enabled row in this organization,
/// (2) that row is actually bound to the customer id the caller declared
/// (denies with the SAME "not-found" reason as an unknown credential — no
/// probe signal for "this credential exists but isn't yours"), (3) the
/// declared customer exists, is visible under RLS, and is enabled.
/// </summary>
public sealed class CloudOrderSubmissionService
{
    private readonly CustomerCatalogAccessService _accessService;
    private readonly PostgresCustomerStore _customerStore;
    private readonly CloudOrderStore _orderStore;

    public CloudOrderSubmissionService(
        CustomerCatalogAccessService accessService, PostgresCustomerStore customerStore, CloudOrderStore orderStore)
    {
        _accessService = accessService;
        _customerStore = customerStore;
        _orderStore = orderStore;
    }

    public async Task<OrderSubmissionOutcome> SubmitAsync(
        CloudTenantScope scope,
        Guid customerId,
        Guid accessCredential,
        Guid orderId,
        Guid destinationBranchId,
        Guid actorId,
        IReadOnlyList<OrderLineSnapshot> lines,
        Guid correlationId,
        BranchSyncStore? destination,
        bool hasAvailableStock,
        CancellationToken ct)
    {
        var accessResult = await _accessService.AuthorizeAsync(scope.OrganizationId, accessCredential, correlationId, ct);
        if (!accessResult.Allowed)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, accessResult.Reason, Order: null, WasNewlyAccepted: false);
        }

        if (accessResult.CustomerId != customerId)
        {
            // The credential is real and enabled, but bound to a DIFFERENT
            // customer than the one the caller declared. Same reason as an
            // unknown credential: a probe cannot learn "this credential
            // exists but belongs to someone else".
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "not-found", Order: null, WasNewlyAccepted: false);
        }

        var customer = await _customerStore.FindAsync(scope, customerId, ct);
        if (customer is null)
        {
            // Missing OR cross-organization (invisible under RLS) — identical
            // outcome, identical reason.
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "not-found", Order: null, WasNewlyAccepted: false);
        }

        if (!customer.IsEnabled)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "customer-disabled", Order: null, WasNewlyAccepted: false);
        }

        return _orderStore.Submit(scope, orderId, customerId, destinationBranchId, actorId, lines, correlationId, destination, hasAvailableStock);
    }
}
