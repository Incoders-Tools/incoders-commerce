namespace Commerce.Domain.Payments;

/// <summary>
/// Derived, never-persisted settlement view (commerce-payments design.md
/// "Where the arithmetic lives"). Produced only by
/// <c>Commerce.Application.Payments.SettlementCalculator.Fold</c>.
/// </summary>
public sealed record Settlement(decimal Target, decimal Settled, decimal Outstanding, bool IsSettled);
