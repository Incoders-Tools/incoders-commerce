namespace Commerce.Domain.Pricing;

/// <summary>
/// What a <see cref="RateComponent"/>'s percentage is computed FROM
/// (commerce-price-composition design.md "Calculation base"). Required and
/// explicit per component: Vaca Verde's four rates all apply to the base
/// price and deliberately do NOT compound, while another distributor's
/// freight is charged on the already-taxed amount. Leaving this implicit
/// would hard-code one business's arithmetic into the engine — the same four
/// percentages give 10,600 -> 15,370 on <see cref="Base"/> and a strictly
/// greater number on <see cref="Subtotal"/>.
/// </summary>
public enum RateCalculationBase
{
    /// <summary>
    /// NOT a calculation base — the absence of one. It exists so that
    /// "a component without a declared calculation base MUST be rejected"
    /// (spec "Explicit Calculation Base Per Component") is a rejection that
    /// can actually fire. A plain two-valued enum would make C#'s `default`
    /// silently mean <see cref="Base"/>, i.e. exactly the inferred default
    /// the spec forbids, and the rule would be untestable.
    /// </summary>
    Unspecified = 0,

    /// <summary>The percentage applies to the entry's base `UnitPrice`.</summary>
    Base = 1,

    /// <summary>
    /// The percentage applies to the subtotal accumulated by the components
    /// ordered before this one.
    /// </summary>
    Subtotal = 2,
}
