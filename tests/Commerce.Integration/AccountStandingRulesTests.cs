using Commerce.Domain.Tenancy;

namespace Commerce.Integration;

/// <summary>
/// Pure organization account-standing rules (odd/tasks/organization-account-standing.md T1): the standing is derived
/// from the billing due date, the grace days and a manual suspension on the business day passed in, never stored.
/// </summary>
public sealed class AccountStandingRulesTests
{
    private static readonly DateOnly DueOn = new(2026, 10, 8);

    [Fact]
    public void NoDueDate_IsActive_WithNoCountdown()
    {
        var standing = AccountStandingRules.Evaluate(null, 30, false, new DateOnly(2027, 1, 1));

        Assert.Equal(AccountStandingStatus.Active, standing.Status);
        Assert.Null(standing.SuspendsOn);
        Assert.Null(standing.DaysLeft);
    }

    [Theory]
    [InlineData(2026, 9, 1)]
    [InlineData(2026, 10, 8)]
    public void OnOrBeforeTheDueDate_IsActive(int year, int month, int day)
    {
        var standing = AccountStandingRules.Evaluate(DueOn, 30, false, new DateOnly(year, month, day));

        Assert.Equal(AccountStandingStatus.Active, standing.Status);
        Assert.Null(standing.SuspendsOn);
        Assert.Null(standing.DaysLeft);
    }

    [Fact]
    public void FirstDayAfterTheDueDate_IsOverdue_WithTheWholeGraceLeft()
    {
        var standing = AccountStandingRules.Evaluate(DueOn, 30, false, new DateOnly(2026, 10, 9));

        Assert.Equal(AccountStandingStatus.Overdue, standing.Status);
        Assert.Equal(new DateOnly(2026, 11, 8), standing.SuspendsOn);
        Assert.Equal(30, standing.DaysLeft);
    }

    [Fact]
    public void LastGraceDay_IsOverdue_WithOneDayLeft()
    {
        var standing = AccountStandingRules.Evaluate(DueOn, 30, false, new DateOnly(2026, 11, 7));

        Assert.Equal(AccountStandingStatus.Overdue, standing.Status);
        Assert.Equal(new DateOnly(2026, 11, 8), standing.SuspendsOn);
        Assert.Equal(1, standing.DaysLeft);
    }

    [Theory]
    [InlineData(2026, 11, 8)]
    [InlineData(2027, 3, 1)]
    public void FromTheSuspensionDateOn_IsSuspended_AndKeepsTheDateItStarted(int year, int month, int day)
    {
        var standing = AccountStandingRules.Evaluate(DueOn, 30, false, new DateOnly(year, month, day));

        Assert.Equal(AccountStandingStatus.Suspended, standing.Status);
        Assert.Equal(new DateOnly(2026, 11, 8), standing.SuspendsOn);
        Assert.Null(standing.DaysLeft);
    }

    [Fact]
    public void ZeroGraceDays_SuspendsOnTheFirstDayAfterTheDueDate_WithNoOverdueStep()
    {
        Assert.Equal(AccountStandingStatus.Active, AccountStandingRules.Evaluate(DueOn, 0, false, DueOn).Status);

        var standing = AccountStandingRules.Evaluate(DueOn, 0, false, new DateOnly(2026, 10, 9));

        Assert.Equal(AccountStandingStatus.Suspended, standing.Status);
        Assert.Equal(new DateOnly(2026, 10, 9), standing.SuspendsOn);
    }

    [Fact]
    public void ManualSuspension_WinsOverAFutureDueDate_AndOverNoDueDate()
    {
        var withFutureDue = AccountStandingRules.Evaluate(new DateOnly(2027, 1, 1), 30, true, new DateOnly(2026, 10, 9));
        var withoutDue = AccountStandingRules.Evaluate(null, 30, true, new DateOnly(2026, 10, 9));

        Assert.Equal(AccountStandingStatus.Suspended, withFutureDue.Status);
        Assert.Null(withFutureDue.SuspendsOn);
        Assert.Null(withFutureDue.DaysLeft);
        Assert.Equal(AccountStandingStatus.Suspended, withoutDue.Status);
    }

    [Fact]
    public void Reactivation_ClearingTheManualSuspensionWithANewDueDate_IsActive()
    {
        var standing = AccountStandingRules.Evaluate(new DateOnly(2026, 11, 30), 30, false, new DateOnly(2026, 11, 10));

        Assert.Equal(AccountStandingStatus.Active, standing.Status);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(90, true)]
    [InlineData(-1, false)]
    [InlineData(91, false)]
    public void GraceDays_AreValidFromZeroToNinety(int graceDays, bool valid)
    {
        Assert.Equal(valid, AccountStandingRules.IsValidGraceDays(graceDays));
    }

    [Fact]
    public void AManualSuspension_IsSuspended_EvenWithGraceDaysOutOfRange()
    {
        // The POS evaluates inputs that arrive over the wire; a manual suspension must never turn into an exception.
        Assert.Equal(AccountStandingStatus.Suspended, AccountStandingRules.Evaluate(DueOn, 91, true, DueOn).Status);
        Assert.Equal(AccountStandingStatus.Suspended, AccountStandingRules.Evaluate(null, -1, true, DueOn).Status);
    }

    [Fact]
    public void InvalidGraceDays_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountStandingRules.Evaluate(DueOn, -1, false, DueOn));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountStandingRules.Evaluate(DueOn, 91, false, DueOn));
    }

    [Fact]
    public void DefaultGraceDays_IsThirty()
    {
        Assert.Equal(30, AccountStandingRules.DefaultGraceDays);
    }
}
