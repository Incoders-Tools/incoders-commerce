using System.Net;
using System.Security.Cryptography;
using Commerce.Updater;
using Microsoft.Data.Sqlite;

namespace Commerce.Upgrade;

public sealed class UpdateInstallWorkflowTests : IDisposable
{
    private const string Trusted = "CN=Incoders Commerce (Interim)";
    private static readonly byte[] Payload = [10, 20, 30, 40, 50];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"upd-wf-{Guid.NewGuid():N}");
    private readonly List<string> _calls = [];

    public UpdateInstallWorkflowTests()
    {
        Directory.CreateDirectory(_root);
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);";
        command.ExecuteNonQuery();
    }

    private string DbPath => Path.Combine(_root, "branch.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static UpdatePackage Package(string? sha = null, string publisher = Trusted, bool attestation = false, PackageFormat format = PackageFormat.Msix) => new()
    {
        Format = format,
        Architecture = "x64",
        Url = "https://example.test/dl/Commerce.Pos.Windows-1.4.0-win-x64.msix",
        Sha256 = sha ?? Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant(),
        SizeBytes = Payload.Length,
        PublisherId = publisher,
        SignatureRequired = true,
        AttestationRequired = attestation
    };

    private static UpdateCheckResult Available(UpdatePackage package) =>
        new(UpdateCheckStatus.Available, new Version(1, 3, 0), new Version(1, 4, 0), package);

    private sealed class PayloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
    }

    private sealed class FaultingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw failure;
    }

    private sealed class FakeSignatures(PackageSignature signature, Action? onInspect = null) : IPackageSignatureVerifier
    {
        public PackageSignature Inspect(string packagePath)
        {
            onInspect?.Invoke();
            return signature;
        }
    }

    private sealed class FakePackaged(bool packaged) : IPackagedAppInfo
    {
        public bool IsPackaged => packaged;
    }

    private sealed class FakeQuiescence(List<string> calls, bool canQuiesce = true) : IBranchNodeQuiescence
    {
        public bool IsQuiesced { get; private set; }

        public bool TryQuiesce(TimeSpan timeout)
        {
            calls.Add("quiesce");
            IsQuiesced = canQuiesce;
            return canQuiesce;
        }

        public void Resume()
        {
            calls.Add("resume");
            IsQuiesced = false;
        }
    }

    private sealed class RecordingBackup(List<string> calls, IUpgradeBackup inner, Exception? failure = null) : IUpgradeBackup
    {
        public string CreateVerifiedBackup(string sourceDbPath, string backupDirectory)
        {
            calls.Add("backup");
            if (failure is not null)
            {
                throw failure;
            }

            return inner.CreateVerifiedBackup(sourceDbPath, backupDirectory);
        }

        public void RestoreFromBackup(string backupPath, string targetDbPath) => inner.RestoreFromBackup(backupPath, targetDbPath);
    }

    private sealed class FakeInstaller(List<string> calls, bool succeed = true, Action? onInstall = null) : IUpdateInstaller
    {
        public Task<UpdateInstallerResult> InstallAsync(string packagePath, CancellationToken cancellationToken)
        {
            calls.Add("install");
            onInstall?.Invoke();

            return Task.FromResult(new UpdateInstallerResult(succeed, succeed ? null : "0x80073CF9 deployment failed"));
        }
    }

    private sealed class FakeRestart(List<string> calls) : IApplicationRestartRegistrar
    {
        public bool Register()
        {
            calls.Add("restart-register");
            return true;
        }
    }

    private UpdateInstallWorkflow Build(
        PackageSignature? signature = null,
        bool packaged = true,
        bool saleInProgress = false,
        bool canQuiesce = true,
        bool installSucceeds = true,
        Exception? backupFailure = null,
        string? pin = null,
        HttpMessageHandler? handler = null,
        Action? onInstall = null,
        Func<bool>? isSaleInProgress = null,
        Action? onInspect = null,
        string? pendingPath = null)
    {
        var options = new UpdateInstallOptions(
            StagingDirectory: Path.Combine(_root, "staging"),
            DatabasePath: DbPath,
            BackupDirectory: Path.Combine(_root, "backups"),
            TrustedPublisher: Trusted,
            QuiesceTimeout: TimeSpan.FromMilliseconds(50),
            TrustedThumbprint: pin);
        return new UpdateInstallWorkflow(
            new PackageDownloader(new HttpClient(handler ?? new PayloadHandler())),
            new FakeSignatures(signature ?? new PackageSignature(PackageSignatureStatus.Valid, Trusted), onInspect),
            new RecordingBackup(_calls, new SqliteUpgradeBackup(), backupFailure),
            new FakeQuiescence(_calls, canQuiesce),
            new FakePackaged(packaged),
            new FakeRestart(_calls),
            new FakeInstaller(_calls, installSucceeds, onInstall),
            new PendingUpgradeStore(pendingPath ?? Path.Combine(_root, "pending.json")),
            isSaleInProgress ?? (() => saleInProgress),
            options);
    }

    private static readonly string Pin = new('a', 64);

    private static PackageSignature Signed(PackageSignatureStatus status, string? thumbprint = null, string subject = Trusted) =>
        new(status, subject, null, thumbprint);

    [Fact]
    public async Task HappyPath_RunsStagesInOrder_AndBacksUpBeforeInstall()
    {
        var stages = new List<UpdateInstallStage>();

        var outcome = await Build().RunAsync(Available(Package()), new SyncProgress(p => stages.Add(p.Stage)), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["quiesce", "backup", "restart-register", "install", "resume"], _calls);
        Assert.NotNull(outcome.BackupPath);
        Assert.True(File.Exists(outcome.BackupPath));
        // Progress is reported synchronously and in order: a late download tick
        // can never move the wizard back after a later stage started.
        Assert.Equal(
            [UpdateInstallStage.Download, UpdateInstallStage.Verify, UpdateInstallStage.Quiesce, UpdateInstallStage.Backup, UpdateInstallStage.Install],
            stages.Distinct().ToList());
        Assert.Equal(stages.OrderBy(stage => stage).ToList(), stages);
        Assert.NotNull(new PendingUpgradeStore(Path.Combine(_root, "pending.json")).Read());
    }

    [Fact]
    public async Task HashMismatch_AbortsBeforeAnythingIsTouched_AndRemovesTheStagedFile()
    {
        var outcome = await Build().RunAsync(Available(Package(sha: new string('a', 64))), null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UpdateFailureReason.HashMismatch, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Verify, outcome.Stage);
        Assert.Empty(_calls);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "staging")));
    }

    [Theory]
    [InlineData(PackageSignatureStatus.NotSigned)]
    [InlineData(PackageSignatureStatus.Invalid)]
    [InlineData(PackageSignatureStatus.UntrustedRoot)]
    [InlineData(PackageSignatureStatus.Unknown)]
    public async Task SignatureNotValid_AbortsBeforeInstall(PackageSignatureStatus status)
    {
        var outcome = await Build(new PackageSignature(status, Trusted)).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UpdateFailureReason.SignatureInvalid, outcome.Reason);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task SignerDifferentFromTrustedPublisher_AbortsBeforeInstall()
    {
        var outcome = await Build(new PackageSignature(PackageSignatureStatus.Valid, "CN=Someone Else"))
            .RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UpdateFailureReason.SignerMismatch, outcome.Reason);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task ManifestPublisherNotTheConfiguredOne_IsRefusedBeforeDownloading()
    {
        var outcome = await Build().RunAsync(Available(Package(publisher: "CN=Other")), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.PublisherNotTrusted, outcome.Reason);
        Assert.False(Directory.Exists(Path.Combine(_root, "staging")));
    }

    [Fact]
    public async Task TrustedPublisherIsConfiguration_SwappingItNeedsNoCodeChange()
    {
        const string commercial = "CN=Incoders SRL, O=Incoders";
        var options = new UpdateInstallOptions(Path.Combine(_root, "staging"), DbPath, Path.Combine(_root, "backups"), commercial, TimeSpan.FromMilliseconds(50));
        var workflow = new UpdateInstallWorkflow(
            new PackageDownloader(new HttpClient(new PayloadHandler())),
            new FakeSignatures(new PackageSignature(PackageSignatureStatus.Valid, commercial)),
            new SqliteUpgradeBackup(), new FakeQuiescence(_calls), new FakePackaged(true), new FakeRestart(_calls),
            new FakeInstaller(_calls), new PendingUpgradeStore(Path.Combine(_root, "pending.json")), () => false, options);

        var outcome = await workflow.RunAsync(Available(Package(publisher: commercial)), null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task SaleInProgress_RefusesBeforeBackupAndInstall()
    {
        var outcome = await Build(saleInProgress: true).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UpdateFailureReason.SaleInProgress, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Quiesce, outcome.Stage);
        Assert.DoesNotContain("backup", _calls);
        Assert.DoesNotContain("install", _calls);
    }

    [Fact]
    public async Task DurableWorkNotQuiescent_Refuses()
    {
        var outcome = await Build(canQuiesce: false).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.SafeWindowUnavailable, outcome.Reason);
        Assert.DoesNotContain("install", _calls);
    }

    [Fact]
    public async Task Unpackaged_StopsBeforeBackupAndInstall_WithAClearMessage()
    {
        var outcome = await Build(packaged: false).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UpdateFailureReason.NotPackaged, outcome.Reason);
        Assert.Contains("instalada", outcome.Message);
        Assert.DoesNotContain("backup", _calls);
        Assert.DoesNotContain("install", _calls);
        Assert.Contains("resume", _calls);
    }

    [Fact]
    public async Task BackupFailure_AbortsBeforeInstall()
    {
        var outcome = await Build(backupFailure: new UpgradeBackupVerificationException("disk full")).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.BackupFailed, outcome.Reason);
        Assert.DoesNotContain("install", _calls);
    }

    [Fact]
    public async Task InstallFailure_IsTyped_KeepsTheBackup_AndClearsThePendingMarker()
    {
        var outcome = await Build(installSucceeds: false).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UpdateFailureReason.InstallFailed, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Install, outcome.Stage);
        Assert.Contains("0x80073CF9", outcome.Message);
        Assert.NotNull(outcome.BackupPath);
        Assert.True(File.Exists(outcome.BackupPath));
        Assert.Null(new PendingUpgradeStore(Path.Combine(_root, "pending.json")).Read());
        Assert.Equal("resume", _calls[^1]);
    }

    [Fact]
    public async Task NonMsixOrAttestationRequired_IsRefused()
    {
        Assert.Equal(UpdateFailureReason.UnsupportedPackage,
            (await Build().RunAsync(Available(Package(format: PackageFormat.Msi)), null, CancellationToken.None)).Reason);
        Assert.Equal(UpdateFailureReason.UnsupportedPackage,
            (await Build().RunAsync(Available(Package(attestation: true)), null, CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task NoAvailableUpdate_IsRefused()
    {
        var outcome = await Build().RunAsync(new UpdateCheckResult(UpdateCheckStatus.UpToDate, new Version(1, 4, 0)), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.UnsupportedPackage, outcome.Reason);
    }

    [Theory]
    [InlineData(PackageSignatureStatus.Valid)]
    [InlineData(PackageSignatureStatus.UntrustedRoot)]
    public async Task PinnedSigner_IsAccepted_ForValidAndUntrustedRoot_EvenWhenSubjectDiffers(PackageSignatureStatus status)
    {
        var outcome = await Build(Signed(status, Pin.ToUpperInvariant(), subject: "CN=Renamed"), pin: Pin)
            .RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Contains("install", _calls);
    }

    [Theory]
    [InlineData(PackageSignatureStatus.Valid)]
    [InlineData(PackageSignatureStatus.UntrustedRoot)]
    public async Task PinnedSigner_WithADifferentCertificate_IsRefused_EvenWithTheTrustedSubject(PackageSignatureStatus status)
    {
        var outcome = await Build(Signed(status, new string('b', 64)), pin: Pin)
            .RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.SignerMismatch, outcome.Reason);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task PinnedSigner_WithoutAThumbprintInTheSignature_IsRefused()
    {
        var outcome = await Build(Signed(PackageSignatureStatus.UntrustedRoot, null), pin: Pin)
            .RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.SignerMismatch, outcome.Reason);
    }

    [Theory]
    [InlineData(PackageSignatureStatus.NotSigned)]
    [InlineData(PackageSignatureStatus.Invalid)]
    [InlineData(PackageSignatureStatus.Expired)]
    [InlineData(PackageSignatureStatus.Unknown)]
    public async Task PinnedSigner_StillNeedsAnAcceptableSignatureStatus(PackageSignatureStatus status)
    {
        var outcome = await Build(Signed(status, Pin), pin: Pin).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.SignatureInvalid, outcome.Reason);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task UntrustedRoot_WithoutAConfiguredPin_IsRefusedEvenForTheTrustedSubject()
    {
        var outcome = await Build(Signed(PackageSignatureStatus.UntrustedRoot, Pin)).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.SignatureInvalid, outcome.Reason);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task PinIsNormalized_ColonsSpacesAndCase()
    {
        var spaced = string.Join(':', Enumerable.Range(0, 32).Select(_ => "AA"));

        var outcome = await Build(Signed(PackageSignatureStatus.UntrustedRoot, Pin), pin: spaced)
            .RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
    }

    [Theory]
    [InlineData("da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [InlineData("not a thumbprint")]
    public async Task MalformedPin_IsRefusedBeforeDownloading(string pin)
    {
        var outcome = await Build(pin: pin).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.PublisherNotTrusted, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Preflight, outcome.Stage);
        Assert.False(Directory.Exists(Path.Combine(_root, "staging")));
    }

    [Fact]
    public async Task CancelDuringDownload_IsReportedAtTheDownloadStage()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FaultingHandler(new OperationCanceledException(cts.Token));
        cts.Cancel();

        var outcome = await Build(handler: handler).RunAsync(Available(Package()), null, cts.Token);

        Assert.Equal(UpdateFailureReason.Cancelled, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Download, outcome.Stage);
    }

    [Fact]
    public async Task HttpClientTimeout_IsATypedDownloadFailure_NotACancel()
    {
        var timeout = new TaskCanceledException("timed out", new TimeoutException());

        var outcome = await Build(handler: new FaultingHandler(timeout)).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.DownloadFailed, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Download, outcome.Stage);
    }

    [Fact]
    public async Task CancelWhileQuiescing_IsReportedAtTheQuiesceStage_AndResumes()
    {
        using var cts = new CancellationTokenSource();

        var outcome = await Build(isSaleInProgress: () =>
        {
            cts.Cancel();
            return false;
        }).RunAsync(Available(Package()), null, cts.Token);

        Assert.Equal(UpdateFailureReason.Cancelled, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Quiesce, outcome.Stage);
        Assert.DoesNotContain("backup", _calls);
        Assert.Equal("resume", _calls[^1]);
    }

    [Fact]
    public async Task CancelDuringInstall_IsReportedAtTheInstallStage_AndLeavesNoPendingMarker()
    {
        using var cts = new CancellationTokenSource();

        var outcome = await Build(onInstall: () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }).RunAsync(Available(Package()), null, cts.Token);

        Assert.Equal(UpdateFailureReason.Cancelled, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Install, outcome.Stage);
        Assert.Null(new PendingUpgradeStore(Path.Combine(_root, "pending.json")).Read());
        Assert.Equal("resume", _calls[^1]);
    }

    [Fact]
    public async Task AnUnexpectedErrorIsTypedAtTheStageThatWasRunning()
    {
        var outcome = await Build(onInspect: () => throw new InvalidOperationException("boom"))
            .RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UpdateFailureReason.UnexpectedError, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Verify, outcome.Stage);
        Assert.Contains("boom", outcome.Message);
        Assert.Empty(_calls);
    }

    public static TheoryData<Exception> BackupFailures => new()
    {
        new IOException("disk full"),
        new UnauthorizedAccessException("denied"),
        new SqliteException("database is locked", 5)
    };

    [Theory]
    [MemberData(nameof(BackupFailures))]
    public async Task PlainBackupIoOrSqliteFailures_AreTypedBackupFailed_AndNothingIsInstalled(Exception failure)
    {
        var outcome = await Build(backupFailure: failure).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.BackupFailed, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Backup, outcome.Stage);
        Assert.DoesNotContain("install", _calls);
        Assert.Equal("resume", _calls[^1]);
    }

    [Fact]
    public async Task PendingMarkerIoFailure_IsTyped_AndNothingIsInstalled()
    {
        var blocker = Path.Combine(_root, "blocker");
        await File.WriteAllTextAsync(blocker, "a file where a directory is needed");

        var outcome = await Build(pendingPath: Path.Combine(blocker, "pending.json")).RunAsync(Available(Package()), null, CancellationToken.None);

        Assert.Equal(UpdateFailureReason.PendingMarkerFailed, outcome.Reason);
        Assert.Equal(UpdateInstallStage.Install, outcome.Stage);
        Assert.DoesNotContain("install", _calls);
        Assert.Equal("resume", _calls[^1]);
    }

    private sealed class SyncProgress(Action<UpdateProgress> onReport) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => onReport(value);
    }
}
