namespace Commerce.Pos.Windows;

/// <summary>
/// One locally-cached PIN-verifiable operator credential (design.md
/// "Interfaces / Contracts"). `Salt`/`Subkey` are the PBKDF2 verifier from
/// <see cref="OperatorPinCredential"/>; the encoded/protected form lives only
/// in <see cref="LocalOperatorStore"/>'s persisted DTO, never here.
/// `Permissions` is the server-derived `int` from `OperatorVerifyResponse`
/// (commerce-customer-identity design.md "Desktop authorization for customer
/// create/edit") — a UX-only affordance for showing/hiding the "Manage
/// customers" button; the server re-checks on every `/customers` call.
/// Defaults to 0 (no permissions ⇒ button hidden) so an older cached entry
/// that predates this field decodes safely.
/// </summary>
public sealed record CachedOperator(
    Guid UserId, string Email, Guid OrganizationId,
    byte[] Salt, byte[] Subkey, DateTimeOffset LastVerifiedUtc, int Permissions = 0)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(14);

    public bool IsStale(DateTimeOffset now) => now - LastVerifiedUtc > Ttl;
}
