using Commerce.Domain.Geography;

namespace Commerce.Integration;

/// <summary>
/// admin-console-field-fixes T2: the optional postal code of a city is an Argentine CP (4 digits, "2000") or a CPA
/// (province letter, 4 digits, 3 letters, "S2000ABC"). Trimmed and upper-cased; blank means none.
/// </summary>
public sealed class PostalCodeRulesTests
{
    [Theory]
    [InlineData("2000", "2000")]
    [InlineData(" 1425 ", "1425")]
    [InlineData("S2000ABC", "S2000ABC")]
    [InlineData("s2000abc", "S2000ABC")]
    [InlineData("C1425DKF", "C1425DKF")]
    public void Valid_postal_codes_are_normalized(string raw, string expected)
    {
        Assert.True(PostalCodeRules.TryNormalize(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("200")]
    [InlineData("20000")]
    [InlineData("S2000AB")]
    [InlineData("S2000ABCD")]
    [InlineData("I2000ABC")]   // no province uses I or O
    [InlineData("O2000ABC")]
    [InlineData("2000ABC")]
    [InlineData("S 2000 ABC")]
    [InlineData("ABCD")]
    public void Invalid_postal_codes_are_refused(string raw)
    {
        Assert.False(PostalCodeRules.TryNormalize(raw, out var normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_means_no_postal_code(string? raw)
    {
        Assert.True(PostalCodeRules.TryNormalize(raw, out var normalized));
        Assert.Null(normalized);
    }
}
