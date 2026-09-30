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

    /// <summary>Switches to <paramref name="target"/>; false when it is not allowed or already current.</summary>
    public bool Navigate(ShellSection target, int? permissions)
    {
        if (target == Current || !Allowed(permissions).Contains(target))
        {
            return false;
        }

        Current = target;
        return true;
    }

    /// <summary>After the operator changed: a section the new operator may not open falls back to the sale.</summary>
    public bool Reconcile(int? permissions)
    {
        if (Allowed(permissions).Contains(Current))
        {
            return false;
        }

        Current = ShellSection.Sale;
        return true;
    }
}

/// <summary>A management section hosted in the main window's content area.</summary>
public interface ISectionView : IDisposable
{
    /// <summary>A network action is in flight: the shell keeps the section until it ends.</summary>
    bool IsBusy { get; }
}
