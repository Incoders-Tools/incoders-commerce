namespace Commerce.Pos.Windows;

/// <summary>
/// One locally-cached PIN-verifiable operator credential (design.md
/// "Interfaces / Contracts"). `Salt`/`Subkey` are the PBKDF2 verifier from
/// <see cref="OperatorPinCredential"/>; the encoded/protected form lives only
/// in <see cref="LocalOperatorStore"/>'s persisted DTO, never here.
/// </summary>
public sealed record CachedOperator(
    Guid UserId, string Email, Guid OrganizationId,
    byte[] Salt, byte[] Subkey, DateTimeOffset LastVerifiedUtc)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(14);

    public bool IsStale(DateTimeOffset now) => now - LastVerifiedUtc > Ttl;
}
