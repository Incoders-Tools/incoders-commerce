using Commerce.Domain.Discounts;

namespace Commerce.Integration;

/// <summary>
/// branch-discount-pin spec: PIN shape (4 to 12 digits) and the slow salted
/// verifier (PBKDF2-SHA256, 128-bit salt, 210 000 iterations, 256-bit subkey).
/// </summary>
public sealed class BranchDiscountPinCredentialTests
{
    [Theory]
    [InlineData("1234")]
    [InlineData("0000")]
    [InlineData("123456789012")]
    public void IsValidPin_AcceptsFourToTwelveDigits(string pin) =>
        Assert.True(BranchDiscountPin.IsValidPin(pin));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("1234567890123")]
    [InlineData("12a4")]
    [InlineData("12 4")]
    [InlineData("１２３４")]
    public void IsValidPin_RejectsEverythingElse(string? pin) =>
        Assert.False(BranchDiscountPin.IsValidPin(pin));

    [Fact]
    public void Derive_UsesTheHouseParametersAndAFreshSaltEveryTime()
    {
        var first = BranchDiscountPin.Derive("2468");
        var second = BranchDiscountPin.Derive("2468");

        Assert.Equal("pbkdf2-sha256", first.Algorithm);
        Assert.Equal(210_000, first.Iterations);
        Assert.Equal(16, first.Salt.Length);
        Assert.Equal(32, first.Hash.Length);
        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Verify_AcceptsTheRightPinAndRejectsAWrongOne()
    {
        var verifier = BranchDiscountPin.Derive("2468");

        Assert.True(BranchDiscountPin.Verify("2468", verifier));
        Assert.False(BranchDiscountPin.Verify("2469", verifier));
        Assert.False(BranchDiscountPin.Verify("", verifier));
    }

    [Fact]
    public void Verify_RefusesAnUnknownAlgorithmInsteadOfGuessing()
    {
        var verifier = BranchDiscountPin.Derive("2468") with { Algorithm = "md5" };

        Assert.False(BranchDiscountPin.Verify("2468", verifier));
    }

    [Fact]
    public void Verify_HonoursTheStoredIterationCount()
    {
        var salt = new byte[16];
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            "2468", salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);

        Assert.True(BranchDiscountPin.Verify("2468", new DiscountPinVerifier("pbkdf2-sha256", 100_000, salt, hash)));
        Assert.False(BranchDiscountPin.Verify("2468", new DiscountPinVerifier("pbkdf2-sha256", 100_001, salt, hash)));
    }
}
