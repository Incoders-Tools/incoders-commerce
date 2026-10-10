using System.Globalization;
using Commerce.BranchNode;
using Commerce.Domain.Tenancy;

namespace Commerce.Pos.Windows;

/// <summary>
/// The red footer notice of an organization that is behind on its payment (odd/tasks/organization-account-standing.md
/// T8). Every signed-in operator sees it, so a cashier passes it on to the owner; the closing line depends on the role.
/// It says "acceso web" on purpose: the POS keeps selling in every state (ADR-002), so "servicio suspendido" would be
/// false for the person reading it. Built from the last synced inputs with the same domain rule the cloud uses, on the
/// terminal's business day, so the countdown moves offline.
/// </summary>
public sealed record AccountStandingNotice(string Text, string? ToolTip)
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    /// <returns>The notice to show, or null when there is nothing to warn about (or nothing was ever synced).</returns>
    public static AccountStandingNotice? Compose(AccountStandingReplica? inputs, DateOnly today, bool isAdmin)
    {
        if (inputs is null)
        {
            return null;
        }

        // Inputs arrive over the wire: a value the rule rejects shows nothing rather than taking the shell down. A manual
        // suspension does not depend on the grace days and is always shown.
        if (!inputs.Suspended && !AccountStandingRules.IsValidGraceDays(inputs.GraceDays))
        {
            return null;
        }

        var standing = AccountStandingRules.Evaluate(inputs.DueOn, inputs.GraceDays, inputs.Suspended, today);
        var closing = isAdmin ? "Comuníquese con Incoders." : "Informe al administrador.";
        var toolTip = standing.SuspendsOn is { } suspendsOn
            ? "Fecha de suspensión: " + suspendsOn.ToString("dd/MM/yyyy", Spanish)
            : null;

        return standing.Status switch
        {
            AccountStandingStatus.Overdue when standing.DaysLeft is { } daysLeft => new AccountStandingNotice(
                $"Pago pendiente: el acceso web se suspende en {daysLeft} {(daysLeft == 1 ? "día" : "días")}. {closing}", toolTip),
            AccountStandingStatus.Suspended => new AccountStandingNotice(
                $"Acceso web suspendido por pago pendiente. {closing}", toolTip),
            _ => null,
        };
    }
}
