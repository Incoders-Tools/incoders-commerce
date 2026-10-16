using System.Collections.Concurrent;

namespace Commerce.Cloud.Api.Email;

/// <summary>
/// Fallback sender used when `RESEND_API_KEY` is absent (commerce-password-
/// recovery design.md "Missing RESEND_API_KEY"). Writes the message to
/// stdout at `LogInformation` — the exact bootstrap-token delivery precedent
/// (<see cref="Commerce.Cloud.Api.Authentication.BootstrapTokenRegistry"/>'s
/// caller): plaintext reaches only server logs, never HTTP. Keeps
/// `deploy/dev/compose.yaml` and integration tests usable without a Resend
/// account.
///
/// Phase 8 follow-up B (commerce-guest-ordering verify-report.md WARNING 2):
/// this sender ALSO retains the last message sent to each recipient address
/// in memory (never persisted, never exposed outside this process), so a
/// Development-only HTTP seam (<c>TestSeedEndpoints.MapTestSeedEndpoints</c>,
/// route <c>GET /internal/test-seed/guest-verification-code</c>) can read a
/// guest's verification code back out — mirroring the existing
/// <c>/internal/test-seed/user</c> precedent's "skip the out-of-band hop a
/// browser test harness cannot retrieve" rationale. This adds no new
/// production capability: <see cref="ResendEmailSender"/> (used whenever
/// `RESEND_API_KEY` IS configured) never retains anything, so the seam is
/// inert outside Development/CI, where this sender is the one registered.
/// </summary>
public sealed class LogOnlyEmailSender : IEmailSender
{
    private readonly ILogger<LogOnlyEmailSender> _logger;
    private readonly ConcurrentDictionary<string, EmailMessage> _lastMessageByRecipient = new(StringComparer.OrdinalIgnoreCase);

    public LogOnlyEmailSender(ILogger<LogOnlyEmailSender> logger)
    {
        _logger = logger;
        _logger.LogWarning(
            "RESEND_API_KEY is not configured; password-recovery emails will be logged instead of sent.");
    }

    public Task<bool> SendAsync(EmailMessage message, CancellationToken ct)
    {
        _logger.LogInformation(
            "Email to {To} (subject: {Subject}):\n{TextBody}", message.To, message.Subject, message.TextBody);
        _lastMessageByRecipient[message.To] = message;
        return Task.FromResult(true);
    }

    /// <summary>
    /// Test-only read-back (Phase 8 follow-up B): the most recent message
    /// sent to <paramref name="to"/>, or null if none was ever sent in this
    /// process. Case-insensitive on the recipient address.
    /// </summary>
    public EmailMessage? TryGetLastMessage(string to) =>
        _lastMessageByRecipient.TryGetValue(to, out var message) ? message : null;
}
