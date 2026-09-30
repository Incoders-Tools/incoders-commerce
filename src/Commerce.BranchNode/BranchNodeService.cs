using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.Domain.CashSessions;
using Commerce.Domain.Discounts;
using Commerce.Domain.Identity;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;

namespace Commerce.BranchNode;

/// <summary>
/// Orchestrates branch-owned sale commits, retry/ACK, and conflict review.
/// Reuses <see cref="TenantAuthorizationService"/> for conflict-review
/// authorization and <see cref="IAuditSink"/> for the resulting audit trail
/// (Component Reuse Policy — do not reimplement authorization/audit here).
/// </summary>
public sealed class BranchNodeService
{
    private static readonly ActionDefinition ReviewSyncConflict = new(
        Name: "review-sync-conflict",
        IsSensitive: true,
        RequiredPermission: Permission.ManageBranchSettings);

    private readonly BranchSyncStore _store;
    private readonly TenantAuthorizationService _authorizationService;
    private readonly IAuditSink _auditSink;
    private readonly Func<DateTimeOffset> _clock;

    public BranchNodeService(
        BranchSyncStore store,
        TenantAuthorizationService authorizationService,
        IAuditSink auditSink,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _authorizationService = authorizationService;
        _auditSink = auditSink;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public BranchOutboxCommitResult CompleteOfflineSale(
        Guid organizationId,
        Guid branchId,
        Guid actorId,
        Guid saleId,
        decimal totalAmount,
        Guid operationId,
        Guid correlationId,
        Guid? customerId = null,
        SaleTender? tender = null)
    {
        var occurredAtUtc = _clock();
        var cashSessionId = _store.GetOpenCashSession()?.SessionId;
        var payload = new SalePayloadV1(
            saleId, totalAmount, "Manual", occurredAtUtc, Lines: [], CustomerId: customerId, Tender: tender,
            CashSessionId: cashSessionId);
        var envelope = new SyncEnvelope(
            OperationId: operationId,
            ContractVersion: 1,
            OrganizationId: organizationId,
            BranchId: branchId,
            AggregateId: saleId,
            AggregateVersion: 1,
            ActorId: actorId,
            CorrelationId: correlationId,
            OccurredAtUtc: occurredAtUtc,
            PayloadKind: "sale",
            Payload: SyncPayloadCodec.Serialize(payload));
        var effect = new SaleEffect(
            saleId, branchId, totalAmount, occurredAtUtc, CustomerId: customerId, Tender: tender, CashSessionId: cashSessionId);

        return _store.CommitSaleAtomically(envelope, effect, requireOpenCashSession: true);
    }

    /// <summary>
    /// The scan-composed sale counterpart to <see cref="CompleteOfflineSale"/>
    /// (commerce-pricing-engine design.md "POS: two explicit buttons, not a
    /// mode toggle"): same envelope/effect shape, `SaleKind = "Scanned"`, and
    /// its lines committed atomically alongside the sale effect and outbox
    /// row via <see cref="BranchSyncStore.CommitScannedSaleAtomically"/>.
    /// <paramref name="customerId"/> is the optional customer picked at the
    /// POS (null = walk-in); it is stored on the sale effect and carried in the
    /// outbox payload. <paramref name="totalAmount"/> is the FINAL total after
    /// every discount; <paramref name="saleDiscount"/> is the whole-sale
    /// discount and <paramref name="discountAuthorization"/> proves who
    /// authorized the discounts (both null when nothing was discounted). Line
    /// discounts travel on <paramref name="lines"/> themselves.
    /// <paramref name="tender"/> is how the customer paid (null on callers that predate tenders).
    /// </summary>
    public BranchOutboxCommitResult CompleteScannedSale(
        Guid organizationId,
        Guid branchId,
        Guid actorId,
        Guid saleId,
        IReadOnlyList<Commerce.Domain.Sync.SaleLine> lines,
        decimal totalAmount,
        Guid operationId,
        Guid correlationId,
        Guid? customerId = null,
        SaleDiscount? saleDiscount = null,
        DiscountAuthorization? discountAuthorization = null,
        SaleTender? tender = null)
    {
        var occurredAtUtc = _clock();
        var cashSessionId = _store.GetOpenCashSession()?.SessionId;
        var payload = new SalePayloadV1(
            saleId, totalAmount, "Scanned", occurredAtUtc, lines, customerId,
            saleDiscount?.Percent, saleDiscount?.Amount, discountAuthorization, tender, cashSessionId);
        var envelope = new SyncEnvelope(
            OperationId: operationId,
            ContractVersion: 1,
            OrganizationId: organizationId,
            BranchId: branchId,
            AggregateId: saleId,
            AggregateVersion: 1,
            ActorId: actorId,
            CorrelationId: correlationId,
            OccurredAtUtc: occurredAtUtc,
            PayloadKind: "sale",
            Payload: SyncPayloadCodec.Serialize(payload));
        var effect = new SaleEffect(
            saleId, branchId, totalAmount, occurredAtUtc, CustomerId: customerId,
            SaleDiscountPercent: saleDiscount?.Percent, SaleDiscountAmount: saleDiscount?.Amount,
            DiscountAuthorization: discountAuthorization, Tender: tender, CashSessionId: cashSessionId);

        return _store.CommitScannedSaleAtomically(envelope, effect, lines, requireOpenCashSession: true);
    }

    /// <summary>The terminal's open cash session, or null (pos-cash-session).</summary>
    public CashSession? GetOpenCashSession() => _store.GetOpenCashSession();

    public CashSession? GetCashSession(Guid sessionId) => _store.GetCashSession(sessionId);

    /// <summary>Live totals and expected cash of a session, computed from its sales' recorded tenders.</summary>
    public CashSessionSummary? GetCashSessionSummary(Guid sessionId) => _store.GetCashSessionSummary(sessionId);

    /// <summary>
    /// Opens the terminal's cash session with its opening float and queues the
    /// <c>cash-session.opened</c> envelope in the same transaction. Refused when
    /// the float is not zero-or-more with at most two decimals, or when a
    /// session is already open. Never reads the device credential.
    /// </summary>
    public CashSessionOpenResult OpenCashSession(
        Guid organizationId, Guid branchId, Guid operatorId, decimal openingFloat, Guid correlationId)
    {
        if (!CashSessionMath.IsValidAmount(openingFloat))
        {
            return new CashSessionOpenResult(CashSessionOpenOutcome.InvalidFloat, null);
        }

        var openedAtUtc = _clock();
        var session = new CashSession(Guid.NewGuid(), organizationId, branchId, operatorId, openedAtUtc, openingFloat);
        var envelope = new SyncEnvelope(
            OperationId: Guid.NewGuid(),
            ContractVersion: 1,
            OrganizationId: organizationId,
            BranchId: branchId,
            AggregateId: session.SessionId,
            AggregateVersion: 1,
            ActorId: operatorId,
            CorrelationId: correlationId,
            OccurredAtUtc: openedAtUtc,
            PayloadKind: CashSessionPayloadKinds.Opened,
            Payload: SyncPayloadCodec.Serialize(
                new CashSessionOpenedPayloadV1(session.SessionId, operatorId, openingFloat, openedAtUtc)));

        return _store.OpenCashSession(session, envelope);
    }

    /// <summary>
    /// Closes the session: totals are computed from its sales, recorded with the
    /// counted cash and the difference, and the <c>cash-session.closed</c>
    /// envelope is queued in the same transaction. Refused for an invalid counted
    /// amount, an unknown session, or a session that is already closed.
    /// </summary>
    public CashSessionCloseResult CloseCashSession(
        Guid sessionId, Guid operatorId, decimal countedCash, Guid correlationId)
    {
        if (!CashSessionMath.IsValidAmount(countedCash))
        {
            return new CashSessionCloseResult(CashSessionCloseOutcome.InvalidCountedCash, _store.GetCashSession(sessionId));
        }

        var closedAtUtc = _clock();
        return _store.CloseCashSession(sessionId, operatorId, countedCash, closedAtUtc, closed =>
        {
            var closure = closed.Closure!;
            var summary = closure.Summary;
            return new SyncEnvelope(
                OperationId: Guid.NewGuid(),
                ContractVersion: 1,
                OrganizationId: closed.OrganizationId,
                BranchId: closed.BranchId,
                AggregateId: closed.SessionId,
                AggregateVersion: 2,
                ActorId: operatorId,
                CorrelationId: correlationId,
                OccurredAtUtc: closedAtUtc,
                PayloadKind: CashSessionPayloadKinds.Closed,
                Payload: SyncPayloadCodec.Serialize(new CashSessionClosedPayloadV1(
                    closed.SessionId, operatorId, closed.OpeningFloat, closedAtUtc, summary.SaleCount, summary.CashKept,
                    summary.CardTotal, summary.QrTotal, summary.UntenderedTotal, summary.ExpectedCash,
                    closure.CountedCash, closure.Difference)));
        });
    }

    public bool Acknowledge(Guid operationId) => _store.Acknowledge(operationId);

    public SyncStatusSnapshot GetStatus(Guid branchId, bool isOffline) => _store.GetStatus(branchId, isOffline);

    /// <summary>
    /// Two non-commuting envelope edits for the same aggregate. Both are kept
    /// as-is; nothing is written or merged until an authorized reviewer
    /// resolves them (ADR-002: last-write-wins is rejected).
    /// </summary>
    public MasterEditConflict DetectConflict(SyncEnvelope local, SyncEnvelope remote) =>
        new(Guid.NewGuid(), local.AggregateId, local, remote, _clock());

    public bool ReviewConflict(MasterEditConflict conflict, UserAccount reviewer, string decision, Guid correlationId)
    {
        var result = _authorizationService.Authorize(
            reviewer,
            new AccessRequest(reviewer.OrganizationId, conflict.Local.BranchId, ReviewSyncConflict, IsOffline: false, correlationId));

        if (!result.Allowed)
        {
            return false;
        }

        conflict.Resolve(new ConflictResolution(reviewer.Id, decision, _clock()));
        return true;
    }
}
