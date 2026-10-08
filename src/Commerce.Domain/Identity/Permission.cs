namespace Commerce.Domain.Identity;

[Flags]
public enum Permission
{
    None = 0,
    ViewSales = 1 << 0,
    ManageCatalog = 1 << 1,
    ManageUsers = 1 << 2,
    ManageBranchSettings = 1 << 3,

    /// <summary>Signs into and operates the point-of-sale terminal (cashier, business-admin).</summary>
    OperatePos = 1 << 4,

    /// <summary>Takes an order for a customer of the organization from the web (seller, business-admin).</summary>
    TakeOrders = 1 << 5
}
