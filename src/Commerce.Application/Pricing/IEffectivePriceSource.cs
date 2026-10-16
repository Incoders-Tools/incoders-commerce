namespace Commerce.Application.Pricing;

/// <summary>
/// The one port both hosts (cloud API and POS) implement to supply the
/// currently-effective unit price for a presentation on a date
/// (commerce-pricing-engine design.md "`PricingResolutionService` contract
/// and location"). Deliberately has NO channel or caller-identity
/// parameter — channel independence is structural, not a convention.
/// Postgres supplies <c>PostgresEffectivePriceSource</c> (this change); the
/// POS will supply a SQLite-backed source in a later work unit. Returning
/// `null` means exactly zero effective rows for the tuple — the caller
/// (<see cref="PricingResolutionService"/>) is responsible for turning that
/// into the typed <see cref="PriceResolutionOutcome.NoEffectivePrice"/>,
/// never a silent zero.
/// </summary>
public interface IEffectivePriceSource
{
    Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct);
}
