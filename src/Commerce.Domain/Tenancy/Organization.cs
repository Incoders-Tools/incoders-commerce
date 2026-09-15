namespace Commerce.Domain.Tenancy;

public sealed class Organization
{
    public Guid Id { get; }
    public string Name { get; }

    public Organization(Guid id, string name)
    {
        Id = id;
        Name = name;
    }
}
