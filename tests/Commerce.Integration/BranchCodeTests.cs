using Commerce.Domain.Tenancy;

namespace Commerce.Integration;

/// <summary>
/// organization-persistence "Branch Short Code": the one place that formats a
/// branch code for humans (`01`, `02`, ... `100`). Reused by document numbers.
/// </summary>
public sealed class BranchCodeTests
{
    [Theory]
    [InlineData(1, "01")]
    [InlineData(9, "09")]
    [InlineData(10, "10")]
    [InlineData(99, "99")]
    [InlineData(100, "100")]
    [InlineData(999, "999")]
    public void Format_PadsToAtLeastTwoDigits(int value, string expected) =>
        Assert.Equal(expected, new BranchCode(value).Format());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000)]
    public void Constructor_RejectsOutOfRange(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BranchCode(value));

    [Fact]
    public void ToString_IsTheFormattedCode() => Assert.Equal("07", new BranchCode(7).ToString());
}
