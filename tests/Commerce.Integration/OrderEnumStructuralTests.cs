using System.Text.RegularExpressions;
using Commerce.Domain.Ordering;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 1 task 1.13 (commerce-payments proposal.md "Payment as a
/// Separate Aggregate from Order" — "no payment value MAY be added to either
/// enum", ADR-011). Reflection over <see cref="OrderDeliveryStatus"/> and
/// <see cref="OrderPendingReason"/> asserts no member name matches
/// <c>paid|payment|settl</c> (case-insensitive). This is the regression
/// tripwire for a later contributor who might otherwise merge payment state
/// into fulfilment state.
/// </summary>
public sealed class OrderEnumStructuralTests
{
    private static readonly Regex ForbiddenPattern = new("paid|payment|settl", RegexOptions.IgnoreCase);

    [Fact]
    public void OrderDeliveryStatus_HasNoPaymentShapedMember()
    {
        var names = Enum.GetNames<OrderDeliveryStatus>();
        Assert.All(names, name => Assert.False(ForbiddenPattern.IsMatch(name), $"'{name}' looks payment-shaped."));
    }

    [Fact]
    public void OrderPendingReason_HasNoPaymentShapedMember()
    {
        var names = Enum.GetNames<OrderPendingReason>();
        Assert.All(names, name => Assert.False(ForbiddenPattern.IsMatch(name), $"'{name}' looks payment-shaped."));
    }
}
