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

    public Presentation(
        Guid id,
        Guid productId,
        string name,
        QuantityBehavior quantityBehavior,
        Guid unitId)
    {
        Id = id;
        ProductId = productId;
        Name = name;
        QuantityBehavior = quantityBehavior;
        UnitId = unitId;
    }
}
