using Commerce.Domain.Suppliers;

namespace Commerce.Integration;

/// <summary>Pure supplier rules: bank details (CBU/CVU, alias) and payment terms. No I/O.</summary>
public sealed class SupplierDomainTests
{
    [Theory]
    [InlineData("0123456789012345678901", "0123456789012345678901")]
    [InlineData("0123 4567-8901 2345 6789 01", "0123456789012345678901")]
    public void BankCbu_IsNormalizedToTwentyTwoDigits(string raw, string expected)
    {
        Assert.True(SupplierRules.TryNormalizeBankCbu(raw, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BankCbu_BlankMeansNone(string? raw)
    {
        Assert.True(SupplierRules.TryNormalizeBankCbu(raw, out var normalized, out _));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("012345678901234567890A")]
    [InlineData("01234567890123456789012")]
    public void BankCbu_WithTheWrongShape_IsRejected(string raw)
    {
        Assert.False(SupplierRules.TryNormalizeBankCbu(raw, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Contains("22 digits", error);
    }

    [Theory]
    [InlineData("mi.alias-1", "mi.alias-1")]
    [InlineData("  ABC123  ", "ABC123")]
    [InlineData("12345678901234567890", "12345678901234567890")]
    public void BankAlias_IsTrimmedAndAccepted(string raw, string expected)
    {
        Assert.True(SupplierRules.TryNormalizeBankAlias(raw, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void BankAlias_BlankMeansNone(string? raw)
    {
        Assert.True(SupplierRules.TryNormalizeBankAlias(raw, out var normalized, out _));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("con espacio")]
    [InlineData("123456789012345678901")]
    [InlineData("ñandu.alias")]
    public void BankAlias_WithTheWrongShape_IsRejected(string raw)
    {
        Assert.False(SupplierRules.TryNormalizeBankAlias(raw, out _, out var error));
        Assert.Contains("6-20", error);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(365, true)]
    [InlineData(-1, false)]
    [InlineData(366, false)]
    public void PaymentTermsDays_MustBeBetweenZeroAnd365(int? days, bool valid)
    {
        Assert.Equal(valid, SupplierRules.TryValidatePaymentTermsDays(days, out var error));
        Assert.Equal(valid, error is null);
    }
}
