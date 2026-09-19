using Commerce.Domain.Ordering;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Row shape for `guest_order_verifications` (commerce-guest-ordering
/// design.md "Interfaces / Contracts"). Returned only by
/// <see cref="PostgresGuestVerificationStore.FindAsync"/>, the deliberately
/// UNSCOPED lookup that resolves a verification before any tenant scope is
/// known — the exact <c>PasswordResetTokenRecord</c> precedent.
/// </summary>
public sealed record GuestVerificationRecord(
    Guid Id,
    Guid OrganizationId,
    string DocumentId,
    GuestContactChannel Channel,
    string ContactAddress,
    string CodeHash,
    int AttemptCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ConsumedAt,
    Guid? ConsumedOrderId);
