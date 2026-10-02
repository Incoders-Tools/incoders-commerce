using System.Text.Json.Serialization;
using Commerce.Domain.Customers;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Input to <see cref="PostgresCustomerStore.CreateAsync"/> — a new customer
/// to insert. No `OrganizationId` (comes from the tenant scope, never a
/// request field), no `IsEnabled` (true at birth), no `CreatedAtUtc`
/// (database default) — commerce-customer-identity design.md "Interfaces /
/// Contracts".
/// </summary>
public sealed record NewCustomer(
    Guid Id,
    CustomerKind CustomerKind,
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
    string? Locality,
    string? Province,
    string? PostalCode,
    string? DeliveryNotes,
    decimal? DiscountPercentage,
    string? PaymentTerms,
    string? Notes,
    Guid CreatedByUserId,
    Guid? CityId = null,
    Guid? BusinessTypeId = null,
    string? ContactName = null);

/// <summary>
/// Fields an edit MAY change. `CustomerKind` is deliberately absent — it is
/// read-only at edit (design.md "Web form shape (create vs. edit)"): it
/// drives which price list applies in Phase C, so flipping it retroactively
/// changes commercial meaning and is a future, deliberate operation.
/// </summary>
public sealed record UpdateCustomer(
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
    string? Locality,
    string? Province,
    string? PostalCode,
    string? DeliveryNotes,
    decimal? DiscountPercentage,
    string? PaymentTerms,
    string? Notes,
    bool IsEnabled,
    ColumnChange<Guid?>? City = null,
    ColumnChange<Guid?>? BusinessType = null,
    ColumnChange<string?>? ContactName = null);

/// <summary>
/// An optional column of an update: <see langword="null"/> (the property is
/// absent) keeps the stored value, a present one replaces it - with
/// <see langword="null"/> clearing the column. Lets clients that predate the
/// column (the POS) PUT a customer without wiping it.
/// </summary>
public sealed record ColumnChange<T>(T Value);

/// <summary>
/// Full persisted shape of one `customers` row, returned by
/// Create/Update/Find/List.
/// </summary>
public sealed record CustomerRecord(
    Guid Id,
    Guid OrganizationId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CustomerKind CustomerKind,
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
    string? Locality,
    string? Province,
    string? PostalCode,
    string? DeliveryNotes,
    decimal? DiscountPercentage,
    string? PaymentTerms,
    string? Notes,
    bool IsEnabled,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId,
    DateTimeOffset UpdatedAtUtc,
    Guid? CityId = null,
    string? CityName = null,
    Guid? BusinessTypeId = null,
    string? BusinessTypeName = null,
    string? ContactName = null);

/// <summary>Optional filters of the customer list; every member is optional and they combine with AND.</summary>
public sealed record CustomerListFilter(string? Search = null, Guid? CityId = null, Guid? BusinessTypeId = null);

/// <summary>
/// Minimum viable pull projection for `GET /device/customers/sync` (Unit 6;
/// design.md "BranchNode cloud->local customer replication"). Deliberately a
/// PROJECTION, not the full aggregate — `Notes`/`DiscountPercentage`/
/// `PaymentTerms` never leave the server.
/// </summary>
public sealed record CustomerReplicaRow(
    Guid CustomerId,
    string DisplayName,
    string CustomerKind,
    string? TaxId,
    string? Phone,
    string? Locality,
    DateTimeOffset UpdatedAtUtc);
