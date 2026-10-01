using System.Text.Json;
using Commerce.Domain.Ordering;
using Commerce.Domain.Tenancy;

namespace Commerce.Integration;

/// <summary>
/// document-numbering "Format": web orders read `P01-W-37` (Pedido, branch 01, Web, order 37 of
/// the branch). The single place that formats, parses and explains that number.
/// </summary>
public sealed class OrderNumberTests
{
    [Theory]
    [InlineData(1, 37, "P01-W-37")]
    [InlineData(1, 1, "P01-W-1")]
    [InlineData(12, 410, "P12-W-410")]
    [InlineData(100, 5, "P100-W-5")]
    public void Format_IsTypeBranchOriginSequence_WithoutPaddingTheSequence(int branch, int sequence, string expected) =>
        Assert.Equal(expected, new OrderNumber(new BranchCode(branch), sequence).Format());

    [Fact]
    public void ToString_IsTheFormattedNumber() =>
        Assert.Equal("P03-W-8", new OrderNumber(new BranchCode(3), 8).ToString());

    [Theory]
    [InlineData("P01-W-37", 1, 37)]
    [InlineData("P100-W-5", 100, 5)]
    [InlineData("P999-W-2147483647", 999, int.MaxValue)]
    public void Parse_RoundTripsAFormattedNumber(string text, int branch, int sequence)
    {
        var number = OrderNumber.Parse(text);

        Assert.Equal(branch, number.Branch.Value);
        Assert.Equal(sequence, number.Sequence);
        Assert.Equal(text, number.Format());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("P1-W-37")]        // branch needs two digits
    [InlineData("P01-W-0")]        // sequences start at 1
    [InlineData("P01-W-037x")]
    [InlineData("V01-W-37")]       // not an order
    [InlineData("P01-C2-37")]      // not the web origin
    [InlineData("p01-w-37")]
    [InlineData("P000-W-1")]       // branch out of range
    [InlineData("P1000-W-1")]
    [InlineData("P01-W-2147483648")]
    [InlineData(" P01-W-37")]
    public void TryParse_RejectsAnythingElse(string? text)
    {
        Assert.False(OrderNumber.TryParse(text, out _));
        Assert.Throws<FormatException>(() => OrderNumber.Parse(text!));
    }

    [Fact]
    public void ASequenceBelowOne_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrderNumber(new BranchCode(1), 0));

    [Fact]
    public void Describe_ExplainsEveryPartInSpanish() =>
        Assert.Equal(
            "P = Pedido · 01 = Sucursal · W = Web · 37 = número de pedido de la sucursal",
            new OrderNumber(new BranchCode(1), 37).Describe());

    [Fact]
    public void Json_WritesAndReadsTheFormattedText()
    {
        var number = new OrderNumber(new BranchCode(1), 37);

        Assert.Equal("\"P01-W-37\"", JsonSerializer.Serialize(number));
        Assert.Equal(number, JsonSerializer.Deserialize<OrderNumber>("\"P01-W-37\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OrderNumber>("\"nope\""));
    }

    [Fact]
    public void AnOrder_CarriesItsNumber_AndSerializesItAsText()
    {
        var number = new OrderNumber(new BranchCode(2), 9);
        var order = new Order(
            Guid.NewGuid(), Guid.NewGuid(), OrderOrigin.RegisteredCustomer, Guid.NewGuid(), guestContact: null,
            Guid.NewGuid(), [], DateTimeOffset.UtcNow, number);

        Assert.Equal(number, order.OrderNumber);
        Assert.Contains("\"orderNumber\":\"P02-W-9\"", JsonSerializer.Serialize(order, JsonSerializerOptions.Web));
    }

    [Fact]
    public void AnOrderWithoutANumber_SerializesNull()
    {
        var order = new Order(
            Guid.NewGuid(), Guid.NewGuid(), OrderOrigin.RegisteredCustomer, Guid.NewGuid(), guestContact: null,
            Guid.NewGuid(), [], DateTimeOffset.UtcNow);

        Assert.Null(order.OrderNumber);
        Assert.Contains("\"orderNumber\":null", JsonSerializer.Serialize(order, JsonSerializerOptions.Web));
    }
}
