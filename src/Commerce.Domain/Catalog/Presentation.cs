namespace Commerce.Domain.Catalog;

/// <summary>
/// A sellable presentation of a Product (e.g. "6-pack", "1kg bag"), carrying
/// its own quantity behavior and unit so Product/Presentation reuse holds
/// across local and web management surfaces and across order snapshots (ADR-003).
/// </summary>
public sealed class Presentation
{
    public Guid Id { get; }
    public Guid ProductId { get; }
    public string Name { get; }
    public QuantityBehavior QuantityBehavior { get; }
    public Guid UnitId { get; }

    /// <summary>
    /// Barcode/SKU (commerce-pricing-engine design.md "Identification code
    /// placement and uniqueness"): per Presentation, not per Product, so a
    /// scan resolves to exactly one sellable line. Nullable — an unlabelled
    /// presentation stays unconstrained. Uniqueness is enforced by the
    /// database (`presentations_org_code_uk`, org-scoped, partial), never by
    /// this class or by UI code.
    /// </summary>
    public string? IdentificationCode { get; }

    public Presentation(
        Guid id,
        Guid productId,
        string name,
        QuantityBehavior quantityBehavior,
        Guid unitId,
        string? identificationCode = null)
    {
        Id = id;
        ProductId = productId;
        Name = name;
        QuantityBehavior = quantityBehavior;
        UnitId = unitId;
        IdentificationCode = identificationCode;
    }
}
