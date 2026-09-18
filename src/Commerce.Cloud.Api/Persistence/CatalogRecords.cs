using Commerce.Domain.Catalog;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Input to <see cref="PostgresCatalogStore.CreateProductAsync"/>. No
/// `OrganizationId` (comes from the tenant scope, never a request field) —
/// commerce-pricing-engine design.md "Verified deviation": Work Unit 1
/// replaces the previous request-body-constructed <see cref="Product"/> with
/// real persistence, mirroring <see cref="NewCustomer"/>'s shape.
/// </summary>
public sealed record NewProduct(
    Guid Id,
    string Name,
    Guid CategoryId,
    Guid DefaultUnitId,
    Guid CreatedByUserId);

/// <summary>Fields an edit MAY change on a product.</summary>
public sealed record UpdateProduct(
    string Name,
    Guid CategoryId,
    Guid DefaultUnitId);

/// <summary>Full persisted shape of one `products` row.</summary>
public sealed record ProductRecord(
    Guid Id,
    Guid OrganizationId,
    string Name,
    Guid CategoryId,
    Guid DefaultUnitId,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId,
    DateTimeOffset UpdatedAtUtc)
{
    public Product ToDomain() => new(Id, OrganizationId, Name, CategoryId, DefaultUnitId);
}

/// <summary>
/// Input to <see cref="PostgresCatalogStore.CreatePresentationAsync"/>.
/// `IdentificationCode` is optional — an unlabelled presentation stays
/// unconstrained (design.md "Identification code placement and uniqueness").
/// </summary>
public sealed record NewPresentation(
    Guid Id,
    Guid ProductId,
    string Name,
    QuantityBehavior QuantityBehavior,
    Guid UnitId,
    string? IdentificationCode,
    Guid CreatedByUserId);

/// <summary>Fields an edit MAY change on a presentation, including its code.</summary>
public sealed record UpdatePresentation(
    string Name,
    QuantityBehavior QuantityBehavior,
    Guid UnitId,
    string? IdentificationCode);

/// <summary>Full persisted shape of one `presentations` row.</summary>
public sealed record PresentationRecord(
    Guid Id,
    Guid OrganizationId,
    Guid ProductId,
    string Name,
    QuantityBehavior QuantityBehavior,
    Guid UnitId,
    string? IdentificationCode,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId,
    DateTimeOffset UpdatedAtUtc)
{
    public Presentation ToDomain() => new(Id, ProductId, Name, QuantityBehavior, UnitId, IdentificationCode);
}

/// <summary>
/// Minimum viable pull projection for the future `GET /device/catalog/sync`
/// (Work Unit 6; design.md "BranchNode replication"). A projection, not the
/// full aggregate.
/// </summary>
public sealed record CatalogChangeRow(
    Guid PresentationId,
    Guid ProductId,
    string ProductName,
    string PresentationName,
    string? IdentificationCode,
    QuantityBehavior QuantityBehavior,
    Guid UnitId,
    DateTimeOffset UpdatedAtUtc);
