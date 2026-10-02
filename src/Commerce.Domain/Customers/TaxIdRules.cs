namespace Commerce.Domain.Customers;

/// <summary>
/// Shape of the tax id per <see cref="TaxIdType"/>: DNI is 7-8 digits, CUIT
/// and CUIL are 11 digits, None carries no value. Callers normalize through
/// <see cref="TryNormalize"/> (separators such as dots, dashes and spaces are
/// dropped) so what is stored is digits only.
/// </summary>
public static class TaxIdRules
{
    public static bool IsValidDigits(TaxIdType type, string digits) => type switch
    {
        TaxIdType.Dni => digits.Length is 7 or 8 && digits.All(char.IsAsciiDigit),
        TaxIdType.Cuit or TaxIdType.Cuil => digits.Length == 11 && digits.All(char.IsAsciiDigit),
        _ => false,
    };

    public static bool TryNormalize(TaxIdType type, string? raw, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        var hasValue = !string.IsNullOrWhiteSpace(raw);

        if (type == TaxIdType.None)
        {
            if (hasValue)
            {
                error = "taxId must be empty when taxIdType is None.";
                return false;
            }
            return true;
        }

        if (!hasValue)
        {
            error = "taxId is required exactly when taxIdType is not None.";
            return false;
        }

        var digits = new string(raw!.Where(c => c is not ('.' or '-' or ' ')).ToArray());
        if (!IsValidDigits(type, digits))
        {
            error = type == TaxIdType.Dni
                ? "taxId must be a DNI of 7 or 8 digits."
                : "taxId must be a CUIT/CUIL of 11 digits.";
            return false;
        }

        normalized = digits;
        return true;
    }
}
