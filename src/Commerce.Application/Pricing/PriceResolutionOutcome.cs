namespace Commerce.Application.Pricing;

/// <summary>
/// Typed result of <see cref="PricingResolutionService.ResolveAsync"/>
/// (commerce-pricing-engine design.md "Requirement: Explicit Error When No
/// Effective Price Exists"). There is NO `decimal?` return anywhere in this
/// contract and no `0m` fallback — a caller cannot accidentally treat a
/// missing price as free by forgetting a null check, because there is no
/// nullable decimal to forget to check.
/// </summary>
public abstract record PriceResolutionOutcome
{
    private PriceResolutionOutcome()
    {
    }

    /// <summary>A price was resolved. `UnitListPrice`/`UnitNetPrice`/`LineTotal` are `Money.Round2`-rounded per the design's rounding policy.</summary>
    public sealed record Resolved(
        decimal UnitListPrice,
        decimal AppliedDiscountPercentage,
        decimal UnitNetPrice,
        decimal LineTotal) : PriceResolutionOutcome;

    /// <summary>No <c>PriceListEntry</c> is effective for <see cref="PresentationId"/> on or before <see cref="On"/> — exactly zero matching rows.</summary>
    public sealed record NoEffectivePrice(Guid PresentationId, DateOnly On) : PriceResolutionOutcome;
}
