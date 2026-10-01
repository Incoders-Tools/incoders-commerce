namespace Commerce.Domain.Tenancy;

/// <summary>
/// The short, numeric, per-organization code of a branch (1..999). The server
/// assigns it at creation (database trigger `branches_allocate_code`) and it
/// never changes. This type is the SINGLE place that formats it for humans:
/// at least two digits (`01`, `02`, ... `99`, `100`), the `{branch}` part of
/// document numbers such as `V01-C2-125`.
/// </summary>
public readonly record struct BranchCode
{
    public const int MinValue = 1;
    public const int MaxValue = 999;

    public int Value { get; }

    public BranchCode(int value)
    {
        if (value is < MinValue or > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"A branch code is between {MinValue} and {MaxValue}.");
        }
        Value = value;
    }

    public string Format() => Value.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    public override string ToString() => Format();
}
