namespace Commerce.Cloud.Api.Email;

/// <summary>
/// Fallback sender used when `RESEND_API_KEY` is absent (commerce-password-
/// recovery design.md "Missing RESEND_API_KEY"). Writes the message to
/// stdout at `LogInformation` — the exact bootstrap-token delivery precedent
/// (<see cref="Commerce.Cloud.Api.Authentication.BootstrapTokenRegistry"/>'s
/// caller): plaintext reaches only server logs, never HTTP. Keeps
/// `deploy/dev/compose.yaml` and integration tests usable without a Resend
/// account.
/// </summary>
public sealed class LogOnlyEmailSender : IEmailSender
{
    private readonly ILogger<LogOnlyEmailSender> _logger;

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
        return Task.FromResult(true);
    }
}
