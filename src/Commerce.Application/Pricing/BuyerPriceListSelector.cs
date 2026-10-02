namespace Commerce.Application.Pricing;

/// <summary>
/// customer-price-lists T2: which price list prices a sale. The answer depends on the BUYER only, never on the channel
/// or the caller (ADR-010 channel independence), so the rule is one pure function that the cloud API and the POS apply
/// the same way, in front of <see cref="PricingResolutionService"/> (whose ports are bound to the list this returns).
///
/// A walk-in buyer (no customer) is priced from the organization default list. A customer is priced from, in order:
/// the customer's own list, the organization's default list for customers, then the organization default list.
/// <paramref name="isAvailable"/> lets the caller skip a list that cannot be used where the sale happens (price lists
/// are branch scoped, a customer is organization scoped, so a customer's list may belong to another branch).
/// </summary>
public static class BuyerPriceListSelector
{
    public static Guid? Select(
        bool isCustomer,
        Guid? customerPriceListId,
        Guid? organizationDefaultCustomerPriceListId,
        Guid? organizationDefaultPriceListId,
        Func<Guid, bool>? isAvailable = null)
    {
        if (isCustomer)
        {
            foreach (var candidate in new[] { customerPriceListId, organizationDefaultCustomerPriceListId })
            {
                if (candidate is { } id && (isAvailable?.Invoke(id) ?? true))
                {
                    return id;
                }
            }
        }

        return organizationDefaultPriceListId;
    }
}
