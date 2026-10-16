using Commerce.Domain.Payments;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 1 task 1.1 (commerce-payments design.md "One aggregate for POS
/// cash and future online"): five distinct <see cref="PaymentMethod"/>
/// values exist, and Mercado Pago is its own first-class member — never a
/// `Card` sub-case (answered product question (a)).
/// </summary>
public sealed class PaymentMethodTaxonomyTests
{
    [Fact]
    public void PaymentMethod_HasFiveDistinctValues()
    {
        var values = Enum.GetValues<PaymentMethod>();

        Assert.Equal(5, values.Distinct().Count());
        Assert.Contains(PaymentMethod.Cash, values);
        Assert.Contains(PaymentMethod.AccountCredit, values);
        Assert.Contains(PaymentMethod.BankTransfer, values);
        Assert.Contains(PaymentMethod.Card, values);
        Assert.Contains(PaymentMethod.MercadoPago, values);
    }

    [Fact]
    public void MercadoPago_IsNotMergedIntoCard()
    {
        // A distinct enum member, not a value equal to Card's underlying int.
        Assert.NotEqual((int)PaymentMethod.Card, (int)PaymentMethod.MercadoPago);
    }
}
