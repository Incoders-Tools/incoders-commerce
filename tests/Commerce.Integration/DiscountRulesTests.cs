using Commerce.Domain.Discounts;

namespace Commerce.Integration;

/// <summary>
/// pos-scan-sale spec "Percentage Discounts on Lines and on the Whole Sale":
/// percentage bounds and half-away-from-zero rounding, plus the PIN lockout
/// policy of branch-discount-pin (5 failures lock for 5 minutes).
/// </summary>
public sealed class DiscountRulesTests
{
    [Theory]
    [InlineData("0.01")]
    [InlineData("10")]
    [InlineData("12.5")]
    [InlineData("99.99")]
    [InlineData("100")]
    public void IsValidPercent_AcceptsMoreThanZeroUpToOneHundredWithTwoDecimals(string percent) =>
        Assert.True(DiscountMath.IsValidPercent(decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100.01")]
    [InlineData("250")]
    [InlineData("10.005")]
    [InlineData("0.001")]
    public void IsValidPercent_RefusesEverythingElse(string percent) =>
        Assert.False(DiscountMath.IsValidPercent(decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("1000", "10", "100.00")]
    [InlineData("10.05", "10", "1.01")]   // 1.005 rounds away from zero
    [InlineData("10.04", "10", "1.00")]   // 1.004 rounds down
    [InlineData("333.33", "33.33", "111.10")]
    [InlineData("50", "100", "50.00")]
    public void Amount_IsRoundedHalfAwayFromZero(string baseAmount, string percent, string expected) =>
        Assert.Equal(
            decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            DiscountMath.Amount(
                decimal.Parse(baseAmount, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture)));
}

public sealed class PinLockoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FourFailuresDoNotLock_TheFifthLocksForFiveMinutes()
    {
        var state = PinLockoutState.None;
        for (var i = 0; i < 4; i++)
        {
            state = PinLockout.RegisterFailure(state, Now);
            Assert.False(PinLockout.IsLocked(state, Now));
        }

        state = PinLockout.RegisterFailure(state, Now);

        Assert.True(PinLockout.IsLocked(state, Now));
        Assert.Equal(Now.AddMinutes(5), state.LockedUntilUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), PinLockout.Remaining(state, Now));
    }

    [Fact]
    public void TheLockExpiresAfterFiveMinutes_AndCountingStartsOver()
    {
        var state = PinLockoutState.None;
        for (var i = 0; i < 5; i++) state = PinLockout.RegisterFailure(state, Now);

        Assert.True(PinLockout.IsLocked(state, Now.AddMinutes(4).AddSeconds(59)));
        Assert.False(PinLockout.IsLocked(state, Now.AddMinutes(5)));

        var afterExpiry = PinLockout.RegisterFailure(state, Now.AddMinutes(6));
        Assert.Equal(1, afterExpiry.FailedAttempts);
        Assert.False(PinLockout.IsLocked(afterExpiry, Now.AddMinutes(6)));
    }

    [Fact]
    public void AFailureWhileLockedChangesNothing_NotEvenTheDeadline()
    {
        var state = PinLockoutState.None;
        for (var i = 0; i < 5; i++) state = PinLockout.RegisterFailure(state, Now);

        var again = PinLockout.RegisterFailure(state, Now.AddMinutes(2));

        Assert.Equal(state, again);
    }

    [Fact]
    public void ASuccessResetsTheFailureCount()
    {
        var state = PinLockoutState.None;
        for (var i = 0; i < 3; i++) state = PinLockout.RegisterFailure(state, Now);

        Assert.Equal(PinLockoutState.None, PinLockout.RegisterSuccess());
    }
}
