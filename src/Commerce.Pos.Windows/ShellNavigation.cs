using Commerce.Domain.Identity;

namespace Commerce.Pos.Windows;

/// <summary>The content the main window shows: the sale, or a management section.</summary>
public enum ShellSection
{
    Sale,

    /// <summary>"Ventas": the sales committed at this terminal, with their detail and void (PIN-authorized).</summary>
    Sales,
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
    /// <summary>How long a detached section may keep the shell waiting before navigation is allowed again.</summary>
    public static readonly TimeSpan DefaultTeardownTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _teardownTimeout;
    private DateTimeOffset? _teardownStartedAt;

    public ShellNavigation(TimeProvider? clock = null, TimeSpan? teardownTimeout = null)
    {
        _clock = clock ?? TimeProvider.System;
        _teardownTimeout = teardownTimeout ?? DefaultTeardownTimeout;
    }

    public ShellSection Current { get; private set; } = ShellSection.Sale;

    /// <summary>
    /// The sale and the sales history are always reachable (voiding a sale asks for the branch PIN, not a permission);
    /// Clientes and Personal need ManageUsers.
    /// </summary>
    public static IReadOnlyList<ShellSection> Allowed(int? permissions) =>
        permissions is { } granted && ((Permission)granted).HasFlag(Permission.ManageUsers)
            ? [ShellSection.Sale, ShellSection.Sales, ShellSection.Customers, ShellSection.Staff]
            : [ShellSection.Sale, ShellSection.Sales];

    /// <summary>Switches to <paramref name="target"/>; false when it is not allowed, already current, or the old section is still being torn down
    /// (<see cref="TeardownPending"/>; a teardown that outlives the timeout no longer blocks).</summary>
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
    public bool TeardownPending =>
        _teardownStartedAt is { } started && _clock.GetUtcNow() - started < _teardownTimeout;

    /// <summary>
    /// The deferred section outlived the timeout without going idle: it no longer blocks navigation and
    /// the host must force its release (<see cref="CompleteTeardown"/> once it did). Navigation is never
    /// blocked for good by a hung request.
    /// </summary>
    public bool TeardownExpired =>
        _teardownStartedAt is { } started && _clock.GetUtcNow() - started >= _teardownTimeout;

    /// <summary>
    /// Like <see cref="Reconcile(int?)"/>, but a section with a request in flight is never torn
    /// down under it. The model still moves to the sale at once (<see cref="ReconcileOutcome.Deferred"/>),
    /// so the model and the screen never disagree; only the disposal of the old section waits for
    /// the host to call <see cref="CompleteTeardown"/> once it is idle.
    /// </summary>
    public ReconcileOutcome Reconcile(int? permissions, bool sectionBusy) =>
        Allowed(permissions).Contains(Current) ? ReconcileOutcome.Unchanged : BackToSale(sectionBusy);

    /// <summary>
    /// The server refused the operator for a management call (admin-console-field-fixes T5): the open section closes
    /// exactly like one the operator lost in <see cref="Reconcile(int?, bool)"/>, even though the cached permissions
    /// still allow it. Unchanged when the sale is already shown.
    /// </summary>
    public ReconcileOutcome Leave(bool sectionBusy) =>
        Current == ShellSection.Sale ? ReconcileOutcome.Unchanged : BackToSale(sectionBusy);

    private ReconcileOutcome BackToSale(bool sectionBusy)
    {
        Current = ShellSection.Sale;
        if (sectionBusy)
        {
            _teardownStartedAt = _clock.GetUtcNow();
            return ReconcileOutcome.Deferred;
        }

        return ReconcileOutcome.Switched;
    }

    /// <summary>The deferred section went idle: true when there was one to release.</summary>
    public bool CompleteTeardown()
    {
        var pending = _teardownStartedAt is not null;
        _teardownStartedAt = null;
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

    /// <summary>Cancels the request in flight (the section is being torn down); it still ends and raises <see cref="Idle"/>.</summary>
    void CancelPending();

    /// <summary>Raised when a network action ends and the section is no longer busy.</summary>
    event Action? Idle;
}

/// <summary>
/// Owns the section views of the main window: the active one and, after an operator change while a
/// request was in flight, the detached one awaiting teardown. UI-free (the window mirrors it into
/// <c>SectionHost</c>), so disposal is provable: every view is disposed exactly once and a release
/// never clears a host that already shows a new section.
/// </summary>
public sealed class SectionLifecycle
{
    public ISectionView? Active { get; private set; }

    public ISectionView? Detached { get; private set; }

    /// <summary>The detached section ended (idle) and was disposed; the argument says whether the host content must be cleared (no new section shown).</summary>
    public event Action<bool>? DetachedReleased;

    /// <summary>Replaces the active view (disposing the old one); null leaves the host empty.</summary>
    public void Show(ISectionView? next)
    {
        var previous = Active;
        Active = next;
        previous?.Dispose();
    }

    /// <summary>Takes the busy active view out of the shell: hidden, its request cancelled, disposed once idle or forced.</summary>
    public void DetachActive()
    {
        if (Active is not { } view)
        {
            return;
        }

        Active = null;
        Detached = view;
        view.Idle += OnDetachedIdle;
        view.CancelPending();
    }

    /// <summary>Disposes the detached view now (idle, or the teardown timed out). True when the host content should be cleared.</summary>
    public bool ReleaseDetached()
    {
        if (Detached is not { } view)
        {
            return false;
        }

        view.Idle -= OnDetachedIdle;
        Detached = null;
        view.Dispose();
        return Active is null;
    }

    private void OnDetachedIdle() => DetachedReleased?.Invoke(ReleaseDetached());
}
