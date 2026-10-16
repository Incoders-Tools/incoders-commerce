using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pos-user-login task 1.1 (design.md "PIN shape and
/// verifier"): PBKDF2-HMAC-SHA256 derivation/verification is pure, no I/O,
/// and `IsValidPin` rejects the trivial PIN shapes the design calls out
/// (wrong length, non-digits, all-same-digit, strictly ascending/descending
/// runs) before any I/O happens.
/// </summary>
public sealed class OperatorPinCredentialTests
{
    [Fact]
    public void Derive_ThenVerify_CorrectPin_Succeeds()
    {
        var (salt, subkey) = OperatorPinCredential.Derive("482913");

        Assert.True(OperatorPinCredential.Verify("482913", salt, subkey));
    }

    [Fact]
    public void Derive_ThenVerify_WrongPin_Fails()
    {
        var (salt, subkey) = OperatorPinCredential.Derive("482913");

        Assert.False(OperatorPinCredential.Verify("111222", salt, subkey));
    }

    [Fact]
    public void Derive_TwoCallsSamePin_ProduceDifferentSalts_ButBothVerify()
    {
        var (saltA, subkeyA) = OperatorPinCredential.Derive("482913");
        var (saltB, subkeyB) = OperatorPinCredential.Derive("482913");

        Assert.NotEqual(Convert.ToBase64String(saltA), Convert.ToBase64String(saltB));
        Assert.True(OperatorPinCredential.Verify("482913", saltA, subkeyA));
        Assert.True(OperatorPinCredential.Verify("482913", saltB, subkeyB));
    }

    [Theory]
    [InlineData("482913")]
    [InlineData("000019")]
    public void IsValidPin_AcceptsSixDigitNonTrivialPin(string pin)
    {
        Assert.True(OperatorPinCredential.IsValidPin(pin));
    }

    [Theory]
    [InlineData("48291")]     // 5 digits
    [InlineData("4829134")]  // 7 digits
    [InlineData("48a913")]   // non-digit
    [InlineData("111111")]   // all-same-digit
    [InlineData("123456")]   // strictly ascending run
    [InlineData("654321")]   // strictly descending run
    public void IsValidPin_RejectsInvalidShapes(string pin)
    {
        Assert.False(OperatorPinCredential.IsValidPin(pin));
    }
}
