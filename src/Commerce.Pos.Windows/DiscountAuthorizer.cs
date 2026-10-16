using Commerce.BranchNode;
using Commerce.Domain.Discounts;

namespace Commerce.Pos.Windows;

public enum DiscountAvailability
{
    /// <summary>The branch has no discount PIN (or this terminal has not synced it yet): discounts are unavailable.</summary>
    NotConfigured,

    /// <summary>Too many wrong attempts: the prompt is locked.</summary>
    Locked,

    /// <summary>A PIN is cached and the prompt is open.</summary>
    Ready,
}

/// <summary>Whether a discount can be authorized right now, and for how long the prompt stays locked.</summary>
public sealed record DiscountAvailabilityStatus(DiscountAvailability State, TimeSpan? LockedFor = null);

public enum DiscountAuthorizationKind
{
    Granted,
    Denied,
    Locked,
    NotConfigured,
}

/// <summary>
/// Result of one authorization attempt. <see cref="Message"/> is operator-facing
/// (Spanish). <see cref="Authorization"/> is set only when <see cref="Kind"/> is
/// <see cref="DiscountAuthorizationKind.Granted"/>.
/// </summary>
public sealed record DiscountAuthorizationOutcome(
    DiscountAuthorizationKind Kind,
    DiscountAuthorization? Authorization = null,
    string? Message = null,
    int AttemptsRemaining = 0,
    TimeSpan? LockedFor = null);

/// <summary>
/// The one seam through which a discount is authorized. Today the only source is
/// the branch PIN (<see cref="BranchPinDiscountAuthorizer"/>); a card reader can
/// be added later as another implementation presenting the same
/// <see cref="DiscountAuthorization"/>, without touching the cart or the sale.
/// </summary>
public interface IDiscountAuthorizer
{
    DiscountAvailabilityStatus GetAvailability();

    /// <summary>Checks the credential typed or presented by the operator; <paramref name="operatorId"/> is who is signed in.</summary>
    DiscountAuthorizationOutcome Authorize(string credential, Guid operatorId);
}

/// <summary>
/// Authorizes discounts with the shared branch PIN, verified offline against the
/// verifier replicated to <c>branch.db</c>. Five consecutive wrong attempts lock
/// the prompt for five minutes; the counter and the lock are persisted per
/// terminal so a restart does not reset them. Reading the cache and the lockout
/// touches only local state: it never reads the device credential and never
/// needs the network.
/// </summary>
public sealed class BranchPinDiscountAuthorizer : IDiscountAuthorizer
{
    private readonly BranchSyncStore _store;
    private readonly Func<Guid> _branchId;
    private readonly Func<DateTimeOffset> _clock;

    public BranchPinDiscountAuthorizer(BranchSyncStore store, Func<Guid> branchId, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _branchId = branchId;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public DiscountAvailabilityStatus GetAvailability()
    {
        if (_store.GetDiscountPin(_branchId()) is null)
        {
            return new DiscountAvailabilityStatus(DiscountAvailability.NotConfigured);
        }

        var now = _clock();
        var lockout = _store.GetDiscountPinLockout();
        return PinLockout.IsLocked(lockout, now)
            ? new DiscountAvailabilityStatus(DiscountAvailability.Locked, PinLockout.Remaining(lockout, now))
            : new DiscountAvailabilityStatus(DiscountAvailability.Ready);
    }

    public DiscountAuthorizationOutcome Authorize(string credential, Guid operatorId)
    {
        var replica = _store.GetDiscountPin(_branchId());
        if (replica is null)
        {
            return new DiscountAuthorizationOutcome(
                DiscountAuthorizationKind.NotConfigured,
                Message: "Esta sucursal no tiene un PIN de descuentos. Pídale a un administrador que lo defina y sincronice la terminal.");
        }

        var now = _clock();
        var lockout = _store.GetDiscountPinLockout();
        if (PinLockout.IsLocked(lockout, now))
        {
            return Locked(PinLockout.Remaining(lockout, now));
        }

        var pin = credential.Trim();
        if (pin.Length == 0)
        {
            return new DiscountAuthorizationOutcome(
                DiscountAuthorizationKind.Denied, Message: "Ingrese el PIN de descuentos.",
                AttemptsRemaining: PinLockout.MaxFailures - (lockout.LockedUntilUtc is null ? lockout.FailedAttempts : 0));
        }

        if (BranchDiscountPin.Verify(pin, replica.Verifier))
        {
            _store.SetDiscountPinLockout(PinLockout.RegisterSuccess());
            return new DiscountAuthorizationOutcome(
                DiscountAuthorizationKind.Granted,
                new DiscountAuthorization(DiscountAuthorization.BranchPin, operatorId, replica.Version));
        }

        var failed = PinLockout.RegisterFailure(lockout, now);
        _store.SetDiscountPinLockout(failed);
        if (PinLockout.IsLocked(failed, now))
        {
            return Locked(PinLockout.Remaining(failed, now));
        }

        var remaining = PinLockout.MaxFailures - failed.FailedAttempts;
        return new DiscountAuthorizationOutcome(
            DiscountAuthorizationKind.Denied,
            Message: $"PIN incorrecto. Intentos restantes: {remaining}.",
            AttemptsRemaining: remaining);
    }

    private static DiscountAuthorizationOutcome Locked(TimeSpan remaining) => new(
        DiscountAuthorizationKind.Locked,
        Message: $"Demasiados intentos fallidos. Vuelva a intentar en {Math.Ceiling(remaining.TotalMinutes):0} min.",
        LockedFor: remaining);
}
