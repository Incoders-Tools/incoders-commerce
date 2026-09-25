using Commerce.Domain.Pricing;

namespace Commerce.Integration;

/// <summary>
/// Pure domain coverage for commerce-price-composition slice 1 — the rate
/// component model itself (`specs/price-list-management/spec.md`
/// requirements "Price List Rate Components", "Explicit Calculation Base Per
/// Component", "Append-Only Effective-Dated Rate Component History"). No
/// Postgres: these are construction rules and arithmetic, not persistence.
///
/// <see cref="RateComponentSet.Compose"/> is exercised here even though slice
/// 1 deliberately does NOT wire it into `PricingResolutionService` — without
/// it, <see cref="RateComponent.CalculationBase"/> would be an inert stored
/// string and the spec's "base-calculated components do not compound"
/// scenario would have nothing to assert against.
/// </summary>
public sealed class RateComponentTests
{
    private static RateComponent Component(
        string code, decimal percentage, RateCalculationBase calculationBase, int order) =>
        new(code, $"{code} ({percentage}%)", percentage, calculationBase, order);

    /// <summary>Vaca Verde's real delivery sheet, all four on the base price.</summary>
    private static IReadOnlyList<RateComponent> VacaVerdeComponents() =>
    [
        Component("IVA", 10.5m, RateCalculationBase.Base, 1),
        Component("IB", 2.5m, RateCalculationBase.Base, 2),
        Component("FLETE", 7m, RateCalculationBase.Base, 3),
        Component("REMARCACION", 25m, RateCalculationBase.Base, 4),
    ];

    private static RateComponentSet SetFor(IReadOnlyList<RateComponent> components) =>
        RateComponentSet.ForPriceList(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), components);

    // --- Component construction -------------------------------------------

    /// <summary>
    /// Spec "Explicit Calculation Base Per Component": the system MUST NOT
    /// infer, default, or hard-code the calculation base; a component without
    /// a declared one MUST be rejected. `Unspecified` is the only way to
    /// express "not declared" in C# — a plain two-valued enum would silently
    /// default to `Base` and make this rule untestable.
    /// </summary>
    [Fact]
    public void Component_WithUndeclaredCalculationBase_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new RateComponent("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Unspecified, 1));
    }

    [Fact]
    public void Component_DefaultEnumValue_IsUnspecified_NotBase()
    {
        // If `default` were `Base`, the rejection above could never fire for a
        // caller that simply forgot the argument.
        Assert.Equal(RateCalculationBase.Unspecified, default(RateCalculationBase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Component_WithBlankCode_IsRejected(string code)
    {
        Assert.Throws<ArgumentException>(() =>
            new RateComponent(code, "Label", 10.5m, RateCalculationBase.Base, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Component_WithBlankLabel_IsRejected(string label)
    {
        Assert.Throws<ArgumentException>(() =>
            new RateComponent("IVA", label, 10.5m, RateCalculationBase.Base, 1));
    }

    [Fact]
    public void Component_WithNegativeOrder_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RateComponent("IVA", "IVA", 10.5m, RateCalculationBase.Base, -1));
    }

    /// <summary>
    /// A percentage is a percentage NUMBER (10.5 meaning 10.5%), not a
    /// fraction (0.105) — the proposal deferred this to implementation and
    /// this is where it is fixed. A negative rate is a discount, which the
    /// change explicitly keeps out of components.
    /// </summary>
    [Fact]
    public void Component_WithNegativePercentage_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RateComponent("IVA", "IVA", -1m, RateCalculationBase.Base, 1));
    }

    /// <summary>
    /// R3-publish-returns-unpersisted-precision. `rate_components.percentage`
    /// is `numeric(9,4)`; Postgres ROUNDS a finer percentage on INSERT without
    /// a word. Accepting one here would mean `PublishSetAsync` hands its caller
    /// a set that composes a different price than every later read of the same
    /// set composes. Four decimal places is a ten-thousandth of a percent — far
    /// below any real rate — so the edge REJECTS instead of silently rounding.
    /// </summary>
    [Fact]
    public void Component_WithAPercentageFinerThanFourDecimalPlaces_IsRejected()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RateComponent("IVA", "IVA", 10.50005m, RateCalculationBase.Base, 1));

        Assert.Contains("four decimal places", error.Message);
    }

    /// <summary>
    /// The boundary, both ways: exactly four decimal places is the finest rate
    /// the column stores exactly, so it MUST be accepted — without this, a
    /// validator that rejected everything below 1% would also pass the test
    /// above.
    /// </summary>
    [Fact]
    public void Component_WithExactlyFourDecimalPlaces_IsAccepted()
    {
        Assert.Equal(10.5005m, new RateComponent("IVA", "IVA", 10.5005m, RateCalculationBase.Base, 1).Percentage);
    }

    /// <summary>
    /// Trailing zeros are SCALE, not information: `10.50000m` carries five
    /// decimal digits in its representation but the same value as `10.5m`, and
    /// the column stores it exactly. A naive scale check on the decimal's bits
    /// would reject it.
    /// </summary>
    [Fact]
    public void Component_WithTrailingZerosBeyondFourPlaces_IsAccepted()
    {
        Assert.Equal(10.5m, new RateComponent("IVA", "IVA", 10.50000m, RateCalculationBase.Base, 1).Percentage);
    }

    /// <summary>
    /// The other half of `numeric(9,4)`: 9 total digits with 4 after the point
    /// leaves 5 before it. A larger percentage is a `22003` numeric overflow
    /// deep inside the publish transaction; rejecting it at the edge makes the
    /// domain type's range exactly the column's range.
    /// </summary>
    [Fact]
    public void Component_WithAPercentageWiderThanTheColumn_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RateComponent("IVA", "IVA", 100_000m, RateCalculationBase.Base, 1));
    }

    [Fact]
    public void Component_PreservesCodeLabelPercentageBaseAndOrder()
    {
        var component = new RateComponent("FLETE", "Flete (7%)", 7m, RateCalculationBase.Subtotal, 3);

        Assert.Equal("FLETE", component.Code);
        Assert.Equal("Flete (7%)", component.Label);
        Assert.Equal(7m, component.Percentage);
        Assert.Equal(RateCalculationBase.Subtotal, component.CalculationBase);
        Assert.Equal(3, component.Order);
    }

    // --- Set construction --------------------------------------------------

    [Fact]
    public void Set_OrdersComponentsByTheirDeclaredOrder_NotInsertionOrder()
    {
        var set = SetFor(
        [
            Component("REMARCACION", 25m, RateCalculationBase.Base, 4),
            Component("IVA", 10.5m, RateCalculationBase.Base, 1),
            Component("FLETE", 7m, RateCalculationBase.Base, 3),
            Component("IB", 2.5m, RateCalculationBase.Base, 2),
        ]);

        Assert.Equal(["IVA", "IB", "FLETE", "REMARCACION"], set.Components.Select(c => c.Code));
    }

    [Fact]
    public void Set_WithDuplicateCode_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => SetFor(
        [
            Component("IVA", 10.5m, RateCalculationBase.Base, 1),
            Component("IVA", 21m, RateCalculationBase.Base, 2),
        ]));
    }

    /// <summary>
    /// Design "Ordering": the order is explicit and must not be ambiguous,
    /// because a component switched to `Subtotal` later would otherwise
    /// silently reorder the whole composition.
    /// </summary>
    [Fact]
    public void Set_WithDuplicateOrder_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => SetFor(
        [
            Component("IVA", 10.5m, RateCalculationBase.Base, 1),
            Component("IB", 2.5m, RateCalculationBase.Base, 1),
        ]));
    }

    /// <summary>Spec "A list may carry no components": empty is legal, not an error.</summary>
    [Fact]
    public void Set_WithNoComponents_IsLegal()
    {
        var set = SetFor([]);
        Assert.Empty(set.Components);
    }

    [Fact]
    public void Set_ForPriceList_CarriesThePriceListAsItsOwner()
    {
        var organizationId = Guid.NewGuid();
        var priceListId = Guid.NewGuid();

        var set = RateComponentSet.ForPriceList(
            Guid.NewGuid(), organizationId, priceListId, new DateOnly(2026, 1, 1), VacaVerdeComponents());

        Assert.Equal(organizationId, set.OrganizationId);
        Assert.Equal(priceListId, set.PriceListId);
        Assert.False(set.IsOrganizationDefault);
    }

    /// <summary>
    /// Spec "Organization Default Rate Components": an org-default set has no
    /// owning price list, which is exactly what makes it inheritable.
    /// </summary>
    [Fact]
    public void Set_ForOrganizationDefault_HasNoOwningPriceList()
    {
        var organizationId = Guid.NewGuid();

        var set = RateComponentSet.ForOrganizationDefault(
            Guid.NewGuid(), organizationId, new DateOnly(2026, 1, 1), VacaVerdeComponents());

        Assert.Equal(organizationId, set.OrganizationId);
        Assert.Null(set.PriceListId);
        Assert.True(set.IsOrganizationDefault);
    }

    // --- Composition (arithmetic only; slice 2 wires this into resolution) --

    /// <summary>
    /// Spec "Base-calculated components do not compound", against the three
    /// rows verified in the real "Precios Vaca Verde Reparto con Porcentajes"
    /// sheet.
    /// </summary>
    [Theory]
    [InlineData(10600, 15370)]
    [InlineData(11400, 16530)]
    [InlineData(22500, 32625)]
    public void Compose_VacaVerdeBaseCalculatedComponents_MatchTheSheet(decimal basePrice, decimal expected)
    {
        var set = SetFor(VacaVerdeComponents());

        Assert.Equal(expected, set.Compose(basePrice));
    }

    /// <summary>
    /// Spec "An entry with no components resolves to its stored amount" — the
    /// property that makes the `UnitPrice` respecification a reinterpretation
    /// rather than a data rewrite.
    /// </summary>
    [Fact]
    public void Compose_WithEmptySet_ReturnsTheBasePriceExactly()
    {
        Assert.Equal(15370m, SetFor([]).Compose(15370m));
    }

    /// <summary>
    /// Spec "Subtotal-calculated components chain in order": the second
    /// component's percentage is computed from the subtotal the first
    /// produced, not from the base price. 100 -> 110 -> 121, never 120.
    /// </summary>
    [Fact]
    public void Compose_SubtotalCalculatedComponents_ChainInOrder()
    {
        var set = SetFor(
        [
            Component("FIRST", 10m, RateCalculationBase.Subtotal, 1),
            Component("SECOND", 10m, RateCalculationBase.Subtotal, 2),
        ]);

        Assert.Equal(121m, set.Compose(100m));
    }

    /// <summary>
    /// Pricing-resolution spec "Components do not compound": the same four
    /// percentages declared on the subtotal instead of the base produce a
    /// strictly greater result — proof that the declared calculation base,
    /// not an implicit default, decides the outcome.
    /// </summary>
    [Fact]
    public void Compose_SameFourPercentagesOnSubtotal_ExceedsTheBaseCalculatedResult()
    {
        var compounded = SetFor(
        [
            Component("IVA", 10.5m, RateCalculationBase.Subtotal, 1),
            Component("IB", 2.5m, RateCalculationBase.Subtotal, 2),
            Component("FLETE", 7m, RateCalculationBase.Subtotal, 3),
            Component("REMARCACION", 25m, RateCalculationBase.Subtotal, 4),
        ]).Compose(10600m);

        Assert.True(compounded > 15370m, $"expected compounding to exceed 15370, got {compounded}");
        Assert.Equal(16057.7909375m, compounded);
    }

    /// <summary>
    /// A mixed set is where ordering becomes observable: `IVA` on the base
    /// contributes 1,113 regardless of position, while `FLETE` on the
    /// subtotal sees whatever came before it.
    /// </summary>
    [Fact]
    public void Compose_MixedBases_AppliesEachComponentAgainstItsOwnDeclaredBase()
    {
        var set = SetFor(
        [
            Component("IVA", 10.5m, RateCalculationBase.Base, 1),      // 10600 + 1113 = 11713
            Component("FLETE", 10m, RateCalculationBase.Subtotal, 2),  // 11713 + 1171.3 = 12884.3
        ]);

        Assert.Equal(12884.3m, set.Compose(10600m));
    }

    /// <summary>
    /// Composition does not round — the change explicitly preserves whatever
    /// rounding resolution already applies and introduces none of its own. A
    /// component rounding to the schema's 2 decimals would collapse this to
    /// 0.00.
    /// </summary>
    [Fact]
    public void Compose_DoesNotRoundItsResult()
    {
        var set = SetFor([Component("IVA", 10.5m, RateCalculationBase.Base, 1)]);

        Assert.Equal(0.001105m, set.Compose(0.001m));
    }
}
