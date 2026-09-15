namespace Commerce.Domain.Tenancy;

public sealed class Branch
{
    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public string Name { get; }

    public Branch(Guid id, Guid organizationId, string name)
    {
        Id = id;
        OrganizationId = organizationId;
        Name = name;
    }
}
