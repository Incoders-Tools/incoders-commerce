namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Name of the category every organization's products fall back to
/// (catalog-categories spec "Every Product References One Category Of Its
/// Organization"); migration 0018 creates it for organizations that already
/// own products.
/// </summary>
public static class DefaultCategory
{
    public const string Name = "Sin categoría";
    public const string IconKey = "generic";
}

/// <summary>Input to <see cref="PostgresCategoryStore.CreateAsync"/>; the organization comes from the tenant scope.</summary>
public sealed record NewCategory(Guid Id, string Name, string IconKey, bool ShowInPos = true, int PosSortOrder = 0);

/// <summary>Full persisted shape of one `categories` row.</summary>
public sealed record CategoryRecord(
    Guid Id,
    Guid OrganizationId,
    string Name,
    string IconKey,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool ShowInPos = true,
    int PosSortOrder = 0);
