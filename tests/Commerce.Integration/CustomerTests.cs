using Commerce.Domain.Customers;
using Commerce.Domain.Ordering;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-customer-identity task 2.3 (customer-registry spec
/// "Organization-Scoped Customer Persistence" and design.md's `Customer`
/// aggregate shape): the `tax_id`/`tax_id_type` invariant, `Enable`/
/// `Disable`, and `Order`'s `Guid.Empty` constructor guard. No I/O.
/// </summary>
public sealed class CustomerTests
{
    private static Customer NewRetailCustomer(TaxIdType taxIdType = TaxIdType.None, string? taxId = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CustomerKind.Retail,
            "Jane Doe",
            legalName: null,
            taxIdType,
            taxId,
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
            paymentTerms: null,
            notes: null,
            createdByUserId: Guid.NewGuid());

    [Fact]
    public void Constructor_TaxIdTypeNone_WithNonNullTaxId_Throws()
    {
        Assert.Throws<ArgumentException>(() => NewRetailCustomer(TaxIdType.None, "20-12345678-9"));
    }

    [Fact]
    public void Constructor_TaxIdTypeCuit_WithNullTaxId_Throws()
    {
        Assert.Throws<ArgumentException>(() => NewRetailCustomer(TaxIdType.Cuit, null));
    }

    [Fact]
    public void Constructor_TaxIdTypeCuit_WithTaxId_Succeeds()
    {
        var customer = NewRetailCustomer(TaxIdType.Cuit, "20-12345678-9");

        Assert.Equal(TaxIdType.Cuit, customer.TaxIdType);
        Assert.Equal("20-12345678-9", customer.TaxId);
    }

    [Fact]
    public void Constructor_TaxIdTypeNone_WithNullTaxId_Succeeds()
    {
        var customer = NewRetailCustomer();

        Assert.Equal(TaxIdType.None, customer.TaxIdType);
        Assert.Null(customer.TaxId);
        Assert.True(customer.IsEnabled);
    }

    [Fact]
    public void Disable_ThenEnable_TogglesIsEnabled()
    {
        var customer = NewRetailCustomer();

        customer.Disable();
        Assert.False(customer.IsEnabled);

        customer.Enable();
        Assert.True(customer.IsEnabled);
    }

    [Fact]
    public void Order_Constructor_EmptyCustomerId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Order(
            Guid.NewGuid(), Guid.NewGuid(), OrderOrigin.RegisteredCustomer, Guid.Empty, guestContact: null, Guid.NewGuid(),
            Array.Empty<OrderLineSnapshot>(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Order_Constructor_NonEmptyCustomerId_Succeeds()
    {
        var customerId = Guid.NewGuid();
        var order = new Order(
            Guid.NewGuid(), Guid.NewGuid(), OrderOrigin.RegisteredCustomer, customerId, guestContact: null, Guid.NewGuid(),
            Array.Empty<OrderLineSnapshot>(), DateTimeOffset.UtcNow);

        Assert.Equal(customerId, order.CustomerId);
    }
}
