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

public enum OperatorSignInScreen
{
    PinPicker,
    Provision,
}

/// <summary>
/// Which screen serves a sign-in: the PIN picker when at least one fresh
/// operator is cached, otherwise the provisioning form. The latter only ever
/// applies to first run (nothing cached yet) or when every cached operator went
/// stale, so the terminal can always be set up at startup.
/// </summary>
public static class OperatorSignInPlanner
{
    public static OperatorSignInScreen ScreenFor(IReadOnlyList<CachedOperator> cachedOperators, DateTimeOffset now) =>
        cachedOperators.Any(op => !op.IsStale(now)) ? OperatorSignInScreen.PinPicker : OperatorSignInScreen.Provision;
}

public enum OperatorLoginMode
{
    /// <summary>Cached operators only; never shows provisioning (switch operator, after sign-out).</summary>
    PinPicker,

    /// <summary>The PIN picker, or the provisioning form when no fresh operator exists (startup, sign in).</summary>
    PinPickerOrFirstRun,
}

/// <summary>
/// The operator-menu actions over <see cref="CurrentOperator"/>. The prompt
/// shows the sign-in UI and returns the operator who signed in (null when
/// cancelled).
///
/// Signing out never touches the cash session: it stays open (pos-operator-session
/// "Operator Sign-Out Keeps the Cash Session") and sales keep working, attributed
/// to the installation until somebody signs in again.
/// </summary>
public sealed class OperatorSessionActions(CurrentOperator current, Func<OperatorLoginMode, CachedOperator?> prompt)
{
    public void SwitchOperator() => Adopt(prompt(OperatorLoginMode.PinPicker));

    public void SignIn() => Adopt(prompt(OperatorLoginMode.PinPickerOrFirstRun));

    public void SignOut()
    {
        current.Clear();
        Adopt(prompt(OperatorLoginMode.PinPicker));
    }

    /// <summary>
    /// Syncs the active operator with the terminal's stored operators after
    /// Personal changed them: a removed operator is signed out, a re-provisioned
    /// one picks up its fresh permissions.
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
        else if (match != active)
        {
            current.Set(match);
        }
    }

    private void Adopt(CachedOperator? signedIn)
    {
        if (signedIn is not null)
        {
            current.Set(signedIn);
        }
    }
}
