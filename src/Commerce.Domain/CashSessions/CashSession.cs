using Commerce.Domain.Pricing;
using Commerce.Domain.Sales;

namespace Commerce.Domain.CashSessions;

/// <summary>
/// The cash session ("caja") of a terminal (pos-cash-session): opened by an
/// operator with an opening float, it owns every sale committed until it is
/// closed. <see cref="Closure"/> is null while the session is open.
/// </summary>
public sealed record CashSession(
    Guid SessionId,
    Guid OrganizationId,
    Guid BranchId,
    Guid OpenedByOperatorId,
    DateTimeOffset OpenedAtUtc,
    decimal OpeningFloat,
    CashSessionClosure? Closure = null)
{
    public bool IsOpen => Closure is null;
}

/// <summary>The recorded close: who, when, the computed totals and the counted cash.</summary>
public sealed record CashSessionClosure(
    Guid ClosedByOperatorId,
    DateTimeOffset ClosedAtUtc,
    CashSessionSummary Summary,
    decimal CountedCash)
{
    /// <summary>Counted minus expected: positive is a surplus, negative a shortage.</summary>
    public decimal Difference => CashSessionMath.Difference(CountedCash, Summary.ExpectedCash);
}

/// <summary>Totals of a session computed from its sales' recorded tenders.</summary>
public sealed record CashSessionSummary(
    decimal OpeningFloat,
    int SaleCount,
    decimal CashKept,
    decimal CardTotal,
    decimal QrTotal,
    decimal UntenderedTotal)
{
    /// <summary>Cash the drawer should hold: the float plus the cash actually kept.</summary>
    public decimal ExpectedCash => OpeningFloat + CashKept;
}

/// <summary>One sale of a session as the totals need it: its final total and its tender.</summary>
public readonly record struct CashSessionSale(decimal Total, SaleTender? Tender);

/// <summary>Why a sale commit was refused before anything was written.</summary>
public enum SaleCommitRefusal
{
    NoOpenCashSession,
}

public enum CashSessionOpenOutcome
{
    Opened,
    AlreadyOpen,
    InvalidFloat,
}

public enum CashSessionCloseOutcome
{
    Closed,
    AlreadyClosed,
    NotFound,
    InvalidCountedCash,
}

/// <summary><see cref="Session"/> is the new session on Opened and the session already open on AlreadyOpen.</summary>
public sealed record CashSessionOpenResult(CashSessionOpenOutcome Outcome, CashSession? Session);

/// <summary><see cref="Session"/> is the closed session on Closed and the current record otherwise (when found).</summary>
public sealed record CashSessionCloseResult(CashSessionCloseOutcome Outcome, CashSession? Session);

/// <summary>Pure cash session arithmetic.</summary>
public static class CashSessionMath
{
    public static CashSessionSummary Summarize(decimal openingFloat, IEnumerable<CashSessionSale> sales)
    {
        var count = 0;
        decimal cash = 0m, card = 0m, qr = 0m, untendered = 0m;
        foreach (var sale in sales)
        {
            count++;
            switch (sale.Tender?.Method)
            {
                case SaleTender.Cash:
                    // Cash kept is what stayed in the drawer: received minus change,
                    // which is the sale total whenever both amounts were recorded.
                    cash += sale.Tender.AmountReceived is { } received && sale.Tender.ChangeGiven is { } change
                        ? received - change
                        : sale.Total;
                    break;
                case SaleTender.Card:
                    card += sale.Total;
                    break;
                case SaleTender.Qr:
                    qr += sale.Total;
                    break;
                default:
                    untendered += sale.Total;
                    break;
            }
        }

        return new CashSessionSummary(openingFloat, count, cash, card, qr, untendered);
    }

    public static decimal Difference(decimal counted, decimal expected) => counted - expected;

    /// <summary>A float or counted amount: zero or more, at most two decimals.</summary>
    public static bool IsValidAmount(decimal amount) => amount >= 0m && Money.Round2(amount) == amount;
}
