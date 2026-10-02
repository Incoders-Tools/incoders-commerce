namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Input to the master data stores' Create: one entry of an organization-owned
/// catalog (a city or a business type). The organization comes from the tenant
/// scope, never from the request.
/// </summary>
public sealed record NewMasterDataEntry(Guid Id, string Name, string Key, int SortOrder, bool IsActive);

/// <summary>
/// Input to the master data stores' Update. `Key` and `SortOrder` are null to
/// keep the stored value; `Name` and `IsActive` always replace it.
/// </summary>
public sealed record UpdateMasterDataEntry(string Name, string? Key, int? SortOrder, bool IsActive);

/// <summary>Full persisted shape of one `cities` / `business_types` row.</summary>
public sealed record MasterDataRecord(
    Guid Id,
    Guid OrganizationId,
    string Name,
    string Key,
    int SortOrder,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
