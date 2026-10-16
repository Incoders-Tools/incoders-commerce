using System.Text.RegularExpressions;

namespace Commerce.Domain.Geography;

/// <summary>
/// The optional postal code of a city (admin-console-field-fixes T2): an Argentine CP of 4 digits ("2000") or a CPA,
/// the province letter (every ISO 3166-2:AR letter, so no I or O), 4 digits and 3 letters ("S2000ABC"). Mirrored by
/// the database CHECK <c>cities_postal_code_format_ck</c> (0039). Trimmed and upper-cased; blank means none.
/// </summary>
public static class PostalCodeRules
{
    public const string Pattern = "^([0-9]{4}|[A-HJ-NP-Z][0-9]{4}[A-Z]{3})$";

    private static readonly Regex Format = new(Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryNormalize(string? raw, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var candidate = raw.Trim().ToUpperInvariant();
        if (!Format.IsMatch(candidate))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
