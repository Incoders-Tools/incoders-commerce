using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers the commerce-customer-identity follow-up (verify-report CRITICAL:
/// "Customer becomes selectable on POS after sync"): pure list-to-picker-item
/// mapping used by <c>MainWindow</c>'s optional sale-time customer picker. No
/// I/O, no WPF — the walk-in default MUST always be first and MUST always be
/// present, even with zero synced customers.
/// </summary>
public sealed class SaleCustomerPickerTests
{
    private static CustomerReplica MakeCustomer(string displayName) =>
        new(Guid.NewGuid(), Guid.NewGuid(), displayName, "Retail", null, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public void BuildItems_EmptyReplica_ReturnsOnlyWalkInDefault()
    {
        var items = SaleCustomerPicker.BuildItems([]);

        var item = Assert.Single(items);
        Assert.Null(item.CustomerId);
        Assert.Equal(SaleCustomerPicker.WalkInLabel, item.Label);
    }

    [Fact]
    public void BuildItems_WithCustomers_WalkInIsFirst_RemainderSortedByDisplayName()
    {
        var zebra = MakeCustomer("Zebra Co");
        var acme = MakeCustomer("Acme Co");

        var items = SaleCustomerPicker.BuildItems([zebra, acme]);

        Assert.Equal(3, items.Count);
        Assert.Null(items[0].CustomerId);
        Assert.Equal(SaleCustomerPicker.WalkInLabel, items[0].Label);
        Assert.Equal(acme.CustomerId, items[1].CustomerId);
        Assert.Equal(zebra.CustomerId, items[2].CustomerId);
    }
}
