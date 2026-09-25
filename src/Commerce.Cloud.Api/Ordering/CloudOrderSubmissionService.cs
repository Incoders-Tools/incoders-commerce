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
    private readonly PostgresRateComponentStore _rateComponentStore;
    private readonly GuestVerificationService? _guestVerificationService;

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
        CloudOrderStore orderStore,
        PostgresCatalogStore catalogStore,
        PostgresPriceListStore priceListStore,
        PostgresRateComponentStore rateComponentStore,
        GuestVerificationService? guestVerificationService = null)
    {
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
        // four checks above and BEFORE _orderStore.Submit — REGISTERED path,
        // customer.DiscountPercentage applied.
        var (deniedReason, snapshots) = await ResolveLinesAsync(scope, lines, customer.DiscountPercentage, ct);
        if (deniedReason is not null)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, deniedReason, Order: null, WasNewlyAccepted: false);
        }

        return _orderStore.Submit(scope, orderId, customerId, destinationBranchId, actorId, snapshots!, correlationId, destination, hasAvailableStock);
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
        // checks above and BEFORE _orderStore.Submit — REGISTERED path,
        // customer.DiscountPercentage applied, identical to SubmitAsync.
        var (deniedReason, snapshots) = await ResolveLinesAsync(scope, lines, customer.DiscountPercentage, ct);
        if (deniedReason is not null)
        {
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, deniedReason, Order: null, WasNewlyAccepted: false);
        }

        return _orderStore.Submit(scope, orderId, customerId, destinationBranchId, actorId, snapshots!, correlationId, destination, hasAvailableStock);
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
    /// confirmed verification is consumed via
    /// <see cref="GuestVerificationService.TryConsumeAsync"/> IMMEDIATELY
    /// BEFORE <see cref="CloudOrderStore.Submit"/> — a pricing denial on any
    /// line (<c>"no-effective-price"</c>) returns before that consumption
    /// call is ever made, so the guest's one-time verification is not burned
    /// by a denial that was never their fault (public-order-surface spec.md
    /// "Guest Verification Gate Before Admission").
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

        var (deniedReason, snapshots) = await ResolveLinesAsync(scope, lines, discountPercentage: null, ct);
        if (deniedReason is not null)
        {
            // Pricing denies BEFORE the verification is ever consumed.
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, deniedReason, Order: null, WasNewlyAccepted: false);
        }

        var consumed = await _guestVerificationService.TryConsumeAsync(
            verificationId, guestContact.DocumentId, guestContact.ContactAddress, orderId, ct);
        if (!consumed)
        {
            // Unconfirmed / expired / already-consumed / mismatched-contact —
            // every failure branch denies identically; no order is stored.
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "verification-invalid", Order: null, WasNewlyAccepted: false);
        }

        return _orderStore.Submit(
            scope, orderId, OrderOrigin.Guest, customerId: null, guestContact, destinationBranchId,
            OrderActors.PublicGuest, snapshots!, correlationId, destination, hasAvailableStock);
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
    private async Task<(string? DeniedReason, List<OrderLineSnapshot>? Snapshots)> ResolveLinesAsync(
        CloudTenantScope scope, IReadOnlyList<SubmitOrderLine> lines, decimal? discountPercentage, CancellationToken ct)
    {
        // No default price list at all is treated the same as zero effective
        // rows for every line — never a silent 0m fallback. A zero-line
        // order never touches pricing at all (pre-existing regression-guard
        // tests submit empty-line orders against schemas that predate this
        // unit).
        var effectiveOn = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        PricingResolutionService? pricingService = null;
        if (lines.Count > 0)
        {
            var defaultPriceList = await _priceListStore.FindDefaultPriceListAsync(scope, ct);
            pricingService = defaultPriceList is null
                ? null
                // commerce-price-composition slice 2: the SAME default price
                // list binds both ports, so the entry's base price and the
                // components composed onto it can never come from two lists.
                : new PricingResolutionService(
                    new PostgresEffectivePriceSource(_priceListStore, scope, defaultPriceList.Id),
                    new PostgresRateComponentSource(_rateComponentStore, scope, defaultPriceList.Id));
        }

        var snapshots = new List<OrderLineSnapshot>(lines.Count);
        foreach (var line in lines)
        {
            var resolution = pricingService is null
                ? new PriceResolutionOutcome.NoEffectivePrice(line.PresentationId, effectiveOn)
                : await pricingService.ResolveAsync(line.PresentationId, line.Quantity, discountPercentage, effectiveOn, ct);

            if (resolution is not PriceResolutionOutcome.Resolved resolvedPrice)
            {
                // One bad line poisons the whole order: deny before any
                // snapshot is built and nothing is ever passed to Submit.
                return ("no-effective-price", null);
            }

            var presentationRecord = await _catalogStore.FindPresentationAsync(scope, line.PresentationId, ct);
            if (presentationRecord is null)
            {
                return ("no-effective-price", null);
            }

            var productRecord = await _catalogStore.FindProductAsync(scope, presentationRecord.ProductId, ct);
            if (productRecord is null)
            {
                return ("no-effective-price", null);
            }

            snapshots.Add(OrderSnapshotFactory.Snapshot(productRecord.ToDomain(), presentationRecord.ToDomain(), line.Quantity, resolvedPrice));
        }

        return (null, snapshots);
    }
}
