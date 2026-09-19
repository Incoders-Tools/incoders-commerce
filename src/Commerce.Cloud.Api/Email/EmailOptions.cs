namespace Commerce.Cloud.Api.Email;

/// <summary>
/// Flat environment-variable configuration for outbound transactional email
/// (commerce-password-recovery design.md "Email seam" / "Missing
/// RESEND_API_KEY"), read the same way `Program.cs` already reads `PORT` —
/// direct <see cref="Environment.GetEnvironmentVariable(string)"/> calls, no
/// bound `IOptions&lt;T&gt;` section.
/// </summary>
public sealed record EmailOptions(string? ResendApiKey, string FromAddress, string PublicBaseUrl)
{
    public static EmailOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("RESEND_API_KEY"),
        Environment.GetEnvironmentVariable("EMAIL_FROM_ADDRESS") ?? "no-reply@example.com",
        Environment.GetEnvironmentVariable("PUBLIC_BASE_URL") ?? "http://localhost:5173");
}
