using System.Windows;

namespace Commerce.Pos.Windows;

/// <summary>
/// Shows the sign-in screen that fits the mode and returns the operator who
/// signed in (null when cancelled or when there was nothing to show). The one
/// place that decides between the PIN picker and first-run provisioning, so
/// startup, "Iniciar sesión" and the open-cash prompt behave the same way.
/// </summary>
public static class OperatorSignInFlow
{
    public static CachedOperator? Run(
        OperatorLoginMode mode, Window? owner,
        OperatorProvisioningClient provisioningClient, LocalOperatorStore operatorStore, string deviceToken)
    {
        var screen = OperatorSignInPlanner.ScreenFor(operatorStore.Load(), DateTimeOffset.UtcNow);

        if (screen == OperatorSignInScreen.PinPicker)
        {
            var picker = new OperatorLoginWindow(operatorStore) { Owner = owner };
            return picker.ShowDialog() == true ? picker.ActiveOperator : null;
        }

        if (mode == OperatorLoginMode.PinPicker)
        {
            // Nobody to pick, and this mode never provisions: adding operators is a Personal task.
            return null;
        }

        var provision = new ProvisionOperatorWindow(provisioningClient, operatorStore, deviceToken, allowContinueWithoutOperator: true)
        {
            Owner = owner
        };
        return provision.ShowDialog() == true ? provision.ProvisionedOperator : null;
    }
}
