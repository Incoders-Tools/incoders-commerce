using System.Text.RegularExpressions;

namespace Commerce.Domain.Validation;

/// <summary>
/// The one email format rule every email field shares (customer, customer contact, staff user, supplier; the web and
/// desktop forms apply the same <see cref="Pattern"/> while typing). Pragmatic, not full RFC 5322: a local part of
/// letters, digits and <c>!#$%&amp;'*+/=?^_`{|}~-</c> in dot-separated runs (no leading, trailing or doubled dot),
/// one <c>@</c>, then at least two domain labels of letters, digits and hyphens that neither start nor end with a
/// hyphen, the last one (the TLD) being 2 or more letters. Case-insensitive, checked on the trimmed value, at most
/// 254 characters with a local part of at most 64.
/// </summary>
public static class EmailAddressRules
{
    public const int MaxLength = 254;
    public const int MaxLocalPartLength = 64;

    /// <summary>
    /// The format, anchored, without length limits (checked separately). Written with constructs that mean the same
    /// in .NET and JavaScript; use it case-insensitively (<c>new RegExp(Pattern, "i")</c>).
    /// </summary>
    public const string Pattern =
        @"^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$";

    private static readonly Regex Format = new(Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= MaxLength
            && trimmed.IndexOf('@') is > 0 and <= MaxLocalPartLength
            && Format.IsMatch(trimmed);
    }

    /// <summary>
    /// For an optional email field: blank is no email (<paramref name="normalized"/> null, valid); otherwise the
    /// trimmed value when it follows the rule. Returns false (and a null <paramref name="normalized"/>) when it does not.
    /// </summary>
    public static bool TryNormalize(string? raw, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!IsValid(raw))
        {
            return false;
        }

        normalized = raw.Trim();
        return true;
    }
}
