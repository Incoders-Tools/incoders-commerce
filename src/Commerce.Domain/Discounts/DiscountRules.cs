using Commerce.Domain.Pricing;

namespace Commerce.Domain.Discounts;

/// <summary>
/// The percentage-discount arithmetic of pos-scan-sale ("Percentage Discounts
/// on Lines and on the Whole Sale"): a percentage is more than 0 and at most
/// 100 with at most two decimals, and every discount amount is rounded once, to
/// two decimals half away from zero (<see cref="Money.Round2"/>).
/// </summary>
public static class DiscountMath
{
    public static bool IsValidPercent(decimal percent) =>
        percent > 0m && percent <= 100m && decimal.Round(percent, 2) == percent;

    /// <summary>The discount amount of <paramref name="percent"/> over <paramref name="baseAmount"/>.</summary>
    public static decimal Amount(decimal baseAmount, decimal percent) => Money.Round2(baseAmount * percent / 100m);
}

/// <summary>
/// Proof that a discount was authorized: the method used, the operator signed
/// in at the time, and the version of the branch PIN that was accepted.
/// Recorded on the sale and synchronized to the cloud audit.
/// </summary>
public sealed record DiscountAuthorization(string Method, Guid OperatorId, long PinVersion)
{
    /// <summary>The shared discount PIN of the branch.</summary>
    public const string BranchPin = "branch-pin";
}

/// <summary>A whole-sale discount as committed: percentage and its rounded amount.</summary>
public sealed record SaleDiscount(decimal Percent, decimal Amount);

/// <summary>Consecutive failed PIN attempts on a terminal and, once locked, until when.</summary>
public sealed record PinLockoutState(int FailedAttempts, DateTimeOffset? LockedUntilUtc)
{
    public static PinLockoutState None { get; } = new(0, null);
}

/// <summary>
/// Lockout policy of branch-discount-pin: 5 consecutive failures lock the
/// prompt for 5 minutes; a success resets; a failure while locked changes
/// nothing; once the lock expires counting starts over. Pure, so the terminal
/// can persist the state and survive a restart.
/// </summary>
public static class PinLockout
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    public static bool IsLocked(PinLockoutState state, DateTimeOffset now) =>
        state.LockedUntilUtc is { } until && now < until;

    public static TimeSpan Remaining(PinLockoutState state, DateTimeOffset now) =>
        state.LockedUntilUtc is { } until && now < until ? until - now : TimeSpan.Zero;

    public static PinLockoutState RegisterFailure(PinLockoutState state, DateTimeOffset now)
    {
        if (IsLocked(state, now))
        {
            return state;
        }

        // An expired lock starts a fresh count.
        var failures = (state.LockedUntilUtc is null ? state.FailedAttempts : 0) + 1;
        return failures >= MaxFailures
            ? new PinLockoutState(0, now + LockDuration)
            : new PinLockoutState(failures, null);
    }

    public static PinLockoutState RegisterSuccess() => PinLockoutState.None;
}
