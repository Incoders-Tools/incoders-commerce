namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Row shape for `password_reset_tokens` (commerce-password-recovery
/// design.md "Interfaces / Contracts"). Returned only by
/// <see cref="PostgresPasswordRecoveryStore.FindTokenAsync"/>, the deliberately
/// UNSCOPED lookup that resolves a token before any tenant scope is known.
/// </summary>
public sealed record PasswordResetTokenRecord(
    string TokenHash,
    Guid UserId,
    Guid OrganizationId,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConsumedAt);
