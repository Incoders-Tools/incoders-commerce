using Commerce.Domain.Ordering;

namespace Commerce.Integration;

/// <summary>
/// commerce-guest-ordering Phase 1 (Unit 1, "Domain origin"): the paired
/// `Order` construction invariant, `GuestContact`'s blank-field guards,
/// `OrderActors.PublicGuest`, and `Order.DispatchRank` (guest-ordering spec,
/// "Order Construction Invariant" / "Guest Identity Capture on the Order" /
/// "Non-Staff Actor Representation" / "Guest Order Non-Priority Ranking").
/// No I/O.
/// </summary>
public sealed class OrderOriginTests
{
    private static GuestContact NewGuestContact(string documentId = "30-12345678-9", string contactAddress = "guest@example.com") =>
        new(documentId, GuestContactChannel.Email, contactAddress, "Jane Guest", DeliveryNotes: null);

    // --- Requirement: Order Construction Invariant ---------------------------

    [Fact]
    public void Constructor_RegisteredCustomer_WithNoCustomerId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Order(
            orderId: Guid.NewGuid(), organizationId: Guid.NewGuid(), origin: OrderOrigin.RegisteredCustomer, customerId: null,
            guestContact: null, destinationBranchId: Guid.NewGuid(), lines: Array.Empty<OrderLineSnapshot>(), submittedAtUtc: DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Constructor_Guest_WithNonNullCustomerId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Order(
            orderId: Guid.NewGuid(), organizationId: Guid.NewGuid(), origin: OrderOrigin.Guest, customerId: Guid.NewGuid(),
            guestContact: NewGuestContact(), destinationBranchId: Guid.NewGuid(), lines: Array.Empty<OrderLineSnapshot>(), submittedAtUtc: DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Constructor_RegisteredCustomer_WithEmptyCustomerId_Throws()
    {
        // Triangulation: Guid.Empty is still rejected under the new invariant
        // (design.md's constructor snippet: `customerId == Guid.Empty` is
        // folded into the RegisteredCustomer branch, not dropped).
        Assert.Throws<ArgumentException>(() => new Order(
            orderId: Guid.NewGuid(), organizationId: Guid.NewGuid(), origin: OrderOrigin.RegisteredCustomer, customerId: Guid.Empty,
            guestContact: null, destinationBranchId: Guid.NewGuid(), lines: Array.Empty<OrderLineSnapshot>(), submittedAtUtc: DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Constructor_Guest_WithNoGuestContact_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Order(
            orderId: Guid.NewGuid(), organizationId: Guid.NewGuid(), origin: OrderOrigin.Guest, customerId: null,
            guestContact: null, destinationBranchId: Guid.NewGuid(), lines: Array.Empty<OrderLineSnapshot>(), submittedAtUtc: DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Constructor_RegisteredCustomer_WithGuestContact_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Order(
            orderId: Guid.NewGuid(), organizationId: Guid.NewGuid(), origin: OrderOrigin.RegisteredCustomer, customerId: Guid.NewGuid(),
            guestContact: NewGuestContact(), destinationBranchId: Guid.NewGuid(), lines: Array.Empty<OrderLineSnapshot>(), submittedAtUtc: DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Constructor_ValidGuestOrder_Succeeds()
    {
        var contact = NewGuestContact();
        var order = new Order(
            orderId: Guid.NewGuid(), organizationId: Guid.NewGuid(), origin: OrderOrigin.Guest, customerId: null,
            guestContact: contact, destinationBranchId: Guid.NewGuid(), lines: Array.Empty<OrderLineSnapshot>(), submittedAtUtc: DateTimeOffset.UtcNow);

        Assert.Equal(OrderOrigin.Guest, order.Origin);
        Assert.Null(order.CustomerId);
        Assert.Same(contact, order.GuestContact);
    }

    [Fact]
    public void Constructor_ValidRegisteredOrder_Succeeds()
    {
        var customerId = Guid.NewGuid();
        var order = new Order(
            orderId: Guid.NewGuid(), organizationId: Guid.NewGuid(), origin: OrderOrigin.RegisteredCustomer, customerId: customerId,
            guestContact: null, destinationBranchId: Guid.NewGuid(), lines: Array.Empty<OrderLineSnapshot>(), submittedAtUtc: DateTimeOffset.UtcNow);

        Assert.Equal(OrderOrigin.RegisteredCustomer, order.Origin);
        Assert.Equal(customerId, order.CustomerId);
        Assert.Null(order.GuestContact);
    }

    // --- Requirement: Guest Order Non-Priority Ranking (DispatchRank) --------

    [Fact]
    public void DispatchRank_RegisteredCustomer_IsZero()
    {
        var order = new Order(
            orderId: Guid.NewGuid(), organizationId: Guid.NewGuid(), origin: OrderOrigin.RegisteredCustomer, customerId: Guid.NewGuid(),
            guestContact: null, destinationBranchId: Guid.NewGuid(), lines: Array.Empty<OrderLineSnapshot>(), submittedAtUtc: DateTimeOffset.UtcNow);

        Assert.Equal(0, order.DispatchRank);
    }

    [Fact]
    public void DispatchRank_Guest_IsOne()
    {
        var order = new Order(
            orderId: Guid.NewGuid(), organizationId: Guid.NewGuid(), origin: OrderOrigin.Guest, customerId: null,
            guestContact: NewGuestContact(), destinationBranchId: Guid.NewGuid(), lines: Array.Empty<OrderLineSnapshot>(), submittedAtUtc: DateTimeOffset.UtcNow);

        Assert.Equal(1, order.DispatchRank);
    }

    // --- Requirement: Non-Staff Actor Representation --------------------------

    [Fact]
    public void OrderActors_PublicGuest_IsNotEmpty()
    {
        Assert.NotEqual(Guid.Empty, OrderActors.PublicGuest);
    }

    [Fact]
    public void OrderActors_PublicGuest_IsStable()
    {
        // Triangulation: the sentinel is a fixed identity, not freshly
        // generated per read — audit consumers rely on it being constant.
        Assert.Equal(OrderActors.PublicGuest, OrderActors.PublicGuest);
    }

    // --- GuestContact blank-field guards ---------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GuestContact_BlankDocumentId_Throws(string documentId)
    {
        Assert.Throws<ArgumentException>(() => new GuestContact(documentId, GuestContactChannel.Email, "guest@example.com", "Jane Guest", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GuestContact_BlankContactAddress_Throws(string contactAddress)
    {
        Assert.Throws<ArgumentException>(() => new GuestContact("30-12345678-9", GuestContactChannel.Email, contactAddress, "Jane Guest", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GuestContact_BlankDisplayName_Throws(string displayName)
    {
        Assert.Throws<ArgumentException>(() => new GuestContact("30-12345678-9", GuestContactChannel.Email, "guest@example.com", displayName, null));
    }

    [Fact]
    public void GuestContact_ValidFields_Succeeds()
    {
        var contact = NewGuestContact();

        Assert.Equal("30-12345678-9", contact.DocumentId);
        Assert.Equal("guest@example.com", contact.ContactAddress);
        Assert.Equal(GuestContactChannel.Email, contact.Channel);
        Assert.Null(contact.DeliveryNotes);
    }
}
