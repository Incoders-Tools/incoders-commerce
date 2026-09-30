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

    private sealed class FakeSignatures(PackageSignature signature) : IPackageSignatureVerifier
    {
        public PackageSignature Inspect(string packagePath) => signature;
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

    private sealed class RecordingBackup(List<string> calls, IUpgradeBackup inner, bool fail = false) : IUpgradeBackup
    {
        public string CreateVerifiedBackup(string sourceDbPath, string backupDirectory)
        {
            calls.Add("backup");
            if (fail)
            {
                throw new UpgradeBackupVerificationException("disk full");
            }

            return inner.CreateVerifiedBackup(sourceDbPath, backupDirectory);
        }

        public void RestoreFromBackup(string backupPath, string targetDbPath) => inner.RestoreFromBackup(backupPath, targetDbPath);
    }

    private sealed class FakeInstaller(List<string> calls, bool succeed = true) : IUpdateInstaller
    {
        public Task<UpdateInstallerResult> InstallAsync(string packagePath, CancellationToken cancellationToken)
        {
            calls.Add("install");
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
        bool backupFails = false)
    {
        var options = new UpdateInstallOptions(
            StagingDirectory: Path.Combine(_root, "staging"),
            DatabasePath: DbPath,
            BackupDirectory: Path.Combine(_root, "backups"),
            TrustedPublisher: Trusted,
            QuiesceTimeout: TimeSpan.FromMilliseconds(50));
        return new UpdateInstallWorkflow(
            new PackageDownloader(new HttpClient(new PayloadHandler())),
            new FakeSignatures(signature ?? new PackageSignature(PackageSignatureStatus.Valid, Trusted)),
            new RecordingBackup(_calls, new SqliteUpgradeBackup(), backupFails),
            new FakeQuiescence(_calls, canQuiesce),
            new FakePackaged(packaged),
            new FakeRestart(_calls),
            new FakeInstaller(_calls, installSucceeds),
            new PendingUpgradeStore(Path.Combine(_root, "pending.json")),
            () => saleInProgress,
            options);
    }

    [Fact]
    public async Task HappyPath_RunsStagesInOrder_AndBacksUpBeforeInstall()
    {
        var stages = new List<UpdateInstallStage>();

        var outcome = await Build().RunAsync(Available(Package()), new SyncProgress(p => stages.Add(p.Stage)), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["quiesce", "backup", "restart-register", "install", "resume"], _calls);
        Assert.NotNull(outcome.BackupPath);
        Assert.True(File.Exists(outcome.BackupPath));
        Assert.Equal(
            [UpdateInstallStage.Download, UpdateInstallStage.Verify, UpdateInstallStage.Quiesce, UpdateInstallStage.Backup, UpdateInstallStage.Install],
            stages.Distinct().Where(s => s != UpdateInstallStage.Preflight).ToList());
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
        var outcome = await Build(backupFails: true).RunAsync(Available(Package()), null, CancellationToken.None);

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

    private sealed class SyncProgress(Action<UpdateProgress> onReport) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => onReport(value);
    }
}
