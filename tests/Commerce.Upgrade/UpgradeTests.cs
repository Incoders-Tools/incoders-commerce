using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.Identity;
using Commerce.Domain.Sync;
using Commerce.Updater;
using Microsoft.Data.Sqlite;

namespace Commerce.Upgrade;

/// <summary>
/// Covers `openspec/changes/commerce-foundation/specs/safe-release-upgrades/spec.md`
/// and ADR-004/ADR-005: each of the seven typed rejections (path traversal,
/// publisher/package-type mismatch, tamper/unsigned, incompatible version,
/// insufficient privilege, interrupted upgrade, backup/recovery failure),
/// both recovery boundaries (pre-reopen snapshot restore, post-reopen binary
/// rollback/forward-repair), active-sale timing protection, and the
/// fail-closed MSIX/MSI packaging selection.
///
/// Migration/backup logic runs against a real branch SQLite file — the exact
/// same file <see cref="BranchSyncStore"/> owns — so "preserve committed
/// local work" is proven end-to-end, not against an imagined schema.
/// </summary>
public sealed class UpgradeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"upgrade-{Guid.NewGuid():N}.db");
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"upgrade-journal-{Guid.NewGuid():N}.txt");
    private readonly string _backupDirectory;
    private readonly string _stagingRoot;

    public UpgradeTests()
    {
        _backupDirectory = Path.Combine(Path.GetTempPath(), $"upgrade-backups-{Guid.NewGuid():N}");
        _stagingRoot = Path.Combine(Path.GetTempPath(), $"upgrade-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _journalPath })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the OS temp directory is periodically reclaimed.
                }
            }
        }

        foreach (var dir in new[] { _backupDirectory, _stagingRoot })
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    // --- Fixtures ------------------------------------------------------

    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();

    private static UserAccount NewActor(Permission permissions) => new(
        Guid.NewGuid(),
        OrganizationId,
        new[] { BranchId },
        new[] { new Role("branch-operator", permissions) });

    private static AccessRequest NewAccessContext() => new(
        OrganizationId,
        BranchId,
        new ActionDefinition("trigger-branch-upgrade", IsSensitive: true, RequiredPermission: Permission.ManageBranchSettings),
        IsOffline: false,
        Guid.NewGuid());

    private ReleasePackage NewValidPackage(string relativePath = "release.msi") => new(
        PackagePath: relativePath,
        PublisherId: "trusted-publisher",
        Format: PackageFormat.Msi,
        ContentHash: "sha256:good-hash",
        SignatureValid: true,
        IsAttested: true,
        AppVersion: 2,
        SyncContractVersion: 2,
        SchemaVersion: 2);

    private static ReleaseManifest TrustedManifest() => new(
        PublisherId: "trusted-publisher",
        Format: PackageFormat.Msi,
        ContentHash: "sha256:good-hash");

    private static InstallationState CurrentState() => new(AppVersion: 1, SyncContractVersion: 1, SchemaVersion: 1);

    private void SeedDatabaseFile()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var envelope = new SyncEnvelope(
            OperationId: Guid.NewGuid(),
            ContractVersion: 1,
            OrganizationId: OrganizationId,
            BranchId: BranchId,
            AggregateId: Guid.NewGuid(),
            AggregateVersion: 1,
            ActorId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            PayloadKind: "sale",
            Payload: "{}");
        var effect = new SaleEffect(Guid.NewGuid(), BranchId, 100m, DateTimeOffset.UtcNow);
        store.CommitSaleAtomically(envelope, effect);
    }

    private UpdaterService NewUpdater(
        IBranchNodeQuiescence? quiescence = null,
        IUpgradeBackup? backup = null,
        IMigrationRunner? migrationRunner = null,
        IAuditSink? auditSink = null,
        Permission actorPermissions = Permission.ManageBranchSettings) => new(
        new TenantAuthorizationService(auditSink ?? new InMemoryAuditSink()),
        auditSink ?? new InMemoryAuditSink(),
        quiescence ?? new InProcessBranchNodeQuiescence(),
        backup ?? new SqliteUpgradeBackup(),
        migrationRunner ?? new SqliteMigrationRunner(),
        new UpgradeCrashJournal(_journalPath),
        _dbPath,
        _backupDirectory);

    // --- Compatible authorized upgrade (spec scenario) ------------------

    [Fact]
    public void CompatibleAuthorizedUpgrade_Succeeds_AppliesMigration_AndHealthChecks()
    {
        SeedDatabaseFile();
        var updater = NewUpdater();

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), NewValidPackage(), TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeStatus.Applied, outcome.Status);
        Assert.Null(outcome.RejectionReason);
        Assert.Equal(2, outcome.PreservedOrCurrentVersion);
    }

    // --- Typed rejection: path traversal --------------------------------

    [Fact]
    public void RejectsPathTraversalPackage()
    {
        var updater = NewUpdater();
        var package = NewValidPackage(relativePath: Path.Combine("..", "..", "evil.msi"));

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), package, TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeStatus.Rejected, outcome.Status);
        Assert.Equal(UpgradeRejectionReason.PathTraversal, outcome.RejectionReason);
        Assert.Equal(1, outcome.PreservedOrCurrentVersion);
    }

    // --- Typed rejection: publisher / package type ----------------------

    [Fact]
    public void RejectsWrongPublisher()
    {
        var updater = NewUpdater();
        var package = NewValidPackage() with { PublisherId = "attacker-publisher" };

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), package, TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeRejectionReason.PublisherOrPackageTypeMismatch, outcome.RejectionReason);
    }

    [Fact]
    public void RejectsWrongPackageType()
    {
        var updater = NewUpdater();
        var package = NewValidPackage() with { Format = PackageFormat.Msix };

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), package, TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeRejectionReason.PublisherOrPackageTypeMismatch, outcome.RejectionReason);
    }

    // --- Typed rejection: tamper / unsigned -----------------------------

    [Fact]
    public void RejectsTamperedPackage_HashMismatch()
    {
        var updater = NewUpdater();
        var package = NewValidPackage() with { ContentHash = "sha256:tampered-hash" };

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), package, TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeRejectionReason.TamperedOrUnsignedPackage, outcome.RejectionReason);
    }

    [Fact]
    public void RejectsUnsignedPackage()
    {
        var updater = NewUpdater();
        var package = NewValidPackage() with { SignatureValid = false };

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), package, TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeRejectionReason.TamperedOrUnsignedPackage, outcome.RejectionReason);
    }

    // --- Typed rejection: incompatibility (fail closed) -----------------

    [Fact]
    public void RejectsIncompatibleVersion_WhenSchemaJumpsMoreThanOne()
    {
        var updater = NewUpdater();
        var package = NewValidPackage() with { SchemaVersion = 5 };

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), package, TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeRejectionReason.IncompatibleVersion, outcome.RejectionReason);
    }

    // --- Typed rejection: insufficient privilege ------------------------

    [Fact]
    public void RejectsInsufficientPrivilege_ReusesTenantAuthorizationService()
    {
        var updater = NewUpdater();

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ViewSales), NewAccessContext(), NewValidPackage(), TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeStatus.Rejected, outcome.Status);
        Assert.Equal(UpgradeRejectionReason.InsufficientPrivilege, outcome.RejectionReason);
    }

    // --- Safe timing: active sale protection ----------------------------

    [Fact]
    public void ActiveSale_BlocksUpgrade_WaitsForSafeWindow_SaleRemainsUninterrupted()
    {
        SeedDatabaseFile();
        var quiescence = new InProcessBranchNodeQuiescence();
        var migrationRunner = new RecordingMigrationRunner(new SqliteMigrationRunner());
        quiescence.BeginSale();
        var updater = NewUpdater(quiescence: quiescence, migrationRunner: migrationRunner);

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), NewValidPackage(), TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromMilliseconds(50));

        Assert.Equal(UpgradeStatus.WaitingForSafeWindow, outcome.Status);
        Assert.False(quiescence.IsQuiesced);
        Assert.Equal(0, migrationRunner.MigrateCallCount);

        quiescence.EndSale();
        var retried = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), NewValidPackage(), TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeStatus.Applied, retried.Status);
    }

    // --- Typed rejection: backup / recovery failure ---------------------

    [Fact]
    public void BackupVerificationFailure_PreservesCurrentVersion_NoMigrationAttempted()
    {
        SeedDatabaseFile();
        var migrationRunner = new RecordingMigrationRunner(new SqliteMigrationRunner());
        var updater = NewUpdater(backup: new ThrowingCreateBackup(), migrationRunner: migrationRunner);

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), NewValidPackage(), TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeStatus.Rejected, outcome.Status);
        Assert.Equal(UpgradeRejectionReason.BackupOrRecoveryFailure, outcome.RejectionReason);
        Assert.Equal(1, outcome.PreservedOrCurrentVersion);
        Assert.Equal(0, migrationRunner.MigrateCallCount);
    }

    [Fact]
    public void RecoveryRestoreFailure_ReportsBackupOrRecoveryFailure()
    {
        SeedDatabaseFile();
        var flakyBackup = new FlakyRestoreBackup(new SqliteUpgradeBackup());
        var updater = NewUpdater(backup: flakyBackup);
        var package = NewValidPackage();
        updater.SimulateInterruptedUpgrade(package);

        var candidate = new InstallationState(package.AppVersion, package.SyncContractVersion, package.SchemaVersion);
        var recovered = updater.DetectAndRecover(CurrentState(), candidate);

        Assert.Equal(UpgradeRejectionReason.BackupOrRecoveryFailure, recovered.RejectionReason);
    }

    // --- Health check failure -> pre-reopen rollback within one run -----

    [Fact]
    public void HealthCheckFailure_RollsBackPreReopen_RestoresSnapshot()
    {
        SeedDatabaseFile();
        var updater = NewUpdater(migrationRunner: new UnhealthyMigrationRunner(new SqliteMigrationRunner()));

        var outcome = updater.RunUpgrade(
            NewActor(Permission.ManageBranchSettings), NewAccessContext(), NewValidPackage(), TrustedManifest(),
            CurrentState(), _stagingRoot, TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeStatus.RolledBackPreReopen, outcome.Status);
        Assert.Equal(1, outcome.PreservedOrCurrentVersion);

        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        Assert.Single(store.GetPendingOutbox(BranchId));
    }

    // --- Interruption: pre-reopen recovery on restart -------------------

    [Fact]
    public void InterruptedUpgrade_PreReopen_RestoresPristineSnapshot_OnRestart()
    {
        SeedDatabaseFile();
        var package = NewValidPackage();
        var updater = NewUpdater();
        updater.SimulateInterruptedUpgrade(package);

        // Simulated restart: a fresh UpdaterService reading the same journal.
        var restarted = NewUpdater();
        var candidate = new InstallationState(package.AppVersion, package.SyncContractVersion, package.SchemaVersion);
        var outcome = restarted.DetectAndRecover(CurrentState(), candidate);

        Assert.Equal(UpgradeStatus.RolledBackPreReopen, outcome.Status);
        Assert.Equal(UpgradeRejectionReason.InterruptedUpgrade, outcome.RejectionReason);
        Assert.Equal(1, outcome.PreservedOrCurrentVersion);
    }

    [Fact]
    public void NoInterruption_DetectAndRecover_ReportsAppliedWithNoRejection()
    {
        var updater = NewUpdater();

        var outcome = updater.DetectAndRecover(CurrentState(), CurrentState());

        Assert.Equal(UpgradeStatus.Applied, outcome.Status);
        Assert.Null(outcome.RejectionReason);
    }

    // --- Interruption: post-reopen recovery boundary ---------------------

    [Fact]
    public void InterruptedUpgrade_PostReopen_RollsBackBinaries_PreservesMigratedSales()
    {
        SeedDatabaseFile();
        var package = NewValidPackage();
        var updater = NewUpdater();
        updater.SimulateInterruptedUpgradeAfterReopen(package);

        var restarted = NewUpdater();
        // Rollback candidate is still N/N+1-compatible with the now-live
        // (migrated) schema, so this must roll binaries back rather than
        // ever touching the pre-upgrade snapshot.
        var liveSchemaState = new InstallationState(package.AppVersion, package.SyncContractVersion, package.SchemaVersion);
        var rollbackCandidate = new InstallationState(package.AppVersion - 1, package.SyncContractVersion, package.SchemaVersion - 1);
        var outcome = restarted.DetectAndRecover(liveSchemaState, rollbackCandidate);

        Assert.Equal(UpgradeStatus.RolledBackPostReopen, outcome.Status);
        Assert.Equal(UpgradeRejectionReason.InterruptedUpgrade, outcome.RejectionReason);

        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        Assert.Single(store.GetPendingOutbox(BranchId));
    }

    [Fact]
    public void InterruptedUpgrade_PostReopen_ForwardRepairs_WhenRollbackIncompatible_PreservesMigratedSales()
    {
        SeedDatabaseFile();
        var package = NewValidPackage();
        var updater = NewUpdater();
        updater.SimulateInterruptedUpgradeAfterReopen(package);

        var restarted = NewUpdater();
        var liveSchemaState = new InstallationState(package.AppVersion, package.SyncContractVersion, package.SchemaVersion);
        // Far incompatible candidate: rollback is not an option, must forward-repair.
        var incompatibleRollbackCandidate = new InstallationState(package.AppVersion - 5, package.SyncContractVersion - 5, package.SchemaVersion - 5);
        var outcome = restarted.DetectAndRecover(liveSchemaState, incompatibleRollbackCandidate);

        Assert.Equal(UpgradeStatus.ForwardRepaired, outcome.Status);
        Assert.Equal(UpgradeRejectionReason.InterruptedUpgrade, outcome.RejectionReason);

        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        Assert.Single(store.GetPendingOutbox(BranchId));
    }

    // --- ADR-005: fail-closed MSIX/MSI packaging selection ---------------

    [Fact]
    public void PackageFormatSelector_SelectsMsix_WhenFleetMeetsWindows10_2004Plus()
    {
        var result = PackageFormatSelector.Select(new WindowsFleetCapability(BuildNumber: 19045, HasAdminInstallRights: true));

        Assert.True(result.Ok);
        Assert.Equal(PackageFormat.Msix, result.Format);
    }

    [Fact]
    public void PackageFormatSelector_FallsBackToMsi_WhenFleetBelowFloor()
    {
        var result = PackageFormatSelector.Select(new WindowsFleetCapability(BuildNumber: 18363, HasAdminInstallRights: true));

        Assert.True(result.Ok);
        Assert.Equal(PackageFormat.Msi, result.Format);
    }

    [Fact]
    public void PackageFormatSelector_RejectsInsufficientPrivilege_WithoutAdminRights()
    {
        var result = PackageFormatSelector.Select(new WindowsFleetCapability(BuildNumber: 19045, HasAdminInstallRights: false));

        Assert.False(result.Ok);
        Assert.Equal(UpgradeRejectionReason.InsufficientPrivilege, result.Reason);
        Assert.Null(result.Format);
    }

    // --- Test doubles ----------------------------------------------------

    private sealed class RecordingMigrationRunner : IMigrationRunner
    {
        private readonly IMigrationRunner _inner;
        public int MigrateCallCount { get; private set; }

        public RecordingMigrationRunner(IMigrationRunner inner) => _inner = inner;

        public void Migrate(string dbPath, int targetSchemaVersion)
        {
            MigrateCallCount++;
            _inner.Migrate(dbPath, targetSchemaVersion);
        }

        public bool CheckHealth(string dbPath, int expectedSchemaVersion) => _inner.CheckHealth(dbPath, expectedSchemaVersion);
    }

    private sealed class UnhealthyMigrationRunner : IMigrationRunner
    {
        private readonly IMigrationRunner _inner;

        public UnhealthyMigrationRunner(IMigrationRunner inner) => _inner = inner;

        public void Migrate(string dbPath, int targetSchemaVersion) => _inner.Migrate(dbPath, targetSchemaVersion);

        public bool CheckHealth(string dbPath, int expectedSchemaVersion) => false;
    }

    private sealed class ThrowingCreateBackup : IUpgradeBackup
    {
        public string CreateVerifiedBackup(string sourceDbPath, string backupDirectory) =>
            throw new UpgradeBackupVerificationException("Simulated backup verification failure.");

        public void RestoreFromBackup(string backupPath, string targetDbPath) =>
            throw new UpgradeBackupVerificationException("Should not be called in this test.");
    }

    private sealed class FlakyRestoreBackup : IUpgradeBackup
    {
        private readonly IUpgradeBackup _inner;

        public FlakyRestoreBackup(IUpgradeBackup inner) => _inner = inner;

        public string CreateVerifiedBackup(string sourceDbPath, string backupDirectory) =>
            _inner.CreateVerifiedBackup(sourceDbPath, backupDirectory);

        public void RestoreFromBackup(string backupPath, string targetDbPath) =>
            throw new UpgradeBackupVerificationException("Simulated recovery restore failure.");
    }
}
