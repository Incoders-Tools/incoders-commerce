namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Input to <see cref="PostgresOrganizationStore.TryCreateBootstrapAsync"/> —
/// the organization row to insert (design.md "Interfaces / Contracts").
/// </summary>
public sealed record NewOrganization(Guid Id, string Name);

/// <summary>
/// Input to <see cref="PostgresOrganizationStore.TryCreateBootstrapAsync"/> —
/// the one default branch created alongside the organization at bootstrap.
/// </summary>
public sealed record NewBranch(Guid Id, string Name);

/// <summary>
/// Result of a bootstrap-creation attempt (design.md "Interfaces /
/// Contracts"). Every non-<see cref="Created"/> value means the transaction
/// rolled back with zero rows written.
/// </summary>
public enum BootstrapOutcome
{
    Created,
    OrganizationAlreadyExists,
    OrganizationAlreadyHasUsers,
    EmailAlreadyRegistered,
}
