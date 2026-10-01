using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Commerce.Domain.Tenancy;

namespace Commerce.Domain.Ordering;

/// <summary>
/// The human number of a web order: `P{branch}-W-{sequence}`, for example `P01-W-37`. `P` is the
/// document type (pedido), the branch is its two-digit-minimum <see cref="BranchCode"/>, `W` is the
/// web origin and the sequence counts the orders of that branch from 1 (not zero-padded). The server
/// assigns the sequence when the order is stored; this type is the single place that formats,
/// parses and explains it. On the wire it is its plain text.
/// </summary>
[JsonConverter(typeof(OrderNumberJsonConverter))]
public readonly record struct OrderNumber
{
    public const char TypeLetter = 'P';
    public const char OriginLetter = 'W';

    public BranchCode Branch { get; }
    public int Sequence { get; }

    public OrderNumber(BranchCode branch, int sequence)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "An order sequence starts at 1.");
        }
        Branch = branch;
        Sequence = sequence;
    }

    /// <summary>`P01-W-37`.</summary>
    public string Format() =>
        $"{TypeLetter}{Branch.Format()}-{OriginLetter}-{Sequence.ToString(CultureInfo.InvariantCulture)}";

    public override string ToString() => Format();

    /// <summary>The tooltip that explains each part to the customer.</summary>
    public string Describe() =>
        $"{TypeLetter} = Pedido · {Branch.Format()} = Sucursal · {OriginLetter} = Web · {Sequence.ToString(CultureInfo.InvariantCulture)} = número de pedido de la sucursal";

    public static bool TryParse(string? text, out OrderNumber number)
    {
        number = default;
        if (text is null || text.Length < 7 || text[0] != TypeLetter) return false;

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

        var parsed = new OrderNumber(new BranchCode(branch), sequence);
        // Only the canonical spelling is a number: no padding on the sequence, no 001 for 01.
        if (parsed.Format() != text) return false;

        number = parsed;
        return true;
    }

    public static OrderNumber Parse(string text) =>
        TryParse(text, out var number) ? number : throw new FormatException($"'{text}' is not an order number such as P01-W-37.");
}

internal sealed class OrderNumberJsonConverter : JsonConverter<OrderNumber>
{
    public override OrderNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        OrderNumber.TryParse(reader.GetString(), out var number)
            ? number
            : throw new JsonException("Expected an order number such as P01-W-37.");

    public override void Write(Utf8JsonWriter writer, OrderNumber value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Format());
}
