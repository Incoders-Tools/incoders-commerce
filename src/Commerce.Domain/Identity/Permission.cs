namespace Commerce.Domain.Identity;

[Flags]
public enum Permission
{
    None = 0,
    ViewSales = 1 << 0,
    ManageCatalog = 1 << 1,
    ManageUsers = 1 << 2,
    ManageBranchSettings = 1 << 3
}
