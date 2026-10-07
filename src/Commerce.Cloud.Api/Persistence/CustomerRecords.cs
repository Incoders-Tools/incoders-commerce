using System.Text.Json.Serialization;
using Commerce.Domain.Customers;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Input to <see cref="PostgresCustomerStore.CreateAsync"/> — a new customer
/// to insert. No `OrganizationId` (comes from the tenant scope, never a
/// request field), no `IsEnabled` (true at birth), no `CreatedAtUtc`
/// (database default) — commerce-customer-identity design.md "Interfaces /
/// Contracts". A null `PartyType` takes the default for the tax id type
/// (<see cref="PartyTypeRules.DefaultFor"/>). The admin API passes null
/// `LegalName`, `Locality` and `Province`: it no longer writes them.
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
    IReadOnlyList<CustomerContactInput>? Contacts = null,
    Guid? PriceListId = null,
    PartyType? PartyType = null,
    int? PaymentTermsDays = null);

/// <summary>
/// One contact person of a customer as written by create/update. `Id` is null for
/// a new contact (a fresh id is generated); a present id is kept: it updates the
/// customer's existing contact with that id, or inserts a new one with it.
/// </summary>
public sealed record CustomerContactInput(
    Guid? Id,
    string FirstName,
    string? LastName,
    string? Phone,
    string? Email,
    string? Role,
    bool IsPrimary,
    int SortOrder);

/// <summary>One persisted contact person of a customer, as returned inside <see cref="CustomerRecord"/>.</summary>
public sealed record CustomerContactRecord(
    Guid Id,
    string FirstName,
    string? LastName,
    string? Phone,
    string? Email,
    string? Role,
    bool IsPrimary,
    int SortOrder);

/// <summary>
/// Thrown by the customer store when a contact id of a create/update cannot be
/// used: it belongs to another customer or organization. The transaction is rolled
/// back; the endpoint answers 400 on `contacts`.
/// </summary>
public sealed class CustomerContactRejectedException(string message) : Exception(message);

/// <summary>
/// Fields an edit MAY change. `CustomerKind` is deliberately absent — it is
/// read-only at edit (design.md "Web form shape (create vs. edit)"): it
/// drives which price list applies in Phase C, so flipping it retroactively
/// changes commercial meaning and is a future, deliberate operation.
/// `Contacts` null keeps the stored contact set; a list REPLACES it.
/// `PartyType` null keeps the stored one. `legal_name`, `locality` and
/// `province` are not editable any more (admin-console-field-fixes): an
/// update leaves whatever is stored.
/// </summary>
public sealed record UpdateCustomer(
    string DisplayName,
    TaxIdType TaxIdType,
    string? TaxId,
    TaxCondition TaxCondition,
    string? Phone,
    string? Email,
    string? AddressStreet,
    string? AddressNumber,
    string? Neighborhood,
    string? PostalCode,
    string? DeliveryNotes,
    decimal? DiscountPercentage,
    string? PaymentTerms,
    string? Notes,
    bool IsEnabled,
    ColumnChange<Guid?>? City = null,
    ColumnChange<Guid?>? BusinessType = null,
    IReadOnlyList<CustomerContactInput>? Contacts = null,
    DateTimeOffset? ExpectedUpdatedAtUtc = null,
    ColumnChange<Guid?>? PriceList = null,
    PartyType? PartyType = null,
    ColumnChange<int?>? PaymentTermsDays = null);

/// <summary>
/// Thrown by <see cref="PostgresCustomerStore.UpdateAsync"/> when the update
/// carried an `ExpectedUpdatedAtUtc` that no longer matches the stored row
/// (someone else saved it first). The transaction is rolled back; the endpoint
/// answers 409 `customer-modified`.
/// </summary>
public sealed class CustomerModifiedException(Guid customerId)
    : Exception($"Customer {customerId} was modified after the supplied expectedUpdatedAtUtc.");

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
    string? ProvinceId = null,
    string? ProvinceName = null,
    Guid? PriceListId = null,
    string? PriceListName = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] PartyType PartyType = PartyType.Person,
    int? PaymentTermsDays = null)
{
    /// <summary>The customer's contact people (never null), primary first by flag, ordered by `sortOrder`.</summary>
    public IReadOnlyList<CustomerContactRecord> Contacts { get; init; } = [];
}

/// <summary>Optional filters of the customer list; every member is optional and they combine with AND.</summary>
public sealed record CustomerListFilter(string? Search = null, Guid? CityId = null, Guid? BusinessTypeId = null);

/// <summary>
/// Minimum viable pull projection for `GET /device/customers/sync` (Unit 6;
/// design.md "BranchNode cloud->local customer replication"). Deliberately a
/// PROJECTION, not the full aggregate — `Notes`/`PaymentTerms` never leave
/// the server. `DiscountPercentage` is a pricing input, so it travels with the
/// `price-lists` snapshot (<see cref="CustomerDiscountAssignment"/>), not here.
/// </summary>
public sealed record CustomerReplicaRow(
    Guid CustomerId,
    string DisplayName,
    string CustomerKind,
    string? TaxId,
    string? Phone,
    string? Locality,
    DateTimeOffset UpdatedAtUtc);

/// <summary>One customer and the price list it is priced from (`price-lists` replica snapshot).</summary>
public sealed record CustomerPriceListAssignment(Guid CustomerId, Guid PriceListId);

/// <summary>One customer and its own payment terms in days (`price-lists` replica snapshot).</summary>
public sealed record CustomerTermsAssignment(Guid CustomerId, int Days);

/// <summary>One customer and its own discount percentage, applied after the list composition (`price-lists` replica snapshot).</summary>
public sealed record CustomerDiscountAssignment(Guid CustomerId, decimal DiscountPercentage);
