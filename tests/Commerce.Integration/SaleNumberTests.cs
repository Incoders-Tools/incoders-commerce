using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Commerce.Domain.Tenancy;

namespace Commerce.Integration;

/// <summary>
/// The human number of a POS sale, `V{branch}-C{register}-{sequence}` (document
/// numbering): format, parse, validation, and the additive payload/effect fields
/// that carry it (an old payload without them must still read).
/// </summary>
public sealed class SaleNumberTests
{
    private static SaleNumber Number(int branch, int register, int sequence) =>
        new(new BranchCode(branch), new RegisterNumber(register), sequence);

    [Theory]
    [InlineData(1, 2, 125, "V01-C2-125")]
    [InlineData(12, 15, 1, "V12-C15-1")]
    [InlineData(100, 3, 99999, "V100-C3-99999")]
    public void Format_IsTypeLetterBranchRegisterSequence_NotPaddedBeyondTheBranch(int branch, int register, int sequence, string expected)
    {
        Assert.Equal(expected, Number(branch, register, sequence).Format());
        Assert.Equal(expected, Number(branch, register, sequence).ToString());
    }

    [Theory]
    [InlineData("V01-C2-125", 1, 2, 125)]
    [InlineData("V100-C999-2147483647", 100, 999, int.MaxValue)]
    public void Parse_RoundTripsFormat(string text, int branch, int register, int sequence)
    {
        Assert.True(SaleNumber.TryParse(text, out var parsed));
        Assert.Equal(Number(branch, register, sequence), parsed);
        Assert.Equal(text, SaleNumber.Parse(text).Format());
    }

    [Theory]
    [InlineData("")]
    [InlineData("V1-C2-3")]
    [InlineData("v01-C2-3")]
    [InlineData("P01-W-3")]
    [InlineData("V01-C0-3")]
    [InlineData("V01-C1000-3")]
    [InlineData("V01-C2-0")]
    [InlineData("V01-C2--1")]
    [InlineData("V00-C2-3")]
    [InlineData("V01-C2-3 ")]
    [InlineData("V01-C2")]
    [InlineData("V01-C2-99999999999")]
    public void TryParse_RejectsAnythingThatIsNotASaleNumber(string text)
    {
        Assert.False(SaleNumber.TryParse(text, out _));
        Assert.Throws<FormatException>(() => SaleNumber.Parse(text));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void ASequenceStartsAtOne(int sequence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Number(1, 1, sequence));
    }

    [Fact]
    public void AnOldPayload_WithoutTheNumberFields_StillReads_AsUnnumbered()
    {
        const string old = """{"SaleId":"3f2c1c9e-6a43-4a43-8f43-2d3b9a111111","TotalAmount":855,"SaleKind":"Manual","OccurredAtUtc":"2026-01-01T00:00:00+00:00","Lines":[]}""";

        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(old);

        Assert.Null(payload.BranchCode);
        Assert.Null(payload.RegisterNumber);
        Assert.Null(payload.SaleSequence);
    }

    [Fact]
    public void ThePayload_RoundTripsTheNumberFields()
    {
        var payload = new SalePayloadV1(
            Guid.NewGuid(), 855m, "Scanned", DateTimeOffset.UnixEpoch, [], BranchCode: 1, RegisterNumber: 2, SaleSequence: 125);

        var back = SyncPayloadCodec.Deserialize<SalePayloadV1>(SyncPayloadCodec.Serialize(payload));

        Assert.Equal((1, 2, 125), (back.BranchCode, back.RegisterNumber, back.SaleSequence));
    }

    [Fact]
    public void ANewPayload_IsStillReadableByAReaderThatKnowsNoNumberFields()
    {
        // The additive-evolution rule: unknown members are ignored by an older reader.
        var json = SyncPayloadCodec.Serialize(new SalePayloadV1(
            Guid.NewGuid(), 855m, "Manual", DateTimeOffset.UnixEpoch, [], BranchCode: 1, RegisterNumber: 2, SaleSequence: 125));

        var asOlder = SyncPayloadCodec.Deserialize<OlderSalePayload>(json);

        Assert.Equal(855m, asOlder.TotalAmount);
    }

    private sealed record OlderSalePayload(Guid SaleId, decimal TotalAmount, string SaleKind);

    [Fact]
    public void TheEffect_ExposesItsNumber_OnlyWhenAllThreePartsAreKnown()
    {
        var effect = new SaleEffect(Guid.NewGuid(), Guid.NewGuid(), 10m, DateTimeOffset.UnixEpoch);

        Assert.Null(effect.Number);
        Assert.Null((effect with { RegisterNumber = 2, SaleSequence = 5 }).Number);
        Assert.Equal("V01-C2-5", (effect with { BranchCode = 1, RegisterNumber = 2, SaleSequence = 5 }).Number!.Value.Format());
    }
}
