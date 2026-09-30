namespace Commerce.Updater;

/// <summary>
/// The POS install wizard's stages as one injectable, testable service; the
/// WPF window only shows its progress and outcome. Order (fail closed, nothing
/// is touched before the previous gate passed):
/// preflight -&gt; download -&gt; verify (SHA256, then signature and signer against
/// the CONFIGURED trusted publisher) -&gt; quiesce (no sale being built, durable
/// work idle) -&gt; packaged check -&gt; verified <c>branch.db</c> backup -&gt; restart
/// registration and pending marker -&gt; OS package install. Never installs
/// silently: the caller starts it explicitly.
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

        try
        {
            var staged = await StageAndVerifyAsync(package, progress, cancellationToken);
            if (staged.Failure is not null)
            {
                return staged.Failure;
            }

            return await InstallAsync(check, package, staged.Path!, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Refuse(UpdateFailureReason.Cancelled, UpdateInstallStage.Download, "La actualización se canceló.");
        }
    }

    private async Task<(string? Path, UpdateInstallOutcome? Failure)> StageAndVerifyAsync(
        UpdatePackage package, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new UpdateProgress(UpdateInstallStage.Download, 0, "Descargando el paquete..."));
        var download = await downloader.DownloadAsync(
            package,
            options.StagingDirectory,
            new Progress<double>(fraction => progress?.Report(new UpdateProgress(UpdateInstallStage.Download, fraction, "Descargando el paquete..."))),
            cancellationToken);
        if (!download.Ok || download.Path is null)
        {
            return (null, Refuse(UpdateFailureReason.DownloadFailed, UpdateInstallStage.Download,
                $"No se pudo descargar el paquete. {download.Detail}".TrimEnd()));
        }

        progress?.Report(new UpdateProgress(UpdateInstallStage.Verify, null, "Verificando integridad y firma..."));
        var computedHash = PackageDownloader.ComputeSha256(download.Path);
        var hashMatches = string.Equals(computedHash, package.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!hashMatches)
        {
            DeleteQuietly(download.Path);
            return (null, Refuse(UpdateFailureReason.HashMismatch, UpdateInstallStage.Verify,
                "La huella SHA256 del paquete no coincide con la publicada. No se instaló nada."));
        }

        var signature = signatures.Inspect(download.Path);
        var signatureValid = signature.Status == PackageSignatureStatus.Valid;
        var verification = PackageVerifier.Verify(
            new ReleasePackage(
                PackagePath: Path.GetRelativePath(options.StagingDirectory, download.Path),
                PublisherId: signature.SignerSubject ?? string.Empty,
                Format: package.Format,
                ContentHash: computedHash,
                SignatureValid: signatureValid,
                IsAttested: true,
                AppVersion: 0,
                SyncContractVersion: 0,
                SchemaVersion: 0),
            new ReleaseManifest(options.TrustedPublisher.Trim(), package.Format, package.Sha256.Trim().ToLowerInvariant()),
            options.StagingDirectory);

        if (!signatureValid)
        {
            DeleteQuietly(download.Path);
            return (null, Refuse(UpdateFailureReason.SignatureInvalid, UpdateInstallStage.Verify,
                $"La firma del paquete no es válida ({signature.Status}). No se instaló nada."));
        }

        if (!verification.Ok || !SameSubject(signature.SignerSubject, options.TrustedPublisher))
        {
            DeleteQuietly(download.Path);
            return (null, Refuse(UpdateFailureReason.SignerMismatch, UpdateInstallStage.Verify,
                "El paquete está firmado por un editor que esta terminal no reconoce. No se instaló nada."));
        }

        return (download.Path, null);
    }

    private async Task<UpdateInstallOutcome> InstallAsync(
        UpdateCheckResult check, UpdatePackage package, string stagedPath, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new UpdateProgress(UpdateInstallStage.Quiesce, null, "Comprobando que no haya una venta en curso..."));
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
            if (!packagedApp.IsPackaged)
            {
                return Refuse(UpdateFailureReason.NotPackaged, UpdateInstallStage.Install,
                    "Esta terminal no está instalada como aplicación empaquetada (modo desarrollo), por lo que no puede actualizarse a sí misma. " +
                    "La descarga y la verificación se completaron; no se instaló nada.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new UpdateProgress(UpdateInstallStage.Backup, null, "Guardando una copia de seguridad de la base local..."));
            string backupPath;
            try
            {
                backupPath = backup.CreateVerifiedBackup(options.DatabasePath, options.BackupDirectory);
            }
            catch (UpgradeBackupVerificationException ex)
            {
                return Refuse(UpdateFailureReason.BackupFailed, UpdateInstallStage.Backup,
                    $"No se pudo crear una copia de seguridad verificada. No se instaló nada. {ex.Message}");
            }

            progress?.Report(new UpdateProgress(UpdateInstallStage.Install, null, "Instalando la actualización..."));
            restartRegistrar.Register();
            pendingStore.Record(new PendingUpgrade(
                check.LocalVersion.ToString(), check.AvailableVersion!.ToString(), backupPath, _clock()));

            UpdateInstallerResult result;
            try
            {
                result = await installer.InstallAsync(stagedPath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new UpdateInstallerResult(false, ex.Message);
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
