namespace Commerce.Domain.Sync.Payloads;

/// <summary>
/// Payload kinds of the cash session (pos-cash-session "Session Synchronized
/// and Audited"). Versioning follows the sale payload: the kind is stable and
/// the record is additive-only (a field may be added later, never removed or
/// repurposed; a breaking change ships as a new kind).
/// </summary>
public static class CashSessionPayloadKinds
{
    public const string Opened = "cash-session.opened";
    public const string Closed = "cash-session.closed";
}

/// <summary>The payload of a <c>cash-session.opened</c> envelope.</summary>
public sealed record CashSessionOpenedPayloadV1(
    Guid SessionId,
    Guid OperatorId,
    decimal OpeningFloat,
    DateTimeOffset OpenedAtUtc);

/// <summary>
/// The payload of a <c>cash-session.closed</c> envelope: the computed totals,
/// the counted cash and the difference (counted minus expected). `AccountTotal` (sales on customers' current accounts)
/// is 0 from terminals that predate it, and so are the `Collected*` totals (customer payments of current account debt;
/// the cash ones are part of the expected cash), and `CashWithdrawn` / `CashDeposited` (cash movements outside a sale).
/// </summary>
public sealed record CashSessionClosedPayloadV1(
    Guid SessionId,
    Guid ClosedByOperatorId,
    decimal OpeningFloat,
    DateTimeOffset ClosedAtUtc,
    int SaleCount,
    decimal CashKept,
    decimal CardTotal,
    decimal QrTotal,
    decimal UntenderedTotal,
    decimal ExpectedCash,
    decimal CountedCash,
    decimal Difference,
    decimal AccountTotal = 0m,
    decimal CollectedCash = 0m,
    decimal CollectedCard = 0m,
    decimal CollectedQr = 0m,
    decimal CashWithdrawn = 0m,
    decimal CashDeposited = 0m);

/// <summary>Payload kinds of the cash movements of a session (money in or out of the drawer outside a sale).</summary>
public static class CashMovementPayloadKinds
{
    public const string Recorded = "cash-movement.recorded";
}

/// <summary>
/// The payload of a <c>cash-movement.recorded</c> envelope: a withdrawal or a deposit of the open session
/// (<see cref="Commerce.Domain.CashSessions.CashMovement"/>), with where the money went or came from, the reason, and the
/// branch PIN authorization a withdrawal needs (null for a deposit).
/// </summary>
public sealed record CashMovementRecordedPayloadV1(
    Guid MovementId,
    Guid CashSessionId,
    string Kind,
    string Counterpart,
    decimal Amount,
    string Reason,
    DateTimeOffset OccurredAtUtc,
    Guid OperatorId,
    Commerce.Domain.Discounts.DiscountAuthorization? Authorization);
