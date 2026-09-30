using Commerce.Domain.Identity;

namespace Commerce.Pos.Windows;

/// <summary>The content the main window shows: the sale, or a management section.</summary>
public enum ShellSection
{
    Sale,
    Customers,
    Staff,
}

/// <summary>
/// Which section the main window shows and which ones the current operator may
/// open. UI-free: the window mirrors <see cref="Current"/>. Permission gating is
/// a UX affordance only; the server re-checks every admin call.
/// </summary>
public sealed class ShellNavigation
{
    public ShellSection Current { get; private set; } = ShellSection.Sale;

    /// <summary>The sale is always reachable; Clientes and Personal need ManageUsers.</summary>
    public static IReadOnlyList<ShellSection> Allowed(int? permissions) =>
        permissions is { } granted && ((Permission)granted).HasFlag(Permission.ManageUsers)
            ? [ShellSection.Sale, ShellSection.Customers, ShellSection.Staff]
            : [ShellSection.Sale];

    /// <summary>Switches to <paramref name="target"/>; false when it is not allowed, already current, or the old section is still being torn down.</summary>
    public bool Navigate(ShellSection target, int? permissions)
    {
        if (TeardownPending || target == Current || !Allowed(permissions).Contains(target))
        {
            return false;
        }

        Current = target;
        return true;
    }

    /// <summary>After the operator changed: a section the new operator may not open falls back to the sale.</summary>
    public bool Reconcile(int? permissions) => Reconcile(permissions, sectionBusy: false) == ReconcileOutcome.Switched;

    /// <summary>
    /// The section the operator lost was busy, so the window still holds it (hidden) until its
    /// request ends; <see cref="Current"/> is already the sale. <see cref="CompleteTeardown"/> releases it.
    /// </summary>
    public bool TeardownPending { get; private set; }

    /// <summary>
    /// Like <see cref="Reconcile(int?)"/>, but a section with a request in flight is never torn
    /// down under it. The model still moves to the sale at once (<see cref="ReconcileOutcome.Deferred"/>),
    /// so the model and the screen never disagree; only the disposal of the old section waits for
    /// the host to call <see cref="CompleteTeardown"/> once it is idle.
    /// </summary>
    public ReconcileOutcome Reconcile(int? permissions, bool sectionBusy)
    {
        if (Allowed(permissions).Contains(Current))
        {
            return ReconcileOutcome.Unchanged;
        }

        Current = ShellSection.Sale;
        if (sectionBusy)
        {
            TeardownPending = true;
            return ReconcileOutcome.Deferred;
        }

        return ReconcileOutcome.Switched;
    }

    /// <summary>The deferred section went idle: true when there was one to release.</summary>
    public bool CompleteTeardown()
    {
        var pending = TeardownPending;
        TeardownPending = false;
        return pending;
    }
}

public enum ReconcileOutcome
{
    /// <summary>The current section is still allowed.</summary>
    Unchanged,

    /// <summary>The section was dropped: the shell is back on the sale and the old section can go now.</summary>
    Switched,

    /// <summary>The section is no longer allowed but is busy: the shell is on the sale, the old section is torn down once idle.</summary>
    Deferred,
}

/// <summary>A management section hosted in the main window's content area.</summary>
public interface ISectionView : IDisposable
{
    /// <summary>A network action is in flight: the shell keeps the section until it ends.</summary>
    bool IsBusy { get; }

    /// <summary>Raised when a network action ends and the section is no longer busy.</summary>
    event Action? Idle;
}
