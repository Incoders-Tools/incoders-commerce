using Commerce.Application.Time;
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
/// <see cref="IOrderStore.SubmitAsync"/> is reached, so an <see cref="Order"/>
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
/// BEFORE <see cref="IOrderStore.SubmitAsync"/>. A <c>NoEffectivePrice</c>
/// outcome on ANY line denies the WHOLE order with reason
/// <c>"no-effective-price"</c> — no partial acceptance.
/// </summary>
public sealed class CloudOrderSubmissionService
{
    private readonly CustomerCatalogAccessService _accessService;
    private readonly PostgresCustomerStore _customerStore;
    private readonly IOrderStore _orderStore;
    private readonly PostgresCatalogStore _catalogStore;
    private readonly PostgresPriceListStore _priceListStore;
    private readonly PostgresRateComponentStore _rateComponentStore;
    private readonly GuestVerificationService? _guestVerificationService;
    private readonly IBusinessClock _businessClock;

    /// <summary>
    /// <paramref name="rateComponentStore"/> is REQUIRED, not optional, even
    /// though a list with no published set composes to the identity: an
    /// optional default here would let a host silently resolve base prices as
    /// if they were finals, and the failure would be invisible until a set is
    /// published. Every call site declares it.
    /// </summary>
    public CloudOrderSubmissionService(
        CustomerCatalogAccessService accessService,
        PostgresCustomerStore customerStore,
        IOrderStore orderStore,
        PostgresCatalogStore catalogStore,
        PostgresPriceListStore priceListStore,
        PostgresRateComponentStore rateComponentStore,
        GuestVerificationService? guestVerificationService = null,
        IBusinessClock? businessClock = null)
    {
        _businessClock = businessClock ?? BusinessClock.System;
        _accessService = accessService;
        _customerStore = customerStore;
        _orderStore = orderStore;
        _catalogStore = catalogStore;
        _priceListStore = priceListStore;
        _rateComponentStore = rateComponentStore;
        _guestVerificationService = guestVerificationService;
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
        // four checks above and BEFORE _orderStore.SubmitAsync — REGISTERED path,
        // customer.DiscountPercentage applied.
        var (deniedReason, snapshots) = await ResolveLinesAsync(scope, lines, customer.DiscountPercentage, isCustomer: true, customer.PriceListId, ct);
        if (deniedReason is not null)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, deniedReason, Order: null, WasNewlyAccepted: false);
        }

        return await _orderStore.SubmitRegisteredAsync(
            scope, orderId, customerId, destinationBranchId, actorId, snapshots!, correlationId, destination, hasAvailableStock, ct);
    }

    /// <summary>
    /// Registered-customer SELF-SERVICE submission path (commerce-guest-
    /// ordering design.md "Registered customer order"; gap-closing follow-up
    /// unit found during Unit 6's independent verification — design.md
    /// documented `POST /customer/orders` and Unit 6's web client already
    /// called it, but no Phase 1-5 task ever built the endpoint or this
    /// method). The session IS the authorization: <paramref name="customerId"/>
    /// comes from the caller's authenticated `CustomerCookie` claim, never
    /// from a client-supplied field, so there is no access-credential to
    /// check and none is accepted here — <see cref="SubmitAsync"/>'s FIRST
    /// TWO checks (credential resolution, credential-to-customer binding)
    /// are structurally absent, not bypassed. The remaining checks — customer
    /// exists/visible under RLS, customer is enabled, then price resolution
    /// — run in the SAME relative order <see cref="SubmitAsync"/> already
    /// uses, so "customer-disabled" denies before any price is resolved,
    /// exactly like the credentialed path.
    /// </summary>
    public async Task<OrderSubmissionOutcome> SubmitForCustomerSessionAsync(
        CloudTenantScope scope,
        Guid customerId,
        Guid orderId,
        Guid destinationBranchId,
        Guid actorId,
        IReadOnlyList<SubmitOrderLine> lines,
        Guid correlationId,
        BranchSyncStore? destination,
        bool hasAvailableStock,
        CancellationToken ct)
    {
        var customer = await _customerStore.FindAsync(scope, customerId, ct);
        if (customer is null)
        {
            // Missing OR cross-organization (invisible under RLS) — same
            // reason SubmitAsync uses for an unresolvable customer.
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "not-found", Order: null, WasNewlyAccepted: false);
        }

        if (!customer.IsEnabled)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "customer-disabled", Order: null, WasNewlyAccepted: false);
        }

        // commerce-pricing-engine: resolution runs here, strictly AFTER the
        // checks above and BEFORE _orderStore.SubmitAsync — REGISTERED path,
        // customer.DiscountPercentage applied, identical to SubmitAsync.
        var (deniedReason, snapshots) = await ResolveLinesAsync(scope, lines, customer.DiscountPercentage, isCustomer: true, customer.PriceListId, ct);
        if (deniedReason is not null)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, deniedReason, Order: null, WasNewlyAccepted: false);
        }

        return await _orderStore.SubmitRegisteredAsync(
            scope, orderId, customerId, destinationBranchId, actorId, snapshots!, correlationId, destination, hasAvailableStock, ct);
    }

    /// <summary>
    /// Staff-entered order (staff-order-taking T2): a signed-in staff member with <c>TakeOrders</c> takes an order for
    /// a customer of the organization. The endpoint already authorized the CALLER, so — like
    /// <see cref="SubmitForCustomerSessionAsync"/> — there is no customer credential to check; the remaining checks
    /// (customer exists and is visible under RLS, customer enabled, then price resolution with the buyer's list and
    /// discount) run in the same order and with the same reasons. A resubmitted <paramref name="orderId"/> returns the
    /// stored order before any check runs, so a retry after a lost response gets the same outcome even if the
    /// customer or the prices changed in between. The taker in <paramref name="entry"/> is the caller, never a
    /// request field.
    /// </summary>
    public async Task<OrderSubmissionOutcome> SubmitForStaffAsync(
        CloudTenantScope scope,
        Guid customerId,
        Guid orderId,
        Guid destinationBranchId,
        StaffOrderEntry entry,
        IReadOnlyList<SubmitOrderLine> lines,
        Guid correlationId,
        BranchSyncStore? destination,
        bool hasAvailableStock,
        CancellationToken ct)
    {
        var existing = await _orderStore.FindAsync(scope, orderId, ct);
        if (existing is not null)
        {
            return new OrderSubmissionOutcome(
                OrderSubmissionOutcomeStatus.Accepted, OrderSubmissionReasons.ExistingOrder, existing, WasNewlyAccepted: false);
        }

        var customer = await _customerStore.FindAsync(scope, customerId, ct);
        if (customer is null)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "not-found", Order: null, WasNewlyAccepted: false);
        }

        if (!customer.IsEnabled)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "customer-disabled", Order: null, WasNewlyAccepted: false);
        }

        var (deniedReason, snapshots) = await ResolveLinesAsync(scope, lines, customer.DiscountPercentage, isCustomer: true, customer.PriceListId, ct);
        if (deniedReason is not null)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, deniedReason, Order: null, WasNewlyAccepted: false);
        }

        return await _orderStore.SubmitStaffAsync(
            scope, orderId, customerId, destinationBranchId, entry, snapshots!, correlationId, destination, hasAvailableStock, ct);
    }

    /// <summary>
    /// The price preview of <see cref="SubmitForStaffAsync"/>: the same customer checks and the same per-line
    /// resolution, but every line is reported (priced, or <c>no-effective-price</c>) instead of stopping at the first
    /// unpriced one, and nothing is stored. A quote with any unpriced line is <c>denied</c> with reason
    /// <c>no-effective-price</c>, exactly the outcome submitting it would have.
    /// </summary>
    public async Task<StaffOrderQuote> QuoteForStaffAsync(
        CloudTenantScope scope, Guid customerId, IReadOnlyList<SubmitOrderLine> lines, CancellationToken ct)
    {
        var customer = await _customerStore.FindAsync(scope, customerId, ct);
        if (customer is null)
        {
            return StaffOrderQuote.Denied("not-found", customerId);
        }

        if (!customer.IsEnabled)
        {
            return StaffOrderQuote.Denied("customer-disabled", customerId);
        }

        var pricing = await BuyerPricingAsync(scope, lines.Count > 0, isCustomer: true, customer.PriceListId, ct);
        var quoted = new List<StaffOrderQuoteLine>(lines.Count);
        foreach (var line in lines)
        {
            var resolved = await ResolveLineAsync(scope, pricing, line, customer.DiscountPercentage, ct);
            quoted.Add(resolved is null
                ? StaffOrderQuoteLine.Unpriced(line)
                : StaffOrderQuoteLine.Priced(resolved, pricing.ListName(resolved.PricedFromListId)));
        }

        var complete = quoted.All(l => l.Status == StaffOrderQuoteLine.PricedStatus);
        return new StaffOrderQuote(
            complete ? StaffOrderQuote.QuotedStatus : StaffOrderQuote.DeniedStatus,
            complete ? null : "no-effective-price",
            customerId,
            pricing.BuyerList?.Id,
            pricing.BuyerList?.Name,
            customer.DiscountPercentage,
            quoted,
            quoted.Sum(l => l.LineTotal ?? 0m));
    }

    /// <summary>
    /// Guest submission path (commerce-guest-ordering design.md "Guest
    /// submission path"). Structurally incapable of reaching
    /// <see cref="CustomerOrderingAccessService"/>/<see cref="PostgresCustomerStore"/>
    /// resolution or the credential check <see cref="SubmitAsync"/> performs
    /// — no such call exists anywhere in this method body. Price resolution
    /// reuses the SAME <see cref="ResolveLinesAsync"/> the registered path
    /// uses, with <c>discountPercentage: null</c> (guest-ordering spec.md
    /// "Guest Price Resolution"): list price, never a discount. The
    /// confirmed verification is handed to <see cref="IOrderStore.SubmitAsync"/>
    /// (persist-web-orders), which spends it in the SAME database transaction
    /// that stores the order: a failed insert leaves it usable, and a pricing
    /// denial (<c>"no-effective-price"</c>) or an unknown destination branch
    /// returns before any consumption, so the guest's one-time verification is
    /// not burned by a failure that was never their fault (public-order-surface
    /// spec.md "Guest Verification Gate Before Admission").
    /// </summary>
    public async Task<OrderSubmissionOutcome> SubmitGuestAsync(
        CloudTenantScope scope,
        Guid orderId,
        Guid verificationId,
        GuestContact guestContact,
        Guid destinationBranchId,
        IReadOnlyList<SubmitOrderLine> lines,
        Guid correlationId,
        BranchSyncStore? destination,
        bool hasAvailableStock,
        CancellationToken ct)
    {
        if (_guestVerificationService is null)
        {
            throw new InvalidOperationException(
                $"{nameof(SubmitGuestAsync)} requires a {nameof(GuestVerificationService)} to be supplied to this {nameof(CloudOrderSubmissionService)}.");
        }

        var (deniedReason, snapshots) = await ResolveLinesAsync(scope, lines, discountPercentage: null, isCustomer: false, customerPriceListId: null, ct);
        if (deniedReason is not null)
        {
            // Pricing denies BEFORE the verification is ever consumed.
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, deniedReason, Order: null, WasNewlyAccepted: false);
        }

        // An unconfirmed / expired / already-consumed / mismatched-contact verification denies as
        // "verification-invalid" inside the store; no order is stored and nothing is spent.
        var verification = _guestVerificationService.ConsumptionFor(
            verificationId, guestContact.DocumentId, guestContact.ContactAddress);

        return await _orderStore.SubmitAsync(
            scope, orderId, OrderOrigin.Guest, customerId: null, guestContact, destinationBranchId,
            OrderActors.PublicGuest, snapshots!, correlationId, destination, hasAvailableStock, verification, ct);
    }

    /// <summary>
    /// The money rule, written ONCE and shared by <see cref="SubmitAsync"/>
    /// (REGISTERED, real discount) and <see cref="SubmitGuestAsync"/> (GUEST,
    /// <c>discountPercentage: null</c>) — the only difference between the two
    /// paths (commerce-guest-ordering design.md "Guest submission path").
    /// Resolve → a <see cref="PriceResolutionOutcome.NoEffectivePrice"/> on
    /// ANY line denies the WHOLE order with reason <c>"no-effective-price"</c>
    /// (no partial acceptance) → catalog lookup → snapshot.
    /// </summary>
    private PriceListPorts PortsOf(CloudTenantScope scope, Guid priceListId) => new(
        priceListId,
        new PostgresEffectivePriceSource(_priceListStore, scope, priceListId),
        new PostgresRateComponentSource(_rateComponentStore, scope, priceListId));

    private async Task<(string? DeniedReason, List<OrderLineSnapshot>? Snapshots)> ResolveLinesAsync(
        CloudTenantScope scope, IReadOnlyList<SubmitOrderLine> lines, decimal? discountPercentage,
        bool isCustomer, Guid? customerPriceListId, CancellationToken ct)
    {
        var pricing = await BuyerPricingAsync(scope, lines.Count > 0, isCustomer, customerPriceListId, ct);
        var snapshots = new List<OrderLineSnapshot>(lines.Count);
        foreach (var line in lines)
        {
            // One bad line poisons the whole order: deny before any further
            // line is resolved and nothing is ever passed to Submit.
            var snapshot = await ResolveLineAsync(scope, pricing, line, discountPercentage, ct);
            if (snapshot is null)
            {
                return ("no-effective-price", null);
            }

            snapshots.Add(snapshot);
        }

        return (null, snapshots);
    }

    /// <summary>The price lists one buyer's order resolves against, chosen once per order.</summary>
    private sealed record BuyerPricing(
        PricingResolutionService? Service, PriceListRecord? BuyerList, PriceListRecord? DefaultList, DateOnly EffectiveOn)
    {
        public string? ListName(Guid? listId) =>
            listId is null ? null
            : listId == BuyerList?.Id ? BuyerList!.Name
            : listId == DefaultList?.Id ? DefaultList!.Name
            : null;
    }

    private async Task<BuyerPricing> BuyerPricingAsync(
        CloudTenantScope scope, bool hasLines, bool isCustomer, Guid? customerPriceListId, CancellationToken ct)
    {
        // No default price list at all is treated the same as zero effective
        // rows for every line — never a silent 0m fallback. A zero-line
        // order never touches pricing at all (pre-existing regression-guard
        // tests submit empty-line orders against schemas that predate this
        // unit).
        var effectiveOn = _businessClock.Today;
        if (!hasLines)
        {
            return new BuyerPricing(null, null, null, effectiveOn);
        }

        // customer-price-lists T2: the list is chosen by the BUYER (the customer's own list, else the
        // organization's default customer list, else the default list; a guest gets the default list), never
        // by the channel the order came through.
        var buyerPriceList = await _priceListStore.ResolveBuyerPriceListAsync(scope, isCustomer, customerPriceListId, ct);
        // customer-price-lists T6: a presentation the buyer's list does not price falls back to the branch
        // default list (with that list's own composition); the line records which list priced it.
        var defaultList = buyerPriceList is null ? null : await _priceListStore.FindDefaultPriceListAsync(scope, ct);
        var service = buyerPriceList is null
            ? null
            // commerce-price-composition slice 2: the SAME price
            // list binds both ports, so the entry's base price and the
            // components composed onto it can never come from two lists.
            : new PricingResolutionService(
                PortsOf(scope, buyerPriceList.Id),
                defaultList is null ? null : PortsOf(scope, defaultList.Id));
        return new BuyerPricing(service, buyerPriceList, defaultList, effectiveOn);
    }

    /// <summary>
    /// Resolve → catalog lookup → snapshot for ONE line; <see langword="null"/> when the line has no effective price
    /// (a <see cref="PriceResolutionOutcome.NoEffectivePrice"/>, or a presentation/product the catalog does not have).
    /// </summary>
    private async Task<OrderLineSnapshot?> ResolveLineAsync(
        CloudTenantScope scope, BuyerPricing pricing, SubmitOrderLine line, decimal? discountPercentage, CancellationToken ct)
    {
        var resolution = pricing.Service is null
            ? new PriceResolutionOutcome.NoEffectivePrice(line.PresentationId, pricing.EffectiveOn)
            : await pricing.Service.ResolveAsync(line.PresentationId, line.Quantity, discountPercentage, pricing.EffectiveOn, ct);

        if (resolution is not PriceResolutionOutcome.Resolved resolvedPrice)
        {
            return null;
        }

        var presentationRecord = await _catalogStore.FindPresentationAsync(scope, line.PresentationId, ct);
        if (presentationRecord is null)
        {
            return null;
        }

        var productRecord = await _catalogStore.FindProductAsync(scope, presentationRecord.ProductId, ct);
        if (productRecord is null)
        {
            return null;
        }

        return OrderSnapshotFactory.Snapshot(productRecord.ToDomain(), presentationRecord.ToDomain(), line.Quantity, resolvedPrice);
    }
}
