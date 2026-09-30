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
/// the counted cash and the difference (counted minus expected).
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
    decimal Difference);
