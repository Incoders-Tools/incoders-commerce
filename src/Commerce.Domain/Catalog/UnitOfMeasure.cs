namespace Commerce.Domain.Catalog;

public sealed class UnitOfMeasure
{
    public Guid Id { get; }
    public string Code { get; }
    public string Name { get; }

    public UnitOfMeasure(Guid id, string code, string name)
    {
        Id = id;
        Code = code;
        Name = name;
    }
}
