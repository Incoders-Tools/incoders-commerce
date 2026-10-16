namespace Commerce.Domain.Pricing;

/// <summary>
/// A named, org-scoped collection of <see cref="PriceListEntry"/> rows
/// (commerce-pricing-engine design.md "Which price list resolves"). Exactly
/// one list per organization may be <see cref="IsDefault"/> — resolution
/// always reads the org's default list; the guest/registered price
/// divergence comes only from the customer's discount, never from a
/// different list. Enforced at the database by a partial unique index
/// (`price_lists_one_default`), not merely by this class.
/// </summary>
public sealed class PriceList
{
    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public string Name { get; }
    public bool IsDefault { get; }

    public PriceList(Guid id, Guid organizationId, string name, bool isDefault)
    {
        Id = id;
        OrganizationId = organizationId;
        Name = name;
        IsDefault = isDefault;
    }
}
