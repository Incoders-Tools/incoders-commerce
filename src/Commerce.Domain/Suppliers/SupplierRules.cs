namespace Commerce.Domain.Suppliers;

/// <summary>
/// Pure shape rules of the supplier master data: the bank details (a 22 digit CBU/CVU and a 6-20 character
/// alias of letters, digits, dots and dashes) and the payment term in days. Callers normalize through the
/// <c>TryNormalize*</c> methods so what is stored is canonical; the same shapes are CHECK constraints in
/// `0030_suppliers.sql`.
/// </summary>
public static class SupplierRules
{
    public const int MaxPaymentTermsDays = 365;

    public static bool TryNormalizeBankCbu(string? raw, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var digits = new string(raw.Where(c => c is not (' ' or '-' or '.')).ToArray());
        if (digits.Length != 22 || !digits.All(char.IsAsciiDigit))
        {
            error = "bankCbu must be a CBU/CVU of 22 digits.";
            return false;
        }

        normalized = digits;
        return true;
    }

    public static bool TryNormalizeBankAlias(string? raw, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var alias = raw.Trim();
        if (alias.Length is < 6 or > 20 || !alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
        {
            error = "bankAlias must be 6-20 characters of letters, digits, dots or dashes.";
            return false;
        }

        normalized = alias;
        return true;
    }

    public static bool TryValidatePaymentTermsDays(int? days, out string? error)
    {
        error = null;
        if (days is < 0 or > MaxPaymentTermsDays)
        {
            error = $"paymentTermsDays must be between 0 and {MaxPaymentTermsDays}.";
            return false;
        }

        return true;
    }
}
