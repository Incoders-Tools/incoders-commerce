using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// A StaysOpen=False popup closes on the mouse-down of its own trigger; the Click on
/// mouse-up must not reopen it, while a fresh press later opens it again.
/// </summary>
public sealed class PopupReopenGuardTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);

    [Fact]
    public void Click_WithNothingClosedBefore_Opens()
    {
        var guard = new PopupReopenGuard();
        guard.NotifyPressed(T0);

        Assert.True(guard.ShouldOpenOnClick(T0 + TimeSpan.FromMilliseconds(80)));
    }

    [Fact]
    public void Click_ThatFollowsACloseCausedByTheSamePress_DoesNotReopen()
    {
        var guard = new PopupReopenGuard();
        guard.NotifyClosed(T0 - TimeSpan.FromMilliseconds(2)); // the popup closes as the press starts
        guard.NotifyPressed(T0);

        Assert.False(guard.ShouldOpenOnClick(T0 + TimeSpan.FromMilliseconds(400))); // slow click
    }

    [Fact]
    public void Click_WhenThePopupClosesAfterThePressStarted_DoesNotReopen()
    {
        var guard = new PopupReopenGuard();
        guard.NotifyPressed(T0);
        guard.NotifyClosed(T0 + TimeSpan.FromMilliseconds(3));

        Assert.False(guard.ShouldOpenOnClick(T0 + TimeSpan.FromMilliseconds(90)));
    }

    [Fact]
    public void Click_OnALaterPress_OpensAgainAfterAnEarlierClose()
    {
        var guard = new PopupReopenGuard();
        guard.NotifyClosed(T0);
        guard.NotifyPressed(T0 + TimeSpan.FromSeconds(3));

        Assert.True(guard.ShouldOpenOnClick(T0 + TimeSpan.FromSeconds(3.1)));
    }

    [Fact]
    public void Click_WithoutARecordedPress_Opens()
    {
        Assert.True(new PopupReopenGuard().ShouldOpenOnClick(T0));
    }

    [Fact]
    public void KeyboardClick_AfterAMouseCloseWasConsumed_Opens()
    {
        var guard = new PopupReopenGuard();
        guard.NotifyPressed(T0);
        guard.NotifyClosed(T0);
        Assert.False(guard.ShouldOpenOnClick(T0 + TimeSpan.FromMilliseconds(50)));

        Assert.True(guard.ShouldOpenOnClick(T0 + TimeSpan.FromSeconds(5))); // Space/Enter: no press recorded
    }
}
