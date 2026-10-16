using System.Globalization;
using Commerce.Domain.Tenancy;

namespace Commerce.Domain.Purchasing;

/// <summary>
/// The human number of a goods reception: `R{branch}-W-{sequence}`, for example `R01-W-37` (see
/// docs/document-numbering.md). `R` is the document type (recepcion), the branch is its <see cref="BranchCode"/>, `W`
/// marks the web console as the origin and the sequence counts the confirmed receptions of the branch from 1 (not
/// zero-padded). Assigned by the server when the reception is CONFIRMED; a draft has no number.
/// </summary>
public readonly record struct ReceptionNumber
{
    public const char TypeLetter = 'R';
    public const char OriginLetter = 'W';

    public BranchCode Branch { get; }
    public int Sequence { get; }

    public ReceptionNumber(BranchCode branch, int sequence)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "A reception sequence starts at 1.");
        }

        Branch = branch;
        Sequence = sequence;
    }

    public string Format() =>
        $"{TypeLetter}{Branch.Format()}-{OriginLetter}-{Sequence.ToString(CultureInfo.InvariantCulture)}";

    public override string ToString() => Format();

    public static bool TryParse(string? text, out ReceptionNumber number)
    {
        number = default;
        if (text is null || text.Length < 7 || text[0] != TypeLetter)
        {
            return false;
        }

        var parts = text.Split('-');
        if (parts.Length != 3
            || parts[1] != OriginLetter.ToString()
            || !int.TryParse(parts[0].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var branch)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || branch is < BranchCode.MinValue or > BranchCode.MaxValue
            || sequence < 1)
        {
            return false;
        }

        var parsed = new ReceptionNumber(new BranchCode(branch), sequence);
        if (parsed.Format() != text)
        {
            return false;
        }

        number = parsed;
        return true;
    }
}
