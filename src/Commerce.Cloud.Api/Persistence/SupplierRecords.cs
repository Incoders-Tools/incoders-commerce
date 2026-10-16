using System.Text.Json.Serialization;
using Commerce.Domain.Customers;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Input to <see cref="PostgresSupplierStore.CreateAsync"/>: a new supplier. No `OrganizationId` (it comes from
/// the tenant scope, never from the request) and no `IsEnabled` (true at birth).
/// </summary>
public sealed record NewSupplier(
    Guid Id,
    string DisplayName,
    string? LegalName,
    TaxIdType TaxIdType,
    string? TaxId,
    TaxCondition TaxCondition,
    string? Phone,
    string? Email,
    string? AddressStreet,
    string? AddressNumber,
    string? Neighborhood,
    string? PostalCode,
    Guid? CityId,
    Guid? CategoryId,
    int? PaymentTermsDays,
    string? BankCbu,
    string? BankAlias,
    string? Notes,
    Guid CreatedByUserId,
    IReadOnlyList<SupplierContactInput>? Contacts = null);

/// <summary>
/// Fields an edit changes. Plain fields REPLACE the stored value (null clears it); `IsEnabled`, `City`, `Category`
/// and `Contacts` keep the stored value when null/absent. `Contacts` REPLACES the whole set when present.
/// `ExpectedUpdatedAtUtc` is the optimistic-concurrency token (null skips the check).
/// </summary>
public sealed record UpdateSupplier(
    string DisplayName,
    string? LegalName,
    TaxIdType TaxIdType,
    string? TaxId,
    TaxCondition TaxCondition,
    string? Phone,
    string? Email,
    string? AddressStreet,
    string? AddressNumber,
    string? Neighborhood,
    string? PostalCode,
    int? PaymentTermsDays,
    string? BankCbu,
    string? BankAlias,
    string? Notes,
    bool? IsEnabled,
    ColumnChange<Guid?>? City = null,
    ColumnChange<Guid?>? Category = null,
    IReadOnlyList<SupplierContactInput>? Contacts = null,
    DateTimeOffset? ExpectedUpdatedAtUtc = null);

/// <summary>One contact person as written by create/update; a null `Id` is a new contact, a present one is kept.</summary>
public sealed record SupplierContactInput(
    Guid? Id,
    string FirstName,
    string? LastName,
    string? Phone,
    string? Email,
    string? Role,
    bool IsPrimary,
    int SortOrder);

/// <summary>One persisted contact person of a supplier.</summary>
public sealed record SupplierContactRecord(
    Guid Id,
    string FirstName,
    string? LastName,
    string? Phone,
    string? Email,
    string? Role,
    bool IsPrimary,
    int SortOrder);

/// <summary>A contact id of a create/update cannot be used (it belongs to another supplier or organization): 400 on `contacts`.</summary>
public sealed class SupplierContactRejectedException(string message) : Exception(message);

/// <summary>The update carried an `ExpectedUpdatedAtUtc` that no longer matches the stored row: 409 `supplier-modified`.</summary>
public sealed class SupplierModifiedException(Guid supplierId)
    : Exception($"Supplier {supplierId} was modified after the supplied expectedUpdatedAtUtc.");

/// <summary>
/// Full persisted shape of one `suppliers` row with the resolved city, province and category names.
/// `Balance` is derived from the current-account ledger: what the business owes the supplier (credits minus debits).
/// </summary>
public sealed record SupplierRecord(
    Guid Id,
    Guid OrganizationId,
    string DisplayName,
    string? LegalName,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] TaxIdType TaxIdType,
    string? TaxId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] TaxCondition TaxCondition,
    string? Phone,
    string? Email,
    string? AddressStreet,
    string? AddressNumber,
    string? Neighborhood,
    string? PostalCode,
    Guid? CityId,
    string? CityName,
    string? ProvinceId,
    string? ProvinceName,
    Guid? CategoryId,
    string? CategoryName,
    int? PaymentTermsDays,
    string? BankCbu,
    string? BankAlias,
    string? Notes,
    bool IsEnabled,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId,
    DateTimeOffset UpdatedAtUtc,
    decimal Balance)
{
    /// <summary>The supplier's contact people (never null), ordered by `sortOrder`.</summary>
    public IReadOnlyList<SupplierContactRecord> Contacts { get; init; } = [];
}

/// <summary>Optional list filters; every member is optional and they combine with AND.</summary>
public sealed record SupplierListFilter(string? Search = null, Guid? CityId = null, Guid? CategoryId = null, bool? Enabled = null);
