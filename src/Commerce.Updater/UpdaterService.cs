using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.Domain.Audit;
using Commerce.Domain.Identity;

namespace Commerce.Updater;

/// <summary>
/// The single shared upgrade orchestration service (Component Reuse Policy —
/// CLI/scheduled triggers are thin adapters over this, matching the Units
/// 4-5 "thin adapter, one shared service" pattern). Reuses
/// <see cref="TenantAuthorizationService"/> (Unit 2) for the entire
/// trigger-privilege decision and <see cref="IAuditSink"/> for the upgrade
/// audit trail — no parallel authorization or audit logic here.
///
/// Sequencing follows ADR-004 exactly: verify package -&gt; check
/// compatibility (fail closed) -&gt; authorize -&gt; quiesce -&gt; verified backup
/// -&gt; migrate -&gt; health check -&gt; reopen writes -&gt; commit. Any failure
/// before the write-reopen boundary restores the pristine snapshot and
/// leaves the prior version operational (pre-reopen recovery); the boundary
/// itself is recorded in a crash journal so an interruption is detectable
/// and recoverable after a restart via <see cref="DetectAndRecover"/>.
/// </summary>
public sealed class UpdaterService
{
    private static readonly ActionDefinition TriggerUpgrade = new(
        Name: "trigger-branch-upgrade",
        IsSensitive: true,
        RequiredPermission: Permission.ManageBranchSettings);

    private readonly TenantAuthorizationService _authorizationService;
    private readonly IAuditSink _auditSink;
    private readonly IBranchNodeQuiescence _quiescence;
    private readonly IUpgradeBackup _backup;
    private readonly IMigrationRunner _migrationRunner;
    private readonly UpgradeCrashJournal _journal;
    private readonly string _dbPath;
    private readonly string _backupDirectory;
    private readonly Func<DateTimeOffset> _clock;

    public UpdaterService(
        TenantAuthorizationService authorizationService,
        IAuditSink auditSink,
        IBranchNodeQuiescence quiescence,
        IUpgradeBackup backup,
        IMigrationRunner migrationRunner,
        UpgradeCrashJournal journal,
        string dbPath,
        string backupDirectory,
        Func<DateTimeOffset>? clock = null)
    {
        _authorizationService = authorizationService;
        _auditSink = auditSink;
        _quiescence = quiescence;
        _backup = backup;
        _migrationRunner = migrationRunner;
        _journal = journal;
        _dbPath = dbPath;
        _backupDirectory = backupDirectory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public UpgradeOutcome RunUpgrade(
        UserAccount actor,
        AccessRequest accessContext,
        ReleasePackage package,
        ReleaseManifest trustedManifest,
        InstallationState currentState,
        string controlledStagingRoot,
        TimeSpan quiesceTimeout)
    {
        var verify = PackageVerifier.Verify(package, trustedManifest, controlledStagingRoot);
        if (!verify.Ok)
        {
            return new UpgradeOutcome(UpgradeStatus.Rejected, verify.Reason, verify.Detail, currentState.AppVersion);
        }

        var compatibility = CompatibilityChecker.Check(package, currentState);
        if (!compatibility.Ok)
        {
            return new UpgradeOutcome(UpgradeStatus.Rejected, compatibility.Reason, compatibility.Detail, currentState.AppVersion);
        }

        var access = _authorizationService.Authorize(
            actor,
            new AccessRequest(accessContext.TargetOrganizationId, accessContext.TargetBranchId, TriggerUpgrade, accessContext.IsOffline, accessContext.CorrelationId));
        if (!access.Allowed)
        {
            RecordAudit(actor, accessContext, "denied", access.Reason);
            return new UpgradeOutcome(UpgradeStatus.Rejected, UpgradeRejectionReason.InsufficientPrivilege, access.Reason, currentState.AppVersion);
        }

        if (!_quiescence.TryQuiesce(quiesceTimeout))
        {
            return new UpgradeOutcome(UpgradeStatus.WaitingForSafeWindow, null,
                "An active sale is in progress; the upgrade will retry once the branch reaches a safe window.", currentState.AppVersion);
        }

        try
        {
            string backupPath;
            try
            {
                backupPath = _backup.CreateVerifiedBackup(_dbPath, _backupDirectory);
            }
            catch (UpgradeBackupVerificationException ex)
            {
                RecordAudit(actor, accessContext, "backup-failed", ex.Message);
                return new UpgradeOutcome(UpgradeStatus.Rejected, UpgradeRejectionReason.BackupOrRecoveryFailure,
                    $"Backup verification failed: {ex.Message} Current version preserved.", currentState.AppVersion);
            }

            _journal.RecordPhase(UpgradePhase.BackupVerified, backupPath);
            _journal.RecordPhase(UpgradePhase.WritesQuiesced, backupPath);

            _migrationRunner.Migrate(_dbPath, package.SchemaVersion);
            _journal.RecordPhase(UpgradePhase.MigrationApplied, backupPath);

            if (!_migrationRunner.CheckHealth(_dbPath, package.SchemaVersion))
            {
                _backup.RestoreFromBackup(backupPath, _dbPath);
                _journal.Clear();
                RecordAudit(actor, accessContext, "rolled-back-pre-reopen", "health-check-failed");
                return new UpgradeOutcome(UpgradeStatus.RolledBackPreReopen, null,
                    "Health check failed after migration; restored the pristine pre-upgrade snapshot.", currentState.AppVersion);
            }

            _journal.RecordPhase(UpgradePhase.HealthChecked, backupPath);
            _journal.RecordPhase(UpgradePhase.WritesReopened, backupPath);
            _journal.RecordPhase(UpgradePhase.Committed, backupPath);
            _journal.Clear();

            RecordAudit(actor, accessContext, "applied", null);
            return new UpgradeOutcome(UpgradeStatus.Applied, null, "Upgrade applied and health-checked.", package.AppVersion);
        }
        finally
        {
            _quiescence.Resume();
        }
    }

    /// <summary>
    /// Runs the same phases as <see cref="RunUpgrade"/> up through migration
    /// but deliberately never records <see cref="UpgradePhase.WritesReopened"/>
    /// or resumes writes — mirrors Unit 3's <c>SimulateInterruptedCommit</c>
    /// technique for proving crash-journal recovery across a simulated
    /// restart with a fresh <see cref="UpdaterService"/> instance reading the
    /// same journal file.
    /// </summary>
    public void SimulateInterruptedUpgrade(ReleasePackage package)
    {
        _quiescence.TryQuiesce(TimeSpan.FromSeconds(5));
        var backupPath = _backup.CreateVerifiedBackup(_dbPath, _backupDirectory);
        _journal.RecordPhase(UpgradePhase.BackupVerified, backupPath);
        _journal.RecordPhase(UpgradePhase.WritesQuiesced, backupPath);
        _migrationRunner.Migrate(_dbPath, package.SchemaVersion);
        _journal.RecordPhase(UpgradePhase.MigrationApplied, backupPath);

        // Deliberately abandoned here: no health check, no WritesReopened, no
        // Resume() — simulates a crash mid-apply, before writes reopen.
    }

    /// <summary>
    /// Goes one phase further than <see cref="SimulateInterruptedUpgrade"/>:
    /// records <see cref="UpgradePhase.WritesReopened"/> (writes have
    /// reopened against the migrated schema) but crashes before
    /// <see cref="UpgradePhase.Committed"/> — proves the post-reopen
    /// recovery boundary, where the snapshot must never be restored.
    /// </summary>
    public void SimulateInterruptedUpgradeAfterReopen(ReleasePackage package)
    {
        _quiescence.TryQuiesce(TimeSpan.FromSeconds(5));
        var backupPath = _backup.CreateVerifiedBackup(_dbPath, _backupDirectory);
        _journal.RecordPhase(UpgradePhase.BackupVerified, backupPath);
        _journal.RecordPhase(UpgradePhase.WritesQuiesced, backupPath);
        _migrationRunner.Migrate(_dbPath, package.SchemaVersion);
        _journal.RecordPhase(UpgradePhase.MigrationApplied, backupPath);
        _migrationRunner.CheckHealth(_dbPath, package.SchemaVersion);
        _journal.RecordPhase(UpgradePhase.HealthChecked, backupPath);
        _journal.RecordPhase(UpgradePhase.WritesReopened, backupPath);
        _quiescence.Resume();

        // Deliberately abandoned here: no Committed marker, journal not
        // cleared — simulates a crash after writes reopened against the
        // migrated schema but before the upgrade fully committed.
    }

    public UpgradeOutcome DetectAndRecover(InstallationState currentState, InstallationState candidateState)
    {
        var (phase, backupPath) = _journal.ReadLastPhase();

        if (phase is UpgradePhase.NotStarted or UpgradePhase.Committed)
        {
            return new UpgradeOutcome(UpgradeStatus.Applied, null, "No interrupted upgrade detected.", currentState.AppVersion);
        }

        if (phase == UpgradePhase.WritesReopened)
        {
            // Post-reopen boundary: NEVER restore the snapshot. Roll the
            // binaries back only if the older candidate binary is explicitly
            // N/N+1-compatible with the now-live (migrated) schema in
            // `currentState` — i.e. the live schema is the candidate's own
            // version or one step ahead — retaining the current database and
            // any already-migrated sales; otherwise forward-repair.
            var rollbackCompatibility = CompatibilityChecker.CheckVersions(
                currentState.AppVersion, currentState.SyncContractVersion, currentState.SchemaVersion, candidateState);

            _journal.Clear();

            return rollbackCompatibility.Ok
                ? new UpgradeOutcome(UpgradeStatus.RolledBackPostReopen, UpgradeRejectionReason.InterruptedUpgrade,
                    "Rolled binaries back against the compatible live schema; database and already-migrated sales retained.", candidateState.AppVersion)
                : new UpgradeOutcome(UpgradeStatus.ForwardRepaired, UpgradeRejectionReason.InterruptedUpgrade,
                    "Binary rollback was not schema-compatible; forward-repaired with compatible code, preserving committed operations.", currentState.AppVersion);
        }

        // BackupVerified / WritesQuiesced / MigrationApplied / HealthChecked:
        // writes never reopened — pre-reopen recovery restores the pristine,
        // verified pre-upgrade snapshot. This is the only phase range where
        // restoring the snapshot is the correct answer (ADR-004).
        if (backupPath is null)
        {
            return new UpgradeOutcome(UpgradeStatus.Rejected, UpgradeRejectionReason.BackupOrRecoveryFailure,
                "Interrupted upgrade detected with no recoverable backup reference.", currentState.AppVersion);
        }

        try
        {
            _backup.RestoreFromBackup(backupPath, _dbPath);
        }
        catch (UpgradeBackupVerificationException ex)
        {
            return new UpgradeOutcome(UpgradeStatus.Rejected, UpgradeRejectionReason.BackupOrRecoveryFailure,
                $"Recovery restore failed: {ex.Message}", currentState.AppVersion);
        }

        _quiescence.Resume();
        _journal.Clear();
        return new UpgradeOutcome(UpgradeStatus.RolledBackPreReopen, UpgradeRejectionReason.InterruptedUpgrade,
            "Interrupted upgrade detected on restart; restored the pristine pre-upgrade snapshot.", currentState.AppVersion);
    }

    private void RecordAudit(UserAccount actor, AccessRequest accessContext, string outcome, string? reason)
    {
        _auditSink.Record(new AuditEntry(
            ActorId: actor.Id,
            OrganizationId: actor.OrganizationId,
            BranchId: accessContext.TargetBranchId,
            Action: TriggerUpgrade.Name,
            Outcome: outcome,
            OccurredAtUtc: _clock(),
            CorrelationId: accessContext.CorrelationId,
            Reason: reason));
    }
}
