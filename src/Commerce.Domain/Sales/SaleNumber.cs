using System.Globalization;
using System.Text.RegularExpressions;
using Commerce.Domain.Tenancy;

namespace Commerce.Domain.Sales;

/// <summary>
/// The human number of a POS sale: `V{branch}-C{register}-{sequence}`, for example
/// `V01-C2-125`. `V` is the document type (venta), the branch is its two-digit-minimum
/// <see cref="BranchCode"/>, `C{register}` is the terminal's <see cref="RegisterNumber"/>
/// and the sequence counts that register's sales from 1. The terminal assigns the
/// sequence offline in the sale transaction; the pair (branch, register) names one
/// installation forever, which is what keeps numbers unique without coordination.
/// This type is the single place that formats and parses it.
/// </summary>
public readonly partial record struct SaleNumber
{
    public const char TypeLetter = 'V';

    public BranchCode Branch { get; }
    public RegisterNumber Register { get; }
    public int Sequence { get; }

    public SaleNumber(BranchCode branch, RegisterNumber register, int sequence)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "A sale sequence starts at 1.");
        }
        Branch = branch;
        Register = register;
        Sequence = sequence;
    }

    /// <summary>`V01-C2-125`.</summary>
    public string Format() =>
        $"{TypeLetter}{Branch.Format()}-{Register.Format()}-{Sequence.ToString(CultureInfo.InvariantCulture)}";

    public override string ToString() => Format();

    public static bool TryParse(string? text, out SaleNumber number)
    {
        number = default;
        if (text is null) return false;

        var match = Pattern().Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups["branch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var branch)
            || !int.TryParse(match.Groups["register"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var register)
            || !int.TryParse(match.Groups["sequence"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || branch is < BranchCode.MinValue or > BranchCode.MaxValue
            || register is < RegisterNumber.MinValue or > RegisterNumber.MaxValue
            || sequence < 1)
        {
            return false;
        }

        number = new SaleNumber(new BranchCode(branch), new RegisterNumber(register), sequence);
        return true;
    }

    public static SaleNumber Parse(string text) =>
        TryParse(text, out var number) ? number : throw new FormatException($"'{text}' is not a sale number such as V01-C2-125.");

    [GeneratedRegex(@"^V(?<branch>\d{2,3})-C(?<register>\d{1,3})-(?<sequence>\d{1,10})$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
/// The identity a terminal numbers its sales with: its branch code and register
/// number, both assigned by the server. Absent (null) until the terminal has learned
/// them, in which case sales commit without a number.
/// </summary>
public sealed record SaleNumbering(BranchCode Branch, RegisterNumber Register);
