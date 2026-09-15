namespace Commerce.Domain.Catalog;

public sealed class Product
{
    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public string Name { get; }
    public Guid CategoryId { get; }
    public Guid DefaultUnitId { get; }
    public IVerticalCatalogExtension VerticalExtension { get; }

    public Product(
        Guid id,
        Guid organizationId,
        string name,
        Guid categoryId,
        Guid defaultUnitId,
        IVerticalCatalogExtension? verticalExtension = null)
    {
        Id = id;
        OrganizationId = organizationId;
        Name = name;
        CategoryId = categoryId;
        DefaultUnitId = defaultUnitId;
        VerticalExtension = verticalExtension ?? NoVerticalCatalogExtension.Instance;
    }
}
