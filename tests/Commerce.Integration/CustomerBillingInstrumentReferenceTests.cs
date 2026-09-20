using Commerce.Domain.Customers;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 4 task 4.1 (commerce-payments design.md "Customer instrument
/// reference", Decision 4): <see cref="Customer.BillingInstrumentReference"/>
/// rejects a 13-19 digit PAN-shaped string via a constructor guard; a
/// non-PAN-shaped value or null is accepted; every existing
/// <see cref="Customer.PaymentTerms"/> behavior is unaffected.
/// </summary>
public sealed class CustomerBillingInstrumentReferenceTests
{
    private static Customer NewCustomer(string? billingInstrumentReference, string? paymentTerms = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CustomerKind.Retail,
            "Jane Doe",
            legalName: null,
            TaxIdType.None,
            taxId: null,
            TaxCondition.ConsumidorFinal,
            phone: "555-1234",
            email: null,
            addressStreet: null,
            addressNumber: null,
            neighborhood: null,
            locality: null,
            province: null,
            postalCode: null,
            deliveryNotes: null,
            discountPercentage: null,
            paymentTerms: paymentTerms,
            notes: null,
            createdByUserId: Guid.NewGuid(),
            billingInstrumentReference: billingInstrumentReference);

    [Theory]
    [InlineData("1234567890123")]      // 13 digits
    [InlineData("1234567890123456789")] // 19 digits
    public void Constructor_PanShapedInstrumentReference_Throws(string panShaped)
    {
        Assert.Throws<ArgumentException>(() => NewCustomer(panShaped));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tok_abc123")]
    [InlineData("123456789012")] // 12 digits: too short to be PAN-shaped
    public void Constructor_NonPanShapedInstrumentReference_Succeeds(string? value)
    {
        var customer = NewCustomer(value);
        Assert.Equal(value, customer.BillingInstrumentReference);
    }

    [Fact]
    public void PaymentTerms_IsUnaffectedByInstrumentReference()
    {
        var customer = NewCustomer(billingInstrumentReference: "tok_xyz", paymentTerms: "Net 30");

        Assert.Equal("Net 30", customer.PaymentTerms);
        Assert.Equal("tok_xyz", customer.BillingInstrumentReference);
    }
}
