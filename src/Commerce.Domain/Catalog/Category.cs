namespace Commerce.Domain.Catalog;

public sealed class Category
{
    public Guid Id { get; }
    public string Name { get; }
    public Guid? ParentCategoryId { get; }

    public Category(Guid id, string name, Guid? parentCategoryId = null)
    {
        Id = id;
        Name = name;
        ParentCategoryId = parentCategoryId;
    }
}
