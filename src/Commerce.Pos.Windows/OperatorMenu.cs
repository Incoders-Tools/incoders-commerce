using Commerce.Domain.Identity;

namespace Commerce.Pos.Windows;

public enum OperatorMenuAction
{
    SwitchOperator,
    SignOut,
    SignIn,
}

/// <summary>What the logged-operator menu shows: who is signed in, and what they can do.</summary>
public sealed record OperatorMenuState(string Title, string? Subtitle, IReadOnlyList<OperatorMenuAction> Actions);

/// <summary>
/// Decides the content of the logged-operator menu (pos-operator-session
/// "Operator Menu"). Adding an operator is deliberately not an action here: it
/// lives in Personal.
/// </summary>
public static class OperatorMenuPresenter
{
    public const string NoActiveOperator = "Sin operador activo";

    public static OperatorMenuState Build(CachedOperator? active, IReadOnlyList<CachedOperator> cachedOperators, DateTimeOffset now)
    {
        if (active is null)
        {
            return new OperatorMenuState(NoActiveOperator, null, [OperatorMenuAction.SignIn]);
        }

        var hasAnotherOperator = cachedOperators.Any(op => op.UserId != active.UserId && !op.IsStale(now));
        IReadOnlyList<OperatorMenuAction> actions = hasAnotherOperator
            ? [OperatorMenuAction.SwitchOperator, OperatorMenuAction.SignOut]
            : [OperatorMenuAction.SignOut];

        return new OperatorMenuState(active.Email, RoleSummary(active.Permissions), actions);
    }

    public static string RoleSummary(int permissions)
    {
        var granted = (Permission)permissions;
        if (granted.HasFlag(Permission.ManageUsers))
        {
            return "Administrador";
        }

        return granted.HasFlag(Permission.OperatePos) ? "Cajero" : "Sin acceso al punto de venta";
    }

    public static string Label(OperatorMenuAction action) => action switch
    {
        OperatorMenuAction.SwitchOperator => "Cambiar operador",
        OperatorMenuAction.SignOut => "Cerrar sesión",
        OperatorMenuAction.SignIn => "Iniciar sesión",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}

/// <summary>
/// The operator-menu actions over <see cref="CurrentOperator"/>. Both "Cambiar
/// operador" and "Cerrar sesión" end in the lock screen: the session only forgets
/// the operator, and the main window (which shows the lock layer whenever nobody is
/// signed in) does the rest.
///
/// Signing out never touches the cash session or the sale in progress: they stay
/// where they are, hidden behind the lock, and the next operator resumes them
/// (pos-operator-session "Sign-Out Keeps The Cash Session And The Cart").
/// </summary>
public sealed class OperatorSessionActions(CurrentOperator current)
{
    public void SwitchOperator() => current.Clear();

    public void SignOut() => current.Clear();

    /// <summary>
    /// Syncs the active operator with the terminal's stored operators after they
    /// changed (removed in Personal, dropped by the status check): a removed operator
    /// is signed out, one whose permissions changed picks up the fresh record. Safe
    /// to call after every sync: an unchanged operator keeps the same instance.
    /// </summary>
    public void Reconcile(IReadOnlyList<CachedOperator> stored)
    {
        if (current.Value is not { } active)
        {
            return;
        }

        var match = stored.FirstOrDefault(op => op.UserId == active.UserId);
        if (match is null)
        {
            current.Clear();
        }
        else if (match.Permissions != active.Permissions || match.Email != active.Email)
        {
            current.Set(match);
        }
    }
}
