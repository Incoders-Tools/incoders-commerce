namespace Commerce.Domain.Identity;

/// <summary>
/// The rule for a NEW staff or system-admin password (railway-deploy-pipeline S16): at least
/// <see cref="MinimumLength"/> characters, counted as typed (never trimmed). Applied where a password is written
/// (reset confirm, renewal, user creation, admin reset, organization and first-admin bootstrap,
/// `create-system-admin`), never at sign-in: an existing shorter password still signs in. Customer accounts and PINs
/// have their own rules.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    public static bool IsLongEnough(string? password) => password is not null && password.Length >= MinimumLength;

    /// <summary>The validation message of the API field <paramref name="field"/>.</summary>
    public static string TooShortMessage(string field) => $"{field} must be at least {MinimumLength} characters.";
}
