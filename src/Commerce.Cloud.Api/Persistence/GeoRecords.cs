namespace Commerce.Cloud.Api.Persistence;

/// <summary>One row of `provinces` with its country; `Id` is the INDEC province code ("06").</summary>
public sealed record ProvinceRecord(string Id, string IsoCode, string Name, string CountryCode, string CountryName);

/// <summary>
/// One row of the global `cities` table with its province and country.
/// `IndecId` is the Georef locality id and is null for a city a system
/// administrator added by hand. `PostalCode` is optional (CP "2000" or CPA
/// "S2000ABC"), maintained by the system administrator.
/// </summary>
public sealed record CityRecord(
    Guid Id,
    string? IndecId,
    string Name,
    string ProvinceId,
    string ProvinceName,
    string CountryCode,
    string? DepartmentName,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? PostalCode = null);

/// <summary>Filters of the city search; `FoldedSearch` is already folded and LIKE-escaped by the caller.</summary>
public sealed record CitySearch(string? FoldedSearch, string? ProvinceId, bool IncludeInactive, int Limit, int Offset);

/// <summary>A city a system administrator adds (it has no INDEC id).</summary>
public sealed record NewCity(Guid Id, string Name, string ProvinceId, string? DepartmentName, bool IsActive, string? PostalCode = null);

/// <summary>
/// Edit of a city. `ProvinceId`, `DepartmentName` and `IsActive` are null to keep
/// the stored value; a department name that is present but blank clears it.
/// `PostalCode` null keeps the stored value; a change carries the already
/// normalized code, or null to clear it.
/// </summary>
public sealed record UpdateCity(string Name, string? ProvinceId, string? DepartmentName, bool? IsActive, ColumnChange<string?>? PostalCode = null);
