using Commerce.Domain.Pricing;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Input to <see cref="PostgresRateComponentStore.PublishSetAsync"/> — a NEW
/// dated set, never an edit of an existing one
/// (commerce-price-composition design.md "Effective dating": append-only, no
/// `EffectiveTo`). No `OrganizationId`: it comes from the tenant scope, never
/// from a request field, exactly as <see cref="NewPriceListEntry"/>.
///
/// <see cref="PriceListId"/> is `null` for the organization's inheritable
/// default set.
///
/// <see cref="Components"/> are already-validated domain
/// <see cref="RateComponent"/> values rather than a parallel DTO: a component
/// cannot reach this record without having declared a calculation base, so
/// the spec's "a component without a declared calculation base MUST be
/// rejected" holds before any SQL is built, and the `calculation_base` CHECK
/// in migration `0013` catches anything that bypasses the constructor.
/// </summary>
public sealed record NewRateComponentSet(
    Guid Id,
    Guid? PriceListId,
    DateOnly EffectiveFrom,
    IReadOnlyList<RateComponent> Components,
    Guid CreatedByUserId);
