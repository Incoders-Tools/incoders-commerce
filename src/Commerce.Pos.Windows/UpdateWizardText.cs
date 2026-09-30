using System.Globalization;
using Commerce.Updater;

namespace Commerce.Pos.Windows;

public sealed record UpdateWizardDetails(
    string CurrentVersion,
    string TargetVersion,
    string Format,
    string Size,
    string Compatibility);

/// <summary>Operator-facing (Spanish) text for the update wizard, kept out of the window so it is testable.</summary>
public static class UpdateWizardText
{
    public static IReadOnlyList<UpdateInstallStage> VisibleStages { get; } =
    [
        UpdateInstallStage.Download,
        UpdateInstallStage.Verify,
        UpdateInstallStage.Quiesce,
        UpdateInstallStage.Backup,
        UpdateInstallStage.Install
    ];

    public static UpdateWizardDetails Describe(UpdateCheckResult check, UpdateEnvironment environment) => new(
        check.LocalVersion.ToString(),
        check.AvailableVersion?.ToString() ?? "-",
        check.Package is { } package ? package.Format.ToString().ToUpperInvariant() : "-",
        FormatSize(check.Package?.SizeBytes ?? 0),
        $"Compatible con esta terminal (Windows {environment.WindowsBuild}, {environment.Architecture})");

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "Tamaño no informado";
        }

        const double kilo = 1024;
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{Decimal(bytes / kilo)} KB",
            < 1024L * 1024 * 1024 => $"{Decimal(bytes / kilo / kilo)} MB",
            _ => $"{Decimal(bytes / kilo / kilo / kilo)} GB"
        };
    }

    public static string StageLabel(UpdateInstallStage stage) => stage switch
    {
        UpdateInstallStage.Download => "Descargar el paquete",
        UpdateInstallStage.Verify => "Verificar integridad y firma",
        UpdateInstallStage.Quiesce => "Preparar la terminal",
        UpdateInstallStage.Backup => "Copia de seguridad de la base local",
        UpdateInstallStage.Install => "Instalar y reiniciar",
        _ => "Comprobaciones previas"
    };

    public static string FailureTitle(UpdateFailureReason reason) => reason switch
    {
        UpdateFailureReason.HashMismatch or UpdateFailureReason.SignatureInvalid or
            UpdateFailureReason.SignerMismatch or UpdateFailureReason.PublisherNotTrusted => "Verificación fallida",
        UpdateFailureReason.SaleInProgress => "Venta en curso",
        UpdateFailureReason.SafeWindowUnavailable => "La terminal está ocupada",
        UpdateFailureReason.NotPackaged => "No se puede instalar en esta ejecución",
        UpdateFailureReason.DownloadFailed => "No se pudo descargar",
        UpdateFailureReason.BackupFailed => "No se pudo respaldar la base local",
        UpdateFailureReason.InstallFailed => "La instalación falló",
        UpdateFailureReason.UnsupportedPackage => "Paquete no compatible",
        UpdateFailureReason.Cancelled => "Actualización cancelada",
        _ => "No se pudo actualizar"
    };

    private static string Decimal(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
}
