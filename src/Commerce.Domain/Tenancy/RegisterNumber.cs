namespace Commerce.Domain.Tenancy;

/// <summary>
/// The register (till) number of a paired POS terminal inside its branch (1..999).
/// The server assigns it at pairing and never reuses a number for a different
/// installation, because sale numbers are generated offline from
/// (branch, register, sequence). It is the `C{register}` part of a document
/// number such as `V01-C2-125`; it is not zero-padded.
/// </summary>
public readonly record struct RegisterNumber
{
    public const int MinValue = 1;
    public const int MaxValue = 999;

    public int Value { get; }

    public RegisterNumber(int value)
    {
        if (value is < MinValue or > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"A register number is between {MinValue} and {MaxValue}.");
        }
        Value = value;
    }

    /// <summary>`C2`: the origin segment of a POS document number.</summary>
    public string Format() => "C" + Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override string ToString() => Format();
}
