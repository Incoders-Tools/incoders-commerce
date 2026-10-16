using System.Globalization;
using System.Text;

namespace Commerce.Domain.Customers;

/// <summary>
/// Derives the stable slug (`key`) of a city or business type from its name:
/// lowercase, accents stripped, every run of non-alphanumerics collapsed to a
/// single underscore, no leading/trailing underscore. Empty when the name has
/// nothing keyable (the caller treats that as a validation error).
/// </summary>
public static class MasterDataKey
{
    public static string FromName(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSeparator = false;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (ch < 128 && char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('_');
                }
                pendingSeparator = false;
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString();
    }
}
