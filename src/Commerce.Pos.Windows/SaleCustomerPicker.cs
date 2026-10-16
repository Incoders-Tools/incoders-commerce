using Commerce.BranchNode;

namespace Commerce.Pos.Windows;

/// <summary>
/// One selectable row in the optional sale-time customer picker (see
/// <see cref="SaleCustomerPicker"/>). A null <see cref="CustomerId"/> is the
/// walk-in / no-customer sentinel. <see cref="Detail"/> is the secondary line
/// (tax id, locality) the search also matches; null when there is nothing to show.
/// </summary>
public sealed record SaleCustomerPickerItem(Guid? CustomerId, string Label, string? Detail = null)
{
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

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
    public const string WalkInLabel = "Consumidor final";

    public static IReadOnlyList<SaleCustomerPickerItem> BuildItems(IReadOnlyList<CustomerReplica> customers)
    {
        var items = new List<SaleCustomerPickerItem> { new(null, WalkInLabel) };

        items.AddRange(
            customers
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(c => new SaleCustomerPickerItem(c.CustomerId, c.DisplayName, DetailOf(c))));

        return items;
    }

    /// <summary>
    /// The rows matching what the operator typed: every word of <paramref name="term"/> must appear in the name or the
    /// detail, ignoring case and accents ("jose cab" finds "José Cabrera"), and a tax id matches with or without its
    /// separators. The walk-in row always stays first, so going back to it never needs a search. A blank term keeps every
    /// row, in order.
    /// </summary>
    public static IReadOnlyList<SaleCustomerPickerItem> Filter(IReadOnlyList<SaleCustomerPickerItem> items, string? term)
    {
        var words = BranchSyncStore.FoldText(term).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(NormalizeWord).ToArray();
        if (words.Length == 0)
        {
            return items;
        }

        return items
            .Where(item => item.CustomerId is null || words.All(word => SearchTextOf(item).Contains(word, StringComparison.Ordinal)))
            .ToList();
    }

    private static string? DetailOf(CustomerReplica customer)
    {
        var parts = new[] { customer.TaxId, customer.Locality }.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>A word of only digits and tax id separators ("30-12.345") is searched by its digits.</summary>
    private static string NormalizeWord(string word) =>
        word.Any(char.IsDigit) && word.All(ch => char.IsDigit(ch) || ch is '-' or '.' or '/')
            ? new string(word.Where(char.IsDigit).ToArray())
            : word;

    private static string SearchTextOf(SaleCustomerPickerItem item)
    {
        var folded = BranchSyncStore.FoldText($"{item.Label} {item.Detail}");
        var digitsOnly = new string(folded.Where(char.IsDigit).ToArray());
        return $"{folded} {digitsOnly}";
    }
}
