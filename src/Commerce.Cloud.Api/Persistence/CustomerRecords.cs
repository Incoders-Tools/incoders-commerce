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
    Guid CreatedByUserId);

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
    bool IsEnabled);

/// <summary>
/// Full persisted shape of one `customers` row, returned by
/// Create/Update/Find/List.
/// </summary>
public sealed record CustomerRecord(
    Guid Id,
    Guid OrganizationId,
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
    bool IsEnabled,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId,
    DateTimeOffset UpdatedAtUtc);

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
