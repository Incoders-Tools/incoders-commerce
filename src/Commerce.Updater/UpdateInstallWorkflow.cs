using Microsoft.Data.Sqlite;

namespace Commerce.Updater;

/// <summary>
/// The POS install wizard's stages as one injectable, testable service; the
/// WPF window only shows its progress and outcome. Order (fail closed, nothing
/// is touched before the previous gate passed):
/// preflight -&gt; download -&gt; verify (SHA256, then signature and signer: the
/// pinned certificate thumbprint when configured, otherwise the CONFIGURED
/// trusted publisher subject with a fully trusted signature) -&gt; quiesce (no sale being built, durable
/// work idle) -&gt; packaged check -&gt; verified <c>branch.db</c> backup -&gt; restart
/// registration and pending marker -&gt; OS package install. Never installs
/// silently: the caller starts it explicitly. Every failure is typed and
/// carries the stage that was running, including cancels and unexpected errors.
/// </summary>
public sealed class UpdateInstallWorkflow(
    PackageDownloader downloader,
    IPackageSignatureVerifier signatures,
    IUpgradeBackup backup,
    IBranchNodeQuiescence quiescence,
    IPackagedAppInfo packagedApp,
    IApplicationRestartRegistrar restartRegistrar,
    IUpdateInstaller installer,
    PendingUpgradeStore pendingStore,
    Func<bool> isSaleInProgress,
    UpdateInstallOptions options,
    Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The stage currently running, so a cancel or an unexpected error is reported where it happened.</summary>
    private sealed class RunState
    {
        public UpdateInstallStage Stage { get; set; } = UpdateInstallStage.Preflight;
    }

    private static void Enter(RunState state, UpdateInstallStage stage, IProgress<UpdateProgress>? progress, double? fraction, string message)
    {
        state.Stage = stage;
        progress?.Report(new UpdateProgress(stage, fraction, message));
    }

    /// <summary>Reports inline (on the reporting thread) so stage and download ticks keep their order.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public async Task<UpdateInstallOutcome> RunAsync(
        UpdateCheckResult check,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (check.Package is not { } package || !check.IsUpdateAvailable || check.AvailableVersion is null)
        {
            return Refuse(UpdateFailureReason.UnsupportedPackage, UpdateInstallStage.Preflight, "No hay una actualización disponible para instalar.");
        }

        if (package.Format != PackageFormat.Msix)
        {
            return Refuse(UpdateFailureReason.UnsupportedPackage, UpdateInstallStage.Preflight,
                "Este tipo de paquete todavía no se puede instalar desde la terminal.");
        }

        if (package.AttestationRequired)
        {
            return Refuse(UpdateFailureReason.UnsupportedPackage, UpdateInstallStage.Preflight,
                "El paquete exige una atestación que esta versión no puede verificar.");
        }

        if (!SameSubject(package.PublisherId, options.TrustedPublisher))
        {
            return Refuse(UpdateFailureReason.PublisherNotTrusted, UpdateInstallStage.Preflight,
                "El editor del paquete no coincide con el editor de confianza configurado en esta terminal.");
        }

        if (options.TrustedThumbprint is { } configuredPin && NormalizeThumbprint(configuredPin) is null)
        {
            return Refuse(UpdateFailureReason.PublisherNotTrusted, UpdateInstallStage.Preflight,
                "La huella de confianza configurada en esta terminal no es válida (se esperan 64 caracteres hexadecimales SHA-256).");
        }

        var state = new RunState();
        try
        {
            var staged = await StageAndVerifyAsync(package, progress, state, cancellationToken);
            if (staged.Failure is not null)
            {
                return staged.Failure;
            }

            return await InstallAsync(check, package, staged.Path!, progress, state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Refuse(UpdateFailureReason.Cancelled, state.Stage, "La actualización se canceló.");
        }
        catch (Exception ex)
        {
            return Refuse(UpdateFailureReason.UnexpectedError, state.Stage, $"Error inesperado: {ex.Message}");
        }
    }

    private async Task<(string? Path, UpdateInstallOutcome? Failure)> StageAndVerifyAsync(
        UpdatePackage package, IProgress<UpdateProgress>? progress, RunState state, CancellationToken cancellationToken)
    {
        Enter(state, UpdateInstallStage.Download, progress, 0, "Descargando el paquete...");
        var download = await downloader.DownloadAsync(
            package,
            options.StagingDirectory,
            new InlineProgress<double>(fraction => progress?.Report(new UpdateProgress(UpdateInstallStage.Download, fraction, "Descargando el paquete..."))),
            cancellationToken);
        if (!download.Ok || download.Path is null)
        {
            return (null, Refuse(UpdateFailureReason.DownloadFailed, UpdateInstallStage.Download,
                $"No se pudo descargar el paquete. {download.Detail}".TrimEnd()));
        }

        Enter(state, UpdateInstallStage.Verify, progress, null, "Verificando integridad y firma...");
        var computedHash = PackageDownloader.ComputeSha256(download.Path);
        var hashMatches = string.Equals(computedHash, package.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!hashMatches)
        {
            DeleteQuietly(download.Path);
            return (null, Refuse(UpdateFailureReason.HashMismatch, UpdateInstallStage.Verify,
                "La huella SHA256 del paquete no coincide con la publicada. No se instaló nada."));
        }

        // The hash, signature status and signer below are the single source of
        // the trust decision. PackageVerifier is not used here: it is built for a
        // fully attested ReleasePackage (app/sync/schema versions) that an
        // install from a manifest does not have, and feeding it placeholders
        // only duplicated these checks.
        var signature = signatures.Inspect(download.Path);
        var pin = options.TrustedThumbprint is null ? null : NormalizeThumbprint(options.TrustedThumbprint);
        var acceptableStatus = signature.Status == PackageSignatureStatus.Valid ||
            (pin is not null && signature.Status == PackageSignatureStatus.UntrustedRoot);
        if (!acceptableStatus)
        {
            DeleteQuietly(download.Path);
            return (null, Refuse(UpdateFailureReason.SignatureInvalid, UpdateInstallStage.Verify,
                $"La firma del paquete no es válida ({signature.Status}). No se instaló nada."));
        }

        var signerMatches = pin is not null
            ? string.Equals(NormalizeThumbprint(signature.SignerThumbprint), pin, StringComparison.Ordinal)
            : SameSubject(signature.SignerSubject, options.TrustedPublisher);
        if (!signerMatches)
        {
            DeleteQuietly(download.Path);
            return (null, Refuse(UpdateFailureReason.SignerMismatch, UpdateInstallStage.Verify,
                "El paquete está firmado por un editor que esta terminal no reconoce. No se instaló nada."));
        }

        return (download.Path, null);
    }

    private async Task<UpdateInstallOutcome> InstallAsync(
        UpdateCheckResult check, UpdatePackage package, string stagedPath, IProgress<UpdateProgress>? progress, RunState state, CancellationToken cancellationToken)
    {
        Enter(state, UpdateInstallStage.Quiesce, progress, null, "Comprobando que no haya una venta en curso...");
        if (isSaleInProgress())
        {
            return Refuse(UpdateFailureReason.SaleInProgress, UpdateInstallStage.Quiesce,
                "Hay una venta en curso. Finalícela o cancélela y vuelva a intentar la actualización.");
        }

        if (!quiescence.TryQuiesce(options.QuiesceTimeout))
        {
            return Refuse(UpdateFailureReason.SafeWindowUnavailable, UpdateInstallStage.Quiesce,
                "La terminal todavía tiene trabajo en curso. Espere unos segundos y vuelva a intentar.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!packagedApp.IsPackaged)
            {
                return Refuse(UpdateFailureReason.NotPackaged, UpdateInstallStage.Quiesce,
                    "Esta terminal no está instalada como aplicación empaquetada (modo desarrollo), por lo que no puede actualizarse a sí misma. " +
                    "La descarga y la verificación se completaron; no se instaló nada.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Enter(state, UpdateInstallStage.Backup, progress, null, "Guardando una copia de seguridad de la base local...");
            string backupPath;
            try
            {
                backupPath = backup.CreateVerifiedBackup(options.DatabasePath, options.BackupDirectory);
            }
            catch (Exception ex) when (ex is UpgradeBackupVerificationException or IOException or UnauthorizedAccessException or SqliteException)
            {
                return Refuse(UpdateFailureReason.BackupFailed, UpdateInstallStage.Backup,
                    $"No se pudo crear una copia de seguridad verificada. No se instaló nada. {ex.Message}");
            }

            Enter(state, UpdateInstallStage.Install, progress, null, "Instalando la actualización...");
            try
            {
                pendingStore.Record(new PendingUpgrade(
                    check.LocalVersion.ToString(), check.AvailableVersion!.ToString(), backupPath, _clock()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refuse(UpdateFailureReason.PendingMarkerFailed, UpdateInstallStage.Install,
                    $"No se pudo registrar la actualización pendiente. No se instaló nada. {ex.Message}");
            }

            UpdateInstallerResult result;
            try
            {
                restartRegistrar.Register();
                result = await installer.InstallAsync(stagedPath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new UpdateInstallerResult(false, ex.Message);
            }
            catch (OperationCanceledException)
            {
                // A cancelled install must not leave a marker that the next start
                // would report as a pending upgrade.
                pendingStore.Clear();
                throw;
            }

            if (!result.Succeeded)
            {
                pendingStore.Clear();
                return new UpdateInstallOutcome(false, UpdateFailureReason.InstallFailed, UpdateInstallStage.Install,
                    $"La instalación falló y la versión actual sigue intacta. Copia de seguridad conservada. {result.Detail}".TrimEnd(),
                    backupPath);
            }

            return new UpdateInstallOutcome(true, null, UpdateInstallStage.Install,
                "La actualización se instaló. La terminal se reiniciará para usar la nueva versión.", backupPath);
        }
        finally
        {
            quiescence.Resume();
        }
    }

    private static UpdateInstallOutcome Refuse(UpdateFailureReason reason, UpdateInstallStage stage, string message) =>
        new(false, reason, stage, message);

    /// <summary>Canonical SHA-256 thumbprint (64 uppercase hex characters), or null when the text is not one.</summary>
    private static string? NormalizeThumbprint(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var hex = new string(text.Where(c => c is not (' ' or ':' or '-')).ToArray()).ToUpperInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private static bool SameSubject(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next download cleans the staging directory again.
        }
    }
}
