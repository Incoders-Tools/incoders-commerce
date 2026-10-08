namespace Commerce.Pos.Windows;

/// <summary>
/// A <c>StaysOpen=False</c> popup closes on the mouse-down of its own trigger, and
/// the trigger's Click (mouse-up) would then reopen it. This decides whether a Click
/// should open the popup: not when the popup was closed by the press that produced it.
/// Times come from the caller (a monotonic clock) so the rule is testable.
/// </summary>
public sealed class PopupReopenGuard
{
    /// <summary>The outside-click close can land a moment before the trigger sees its own press.</summary>
    private static readonly TimeSpan CloseLeadTolerance = TimeSpan.FromMilliseconds(150);

    private TimeSpan? _closedAt;
    private TimeSpan? _pressedAt;

    public void NotifyPressed(TimeSpan now) => _pressedAt = now;

    public void NotifyClosed(TimeSpan now) => _closedAt = now;

    /// <summary>Consumes the press: a later keyboard-driven Click has no press and is never suppressed.</summary>
    public bool ShouldOpenOnClick(TimeSpan now)
    {
        var closedByThisPress = _pressedAt is { } pressed && _closedAt is { } closed && closed >= pressed - CloseLeadTolerance;
        _pressedAt = null;
        _closedAt = null;
        return !closedByThisPress;
    }
}
