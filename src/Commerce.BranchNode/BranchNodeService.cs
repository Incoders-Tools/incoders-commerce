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
        SaleTender? tender = null,
        SaleNumbering? numbering = null)
    {
        RequireCustomerForAccount(tender, customerId);
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

        return _store.CommitSaleAtomically(envelope, effect, requireOpenCashSession: true, numbering);
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
    /// <paramref name="numbering"/> is the terminal's branch code and register number; null while it does
    /// not know them (the sale then commits without a human number).
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
        SaleTender? tender = null,
        SaleNumbering? numbering = null)
    {
        RequireCustomerForAccount(tender, customerId);
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

        return _store.CommitScannedSaleAtomically(envelope, effect, lines, requireOpenCashSession: true, numbering);
    }

    /// <summary>A sale on current account is charged to its customer: without one there is no account to charge.</summary>
    private static void RequireCustomerForAccount(SaleTender? tender, Guid? customerId)
    {
        if (tender?.Method == SaleTender.Account && customerId is null)
        {
            throw new ArgumentException("A sale on current account needs its customer.", nameof(tender));
        }
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
                    closure.CountedCash, closure.Difference, summary.AccountTotal, summary.CollectedCash, summary.CollectedCard,
                    summary.CollectedQr, summary.CashWithdrawn, summary.CashDeposited)));
        });
    }

    /// <summary>The longest void reason accepted (it travels in the envelope and the cloud audit).</summary>
    public const int MaxVoidReasonLength = 200;

    /// <summary>
    /// Voids a committed sale of the open cash session, authorized by <paramref name="authorization"/> (the branch PIN
    /// proof). The sale is left untouched: the void is its own record, written with its <c>sale.voided</c> envelope in one
    /// transaction, and the session's totals stop counting the sale. A blank reason is refused (PRD: a void needs a reason);
    /// so is a sale already voided, unknown, or of a closed session (see <see cref="SaleVoidOutcome"/>).
    /// </summary>
    public SaleVoidResult VoidSale(
        Guid organizationId,
        Guid branchId,
        Guid operatorId,
        Guid saleId,
        DiscountAuthorization authorization,
        string reason,
        Guid correlationId)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxVoidReasonLength)
        {
            throw new ArgumentException($"A void needs a reason of 1 to {MaxVoidReasonLength} characters.", nameof(reason));
        }

        var voidedAtUtc = _clock();
        return _store.VoidSale(
            saleId,
            sale => new SaleVoidRecord(saleId, sale.CashSessionId, voidedAtUtc, operatorId, authorization, trimmed, Guid.NewGuid()),
            (sale, record) => new SyncEnvelope(
                OperationId: record.OperationId,
                ContractVersion: 1,
                OrganizationId: organizationId,
                BranchId: branchId,
                AggregateId: saleId,
                AggregateVersion: 2,
                ActorId: operatorId,
                CorrelationId: correlationId,
                OccurredAtUtc: voidedAtUtc,
                PayloadKind: SalePayloadKinds.Voided,
                Payload: SyncPayloadCodec.Serialize(new SaleVoidedPayloadV1(
                    saleId, voidedAtUtc, operatorId, authorization, trimmed, sale.TotalAmount, sale.Tender, sale.CashSessionId))));
    }

    /// <summary>The longest note a customer payment can carry.</summary>
    public const int MaxPaymentNoteLength = 200;

    /// <summary>
    /// A customer pays part or all of its current account debt at the counter (a "cobro"): recorded in the open cash
    /// session (a cash payment is cash in the drawer) with its <c>customer-payment.received</c> envelope, which credits the
    /// customer's account and puts the money in the branch treasury in the cloud. The amount is positive with at most two
    /// decimals; the tender is cash (received at least the amount), card or QR, never "on account".
    /// </summary>
    public CustomerPaymentResult ReceiveCustomerPayment(
        Guid organizationId, Guid branchId, Guid operatorId, Guid customerId, decimal amount, SaleTender tender, string? note,
        Guid correlationId)
    {
        if (amount <= 0m || decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentException("A payment is a positive amount with at most two decimals.", nameof(amount));
        }

        if (tender.Method is not (SaleTender.Cash or SaleTender.Card or SaleTender.Qr))
        {
            throw new ArgumentException("A payment is received in cash, card or QR.", nameof(tender));
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is { Length: > MaxPaymentNoteLength })
        {
            throw new ArgumentException($"The note has at most {MaxPaymentNoteLength} characters.", nameof(note));
        }

        var receivedAtUtc = _clock();
        var session = _store.GetOpenCashSession()?.SessionId;
        var payment = new CustomerPaymentRecord(
            Guid.NewGuid(), customerId, null, amount, tender, receivedAtUtc, session, operatorId, trimmedNote, null);
        var envelope = new SyncEnvelope(
            OperationId: Guid.NewGuid(),
            ContractVersion: 1,
            OrganizationId: organizationId,
            BranchId: branchId,
            AggregateId: payment.PaymentId,
            AggregateVersion: 1,
            ActorId: operatorId,
            CorrelationId: correlationId,
            OccurredAtUtc: receivedAtUtc,
            PayloadKind: CustomerPaymentPayloadKinds.Received,
            Payload: SyncPayloadCodec.Serialize(new CustomerPaymentReceivedPayloadV1(
                payment.PaymentId, customerId, amount, tender, receivedAtUtc, session, trimmedNote)));

        var outcome = session is null ? CustomerPaymentOutcome.NoOpenCashSession : _store.RecordCustomerPayment(payment, envelope);
        return new CustomerPaymentResult(outcome, outcome == CustomerPaymentOutcome.Recorded ? payment : null);
    }

    /// <summary>Voids a payment of the open cash session, authorized by the branch PIN, with a reason (same rules as a sale void).</summary>
    public SaleVoidResult VoidCustomerPayment(
        Guid organizationId, Guid branchId, Guid operatorId, Guid paymentId, DiscountAuthorization authorization, string reason,
        Guid correlationId)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxVoidReasonLength)
        {
            throw new ArgumentException($"A void needs a reason of 1 to {MaxVoidReasonLength} characters.", nameof(reason));
        }

        var voidedAtUtc = _clock();
        return _store.VoidCustomerPayment(
            paymentId,
            payment => new SaleVoidRecord(paymentId, payment.CashSessionId, voidedAtUtc, operatorId, authorization, trimmed, Guid.NewGuid()),
            (payment, record) => new SyncEnvelope(
                OperationId: record.OperationId,
                ContractVersion: 1,
                OrganizationId: organizationId,
                BranchId: branchId,
                AggregateId: paymentId,
                AggregateVersion: 2,
                ActorId: operatorId,
                CorrelationId: correlationId,
                OccurredAtUtc: voidedAtUtc,
                PayloadKind: CustomerPaymentPayloadKinds.Voided,
                Payload: SyncPayloadCodec.Serialize(new CustomerPaymentVoidedPayloadV1(
                    paymentId, payment.CustomerId, payment.Amount, payment.Tender, voidedAtUtc, operatorId, authorization, trimmed,
                    payment.CashSessionId))));
    }

    /// <summary>
    /// Records money taken out of (<see cref="CashMovement.Withdrawal"/>) or put into (<see cref="CashMovement.Deposit"/>)
    /// the drawer of the open session, outside a sale. A withdrawal needs the branch PIN authorization and cannot take
    /// more than the drawer should hold. Both need a reason; both change the expected cash and reach the treasury on sync.
    /// </summary>
    public CashMovementResult RecordCashMovement(
        Guid organizationId, Guid branchId, Guid operatorId, string kind, string counterpart, decimal amount, string reason,
        DiscountAuthorization? authorization, Guid correlationId)
    {
        if (!CashMovement.IsValidKind(kind) || !CashMovement.IsValidCounterpart(kind, counterpart))
        {
            throw new ArgumentException("Unknown cash movement kind or counterpart.", nameof(counterpart));
        }

        if (amount <= 0m || decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentException("A cash movement is a positive amount with at most two decimals.", nameof(amount));
        }

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > CashMovement.MaxReasonLength)
        {
            throw new ArgumentException($"A cash movement needs a reason of 1 to {CashMovement.MaxReasonLength} characters.", nameof(reason));
        }

        if (kind == CashMovement.Withdrawal && authorization is null)
        {
            throw new ArgumentException("A withdrawal needs the branch PIN authorization.", nameof(authorization));
        }

        if (_store.GetOpenCashSession()?.SessionId is not { } session)
        {
            return new CashMovementResult(CashMovementOutcome.NoOpenCashSession, null);
        }

        var occurredAtUtc = _clock();
        var movement = new CashMovementRecord(
            Guid.NewGuid(), session, kind, counterpart, amount, trimmed, occurredAtUtc, operatorId,
            kind == CashMovement.Withdrawal ? authorization : null);
        var envelope = new SyncEnvelope(
            OperationId: Guid.NewGuid(),
            ContractVersion: 1,
            OrganizationId: organizationId,
            BranchId: branchId,
            AggregateId: movement.MovementId,
            AggregateVersion: 1,
            ActorId: operatorId,
            CorrelationId: correlationId,
            OccurredAtUtc: occurredAtUtc,
            PayloadKind: CashMovementPayloadKinds.Recorded,
            Payload: SyncPayloadCodec.Serialize(new CashMovementRecordedPayloadV1(
                movement.MovementId, session, kind, counterpart, amount, trimmed, occurredAtUtc, operatorId, movement.Authorization)));

        var outcome = _store.RecordCashMovement(movement, envelope);
        return new CashMovementResult(outcome, outcome == CashMovementOutcome.Recorded ? movement : null);
    }

    /// <summary>The cash movements in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), newest first.</summary>
    public IReadOnlyList<CashMovementRecord> ListCashMovements(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        _store.ListCashMovements(fromUtc, toUtc);

    /// <summary>The customer payments received in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), newest first.</summary>
    public IReadOnlyList<CustomerPaymentRecord> ListCustomerPayments(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        _store.ListCustomerPayments(fromUtc, toUtc);

    /// <summary>What this terminal knows of a customer's current account (last synced balance, corrected by what it did since).</summary>
    public CustomerAccountView GetCustomerAccountView(Guid customerId) => _store.GetCustomerAccountView(customerId);

    /// <summary>The sales committed in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), newest first.</summary>
    public IReadOnlyList<SaleHistoryEntry> ListSales(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 500) =>
        _store.ListSales(fromUtc, toUtc, limit);

    /// <summary>The stored effect of a committed sale (discounts, tender, session), or null when unknown.</summary>
    public SaleEffect? GetSaleEffect(Guid saleId) => _store.GetSaleEffect(saleId);

    /// <summary>The lines of a committed sale, in order (none for a manual-amount sale).</summary>
    public IReadOnlyList<Commerce.Domain.Sync.SaleLine> ListSaleLines(Guid saleId) => _store.ListSaleLines(saleId);

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
