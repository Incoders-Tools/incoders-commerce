using System.Security.Cryptography;
using System.Text;
using Commerce.Cloud.Api.Email;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;

namespace Commerce.Cloud.Api.Ordering;

/// <summary>
/// Result of a confirm attempt (public-order-surface spec.md "Guest
/// Verification Gate Before Admission"). Every failure branch — unknown,
/// expired, already-consumed, attempt-exhausted, or a wrong code — collapses
/// to the SAME <see cref="InvalidOrExpired"/> value, so no branch can be used
/// as an oracle to distinguish "wrong code" from "no such verification".
/// </summary>
public enum GuestVerificationConfirmResult
{
    Confirmed,
    InvalidOrExpired,
}

/// <summary>
/// Code generation, hashing, expiry/attempt policy, and email composition
/// for guest order verification (commerce-guest-ordering design.md
/// "Verification state shape" / Data Flow). Composes
/// <see cref="PostgresGuestVerificationStore"/> for persistence and
/// <see cref="IEmailSender"/> for delivery — the exact
/// commerce-password-recovery seam, reused rather than duplicated.
/// </summary>
public sealed class GuestVerificationService
{
    private static readonly TimeSpan CodeExpiry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ConfirmToSubmitTtl = TimeSpan.FromMinutes(30);
    private const int MaxAttempts = 5;

    private readonly PostgresGuestVerificationStore _store;
    private readonly IEmailSender _emailSender;
    private readonly Func<DateTimeOffset> _clock;

    public GuestVerificationService(
        PostgresGuestVerificationStore store, IEmailSender emailSender, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _emailSender = emailSender;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Generates a 6-digit code via <see cref="RandomNumberGenerator"/>,
    /// persists only its SHA-256 hash, and emails the plaintext code.
    /// ALWAYS returns the verification id regardless of email delivery
    /// outcome (design.md Data Flow: "ALWAYS 202 {verificationId}") — a
    /// failing <see cref="IEmailSender"/> still issues the row (threat matrix
    /// "Process integration").
    /// </summary>
    public async Task<Guid> RequestAsync(
        CloudTenantScope scope, string documentId, GuestContactChannel channel, string contactAddress,
        CancellationToken ct)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var codeHash = Hash(code);
        var expiresAt = _clock().Add(CodeExpiry);

        var verificationId = await _store.IssueAsync(scope, documentId, channel, contactAddress, codeHash, expiresAt, ct);

        var textBody =
            $"Your Commerce guest order verification code is {code}. " +
            "This code expires in 10 minutes and can be used once.";
        var htmlBody = $"<p>Your Commerce guest order verification code is <strong>{code}</strong>.</p>" +
            "<p>This code expires in 10 minutes and can be used once.</p>";

        // Delivery outcome does not gate issuance — the reset-password/request
        // precedent verbatim.
        await _emailSender.SendAsync(
            new EmailMessage(contactAddress, "Your Commerce guest order verification code", htmlBody, textBody), ct);

        return verificationId;
    }

    /// <summary>
    /// Confirms a code. Every failure branch returns the SAME
    /// <see cref="GuestVerificationConfirmResult.InvalidOrExpired"/>: unknown
    /// id, expired, already confirmed/consumed, attempts exhausted, or a
    /// mismatched code (which additionally increments `attempt_count`, the
    /// 5th such increment burning the row via the table's own CHECK
    /// constraint boundary).
    /// </summary>
    public async Task<GuestVerificationConfirmResult> ConfirmAsync(Guid verificationId, string code, CancellationToken ct)
    {
        var record = await _store.FindAsync(verificationId, ct);
        var now = _clock();

        if (!IsPending(record, now))
        {
            return GuestVerificationConfirmResult.InvalidOrExpired;
        }

        var scope = new CloudTenantScope(record!.OrganizationId);
        var codeHash = Hash(code);
        if (!FixedTimeEquals(codeHash, record.CodeHash))
        {
            await _store.RecordAttemptAsync(scope, verificationId, ct);
            return GuestVerificationConfirmResult.InvalidOrExpired;
        }

        await _store.ConfirmAsync(scope, verificationId, ct);
        return GuestVerificationConfirmResult.Confirmed;
    }

    /// <summary>
    /// Consumes a confirmed ticket immediately before
    /// <c>CloudOrderStore.Submit</c> (design.md Data Flow), so one
    /// confirmation admits exactly one order. Requires the confirmed ticket's
    /// document id and contact address to MATCH the submitted ones, and the
    /// 30-minute confirm-to-submit TTL to not have elapsed. Returns false —
    /// leaving the row untouched — for every failure branch; the caller (Unit
    /// 4) is responsible for denying the whole order without consuming the
    /// verification on a `no-effective-price` denial.
    /// </summary>
    public async Task<bool> TryConsumeAsync(
        Guid verificationId, string documentId, string contactAddress, Guid orderId, CancellationToken ct)
    {
        var record = await _store.FindAsync(verificationId, ct);
        var now = _clock();

        if (record is null || record.ConsumedAt is not null || record.ConfirmedAt is null)
        {
            return false;
        }

        if (record.ConfirmedAt.Value.Add(ConfirmToSubmitTtl) <= now)
        {
            return false;
        }

        if (!string.Equals(record.DocumentId, documentId, StringComparison.Ordinal) ||
            !string.Equals(record.ContactAddress, contactAddress, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await _store.ConsumeAsync(new CloudTenantScope(record.OrganizationId), verificationId, orderId, ct);
        return true;
    }

    private static bool IsPending(GuestVerificationRecord? record, DateTimeOffset now) =>
        record is not null
        && record.ConsumedAt is null
        && record.ConfirmedAt is null
        && record.ExpiresAt > now
        && record.AttemptCount < MaxAttempts;

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
