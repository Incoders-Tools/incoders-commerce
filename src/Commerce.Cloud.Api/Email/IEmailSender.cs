namespace Commerce.Cloud.Api.Email;

/// <summary>
/// Transactional email seam (commerce-password-recovery design.md "Email
/// seam"). No test ever reaches the network — tests substitute a fake
/// implementation.
/// </summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

public interface IEmailSender
{
    Task<bool> SendAsync(EmailMessage message, CancellationToken ct);
}
