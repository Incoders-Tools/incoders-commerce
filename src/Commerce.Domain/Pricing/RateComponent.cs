namespace Commerce.Domain.Pricing;

/// <summary>
/// One open rate row inside a <see cref="RateComponentSet"/>
/// (commerce-price-composition design.md "Component shape"). Rates are ROWS,
/// never columns: nothing in this model names VAT, gross-receipts tax,
/// freight or markup, so a zone surcharge or a new IIBB perception is an
/// additional component, not a schema migration and not a new branch in the
/// resolution code.
///
/// An immutable historical fact, like <see cref="PriceListEntry"/>: a rate
/// change publishes a NEW dated set, it never rewrites this row.
/// </summary>
public sealed class RateComponent
{
    /// <summary>Stable machine identifier, e.g. `IVA`, `IB`, `FLETE`, `REMARCACION`.</summary>
    public string Code { get; }

    /// <summary>Human-readable, shown to an admin, e.g. "IVA (10,5%)".</summary>
    public string Label { get; }

    /// <summary>
    /// A percentage NUMBER, not a fraction: 10.5 means 10.5%, never 0.105.
    /// The proposal deferred this choice to implementation; it is fixed here
    /// and mirrored by `rate_components.percentage numeric(9,4)`. Every
    /// consumer divides by 100 exactly once, inside
    /// <see cref="RateComponentSet.Compose"/>.
    /// </summary>
    public decimal Percentage { get; }

    /// <summary>Required and explicit — see <see cref="RateCalculationBase"/>.</summary>
    public RateCalculationBase CalculationBase { get; }

    /// <summary>
    /// Explicit application order within its set, never insertion order and
    /// never code-alphabetical. Only OBSERVABLE once at least one component
    /// uses <see cref="RateCalculationBase.Subtotal"/>, but declared
    /// unconditionally so switching a component to `Subtotal` later cannot
    /// silently reorder the composition (design.md "Ordering").
    /// </summary>
    public int Order { get; }

    public RateComponent(
        string code,
        string label,
        decimal percentage,
        RateCalculationBase calculationBase,
        int order)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("code must be a non-blank machine identifier.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("label must be a non-blank human-readable name.", nameof(label));
        }

        if (percentage < 0)
        {
            // A negative rate would be a discount. Customer discounts stay
            // exactly where they are — applied last, by the resolution
            // service — and are explicitly out of scope for components.
            throw new ArgumentOutOfRangeException(nameof(percentage), "percentage must not be negative.");
        }

        if (calculationBase is RateCalculationBase.Unspecified)
        {
            throw new ArgumentException(
                "calculationBase must be declared explicitly as Base or Subtotal; it is never inferred.",
                nameof(calculationBase));
        }

        if (order < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "order must not be negative.");
        }

        Code = code;
        Label = label;
        Percentage = percentage;
        CalculationBase = calculationBase;
        Order = order;
    }
}
