namespace Commerce.Domain.Identity;

public sealed class Role
{
    public string Name { get; }
    public Permission Permissions { get; }

    public Role(string name, Permission permissions)
    {
        Name = name;
        Permissions = permissions;
    }
}
