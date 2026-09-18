using Commerce.Application.Ordering;
using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Pricing;
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
///
/// commerce-pricing-engine design.md "OrderLineSnapshot extension and where
/// resolution runs": a fifth check, price resolution via
/// <see cref="PricingResolutionService"/>, runs strictly AFTER the four
/// checks above (the customer's <c>DiscountPercentage</c> is only known
/// after the customer read, and an unauthorized caller must not be able to
/// probe catalog/price existence through a reason-code difference) and
/// BEFORE <see cref="CloudOrderStore.Submit"/>. A <c>NoEffectivePrice</c>
/// outcome on ANY line denies the WHOLE order with reason
/// <c>"no-effective-price"</c> — no partial acceptance.
/// </summary>
public sealed class CloudOrderSubmissionService
{
    private readonly CustomerCatalogAccessService _accessService;
    private readonly PostgresCustomerStore _customerStore;
    private readonly CloudOrderStore _orderStore;
    private readonly PostgresCatalogStore _catalogStore;
    private readonly PostgresPriceListStore _priceListStore;

    public CloudOrderSubmissionService(
        CustomerCatalogAccessService accessService,
        PostgresCustomerStore customerStore,
        CloudOrderStore orderStore,
        PostgresCatalogStore catalogStore,
        PostgresPriceListStore priceListStore)
    {
        _accessService = accessService;
        _customerStore = customerStore;
        _orderStore = orderStore;
        _catalogStore = catalogStore;
        _priceListStore = priceListStore;
    }

    public async Task<OrderSubmissionOutcome> SubmitAsync(
        CloudTenantScope scope,
        Guid customerId,
        Guid accessCredential,
        Guid orderId,
        Guid destinationBranchId,
        Guid actorId,
        IReadOnlyList<SubmitOrderLine> lines,
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

        // commerce-pricing-engine: resolution runs here, strictly AFTER the
        // four checks above and BEFORE _orderStore.Submit. No default price
        // list at all is treated the same as zero effective rows for every
        // line — never a silent 0m fallback. A zero-line order never touches
        // pricing at all (pre-existing regression-guard tests submit
        // empty-line orders against schemas that predate this unit).
        var effectiveOn = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        PricingResolutionService? pricingService = null;
        if (lines.Count > 0)
        {
            var defaultPriceList = await _priceListStore.FindDefaultPriceListAsync(scope, ct);
            pricingService = defaultPriceList is null
                ? null
                : new PricingResolutionService(new PostgresEffectivePriceSource(_priceListStore, scope, defaultPriceList.Id));
        }

        var snapshots = new List<OrderLineSnapshot>(lines.Count);
        foreach (var line in lines)
        {
            var resolution = pricingService is null
                ? new PriceResolutionOutcome.NoEffectivePrice(line.PresentationId, effectiveOn)
                : await pricingService.ResolveAsync(line.PresentationId, line.Quantity, customer.DiscountPercentage, effectiveOn, ct);

            if (resolution is not PriceResolutionOutcome.Resolved resolvedPrice)
            {
                // One bad line poisons the whole order: deny before any
                // snapshot is built and nothing is ever passed to Submit.
                return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "no-effective-price", Order: null, WasNewlyAccepted: false);
            }

            var presentationRecord = await _catalogStore.FindPresentationAsync(scope, line.PresentationId, ct);
            if (presentationRecord is null)
            {
                return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "no-effective-price", Order: null, WasNewlyAccepted: false);
            }

            var productRecord = await _catalogStore.FindProductAsync(scope, presentationRecord.ProductId, ct);
            if (productRecord is null)
            {
                return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "no-effective-price", Order: null, WasNewlyAccepted: false);
            }

            snapshots.Add(OrderSnapshotFactory.Snapshot(productRecord.ToDomain(), presentationRecord.ToDomain(), line.Quantity, resolvedPrice));
        }

        return _orderStore.Submit(scope, orderId, customerId, destinationBranchId, actorId, snapshots, correlationId, destination, hasAvailableStock);
    }
}
