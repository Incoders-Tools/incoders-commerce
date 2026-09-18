using Commerce.BranchNode;

namespace Commerce.Pos.Windows;

/// <summary>
/// One selectable row in the optional sale-time customer picker (see
/// <see cref="SaleCustomerPicker"/>). A null <see cref="CustomerId"/> is the
/// walk-in / no-customer sentinel.
/// </summary>
public sealed record SaleCustomerPickerItem(Guid? CustomerId, string Label);

/// <summary>
/// Pure mapping from the local <c>customers_replica</c> cache
/// (<see cref="CustomerReplica"/>, populated by
/// <see cref="MainWindow.PullCustomersAsync"/>) to the items shown in
/// MainWindow's OPTIONAL sale-time customer picker
/// (commerce-customer-identity follow-up, verify-report CRITICAL: "Customer
/// becomes selectable on POS after sync"). Anonymous walk-in retail stays the
/// fastest, zero-friction default: the walk-in sentinel is always present,
/// even with zero synced customers, and always sorts first. No I/O.
/// </summary>
public static class SaleCustomerPicker
{
    public const string WalkInLabel = "Walk-in (no customer)";

    public static IReadOnlyList<SaleCustomerPickerItem> BuildItems(IReadOnlyList<CustomerReplica> customers)
    {
        var items = new List<SaleCustomerPickerItem> { new(null, WalkInLabel) };

        items.AddRange(
            customers
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(c => new SaleCustomerPickerItem(c.CustomerId, c.DisplayName)));

        return items;
    }
}
