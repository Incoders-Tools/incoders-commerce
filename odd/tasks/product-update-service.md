# Product update service

## Goal

Define and implement the installed-terminal update path for Windows POS terminals: release policy, compatibility detection, footer status, and eventually a safe download/install wizard.

## Scope

- Release/update policy for issue #69.
- POS local version and update availability detection.
- Compatibility checks for Windows build, architecture, package format, sync/schema contracts, and trust metadata.
- Installed VM validation path.

## Non-goals for first slice

- Silent updates.
- Installing from raw branch commits.
- Applying an update before manifest/signature/hash verification exists.
- Blocking sales when update checks fail.

## Tasks

- [x] Draft release/update policy
  - Evidence: Added `docs/pos-product-updates.md` covering source of truth, manifest shape, compatibility outcomes, UI policy, wizard stages, rollout policy, installed VM validation, and first implementation slice.
  - Checks: Documentation readback; no code build required for docs-only change.
  - Commit: pending.

- [x] Add POS version metadata
  - Evidence: Added `Version`, `AssemblyVersion`, `FileVersion`, and `InformationalVersion` metadata to `src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj`; `MainWindow` reads `AssemblyInformationalVersionAttribute` for footer display.
  - Checks: `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` succeeded with existing NU1903 warnings and 0 errors.

- [x] Add release manifest model and parser
  - Evidence: Added `src/Commerce.Updater/ReleaseDiscovery.cs` with local release manifest records, typed update outcomes, manifest schema validation, version comparison, and compact Spanish status formatting.
  - Checks: `dotnet test tests/Commerce.Upgrade/Commerce.Upgrade.csproj --no-build` passed 27/27 after build.

- [x] Add local-file manifest source for VM testing
  - Evidence: Added `LocalUpdateManifestSource`, defaulting from POS DI to `%LocalAppData%/Incoders/Commerce/update-manifest.json`, overrideable with `Commerce:UpdateManifestPath`.
  - Checks: Missing manifest returns `ManifestNotConfigured` instead of claiming the service is absent.

- [x] Wire footer/settings update status
  - Evidence: `MainWindow` now displays `Versión <local> · <update status>` in the footer/settings version provider using `ReleaseDiscovery.CheckForUpdates(...)` at startup.
  - Checks: Desktop build succeeded.

- [x] Add update compatibility tests
  - Evidence: Added `tests/Commerce.Upgrade/UpdateDiscoveryTests.cs` for missing manifest, up-to-date, available update, incompatible Windows, incompatible architecture, invalid manifest, unsupported schema, and no compatible package.
  - Checks: `dotnet test tests/Commerce.Upgrade/Commerce.Upgrade.csproj --no-build` passed 27/27.

## Phase 2 — Releases and install (authorized 2026-09-30)

Owner decisions (2026-09-30):
- Start managing product releases in the repository (GitHub Releases; the
  repository is public, so terminals download release assets without
  credentials).
- Signing: interim self-signed code-signing certificate (option B). The
  certificate is installed once per terminal (admin). A commercial/trusted
  certificate (e.g. Azure Trusted Signing) is a documented GO-LIVE
  REQUIREMENT; swapping it must not require code changes.

Parent defaults (revisit if the owner objects):
- Update checks call GitHub Releases directly (no first-party mirror API
  yet); availability is global per product channel (`stable` from `main`,
  `internal` prereleases from `dev`), not per organization/branch.
- A release is cut by pushing a `v<semver>` tag (or a manual dispatch);
  the tag is the version source. This is a new release-only workflow; the
  owner's "CI only on PRs to main" policy for gates is unchanged.
- MSIX only in this cut; the ADR-005 signed-MSI fallback stays a documented
  follow-up (and a go-live item only if a terminal below the MSIX floor
  appears).
- The operator starts an update from the footer indicator; never silent;
  refused while a sale is being built; offline/failed checks never block
  sales.

Tasks:
- [x] R1. Docs: ADR for interim self-signed signing (amending ADR-004/005
      expectations), a go-live requirements checklist (commercial cert, MSI
      fallback decision, VM validation), and a terminal certificate install
      runbook; update `docs/pos-product-updates.md` open decisions.
- [x] R2. Packaging: script that publishes the POS self-contained, builds an
      MSIX (Windows SDK MakeAppx), signs it (SignTool) with a PFX, and a dev
      script to generate the self-signed certificate. Version from the tag.
- [x] R3. Release workflow: tag-triggered job on `windows-latest` that runs
      R2 with the PFX from a repository secret, computes SHA256, generates
      `commerce-pos-release-manifest.json` matching the existing manifest
      schema, and creates the GitHub Release with the assets (prerelease for
      the internal channel). Adding the secret and pushing tags are owner
      actions.
- [x] R4. HTTP manifest source for GitHub Releases per channel, local-file
      override kept, timeouts, offline-safe, tests.
- [x] R5. POS install wizard: footer indicator opens it; details,
      download to a staging dir, verify SHA256 + Authenticode publisher,
      quiesce (no sale in progress), back up `branch.db`, install through the
      Windows package deployment API, restart, typed failures; tests for the
      pure steps.
- [ ] R6. Installed VM validation per `docs/pos-product-updates.md` (owner
      or parent with an authorized VM; not runnable in CI).
Route: R1..R3 one writer, then R4..R5 one writer (delegated direct; work units in the Progress entries).

### Progress (R1..R3, delegated direct, one writer)

- R1 commit 6e6a22f `docs(release): ...`: ADR-013 (interim self-signed signing), `docs/launch/go-live-requirements.md`, `deploy/pos-terminal-certificate.md`, open decisions updated in `docs/pos-product-updates.md`.
- R2 commit 1e584c3 `feat(release): add MSIX packaging ...`: `deploy/release/{new-dev-signing-cert,build-pos-msix}.ps1`, `AppxManifest.template.xml`; `.gitignore` covers `*.pfx *.p12 *.cer *.msix artifacts/`.
- R3 commit 45d2914 `feat(release): add tag-triggered POS release workflow ...`: `.github/workflows/pos-release.yml`, `deploy/release/new-release-manifest.ps1`, `tests/Commerce.Upgrade/ReleaseManifestGeneratorTests.cs`.
- Checks observed: PowerShell parse of both packaging scripts = 0 errors; throwaway cert + real `build-pos-msix.ps1 -Version 0.2.0-internal.1` produced and signed an MSIX with SDK 10.0.26100 tools, `Get-AuthenticodeSignature` shows the signer `CN=Incoders Commerce (Interim)` with status UnknownError (untrusted root, expected because the cert was not installed); TDD RED (3 tests failing, script missing) then GREEN, `dotnet test tests/Commerce.Upgrade` 34/34; `dotnet build Commerce.sln` 0 errors in a throwaway worktree (the main tree is locked by the running POS); workflow YAML parsed with PyYAML.
- Not observed: `new-release-manifest.ps1` was only exercised through the tests; the workflow itself has not run (needs push, tag and secrets); the MSIX was never installed.
- Decision: the workflow honors `.github/release-authorization.yml` (ADR-004 publication gate); all channels are `false`, so the owner must flip the flag.
- Finding: a publisher change (interim to commercial certificate) is a new MSIX package family, so the swap needs a one-time reinstall per terminal (documented in ADR-013).
- Risk for R6: MSIX file virtualization may redirect `%LocalAppData%` writes (`branch.db`, `update-manifest.json`).

### Progress (R4..R5, delegated direct, one writer)

- R4 commit 1b02eef `feat(updater): check GitHub Releases per channel for POS updates`: `GitHubReleaseManifestSource` (public REST API, User-Agent, no auth, 10 s budget, newest by mapped version, `stable` = non-prerelease, `internal` = prereleases too), `LocalFileManifestSource` (override via `Commerce:UpdateManifestPath`), `UpdateChecker` (never throws; failure = `CheckFailedInvalid`, no release/asset = `ManifestNotConfigured`), `ReleaseDiscovery.Evaluate`, `Checking` status; the POS runs the check off the UI thread and refreshes the footer.
- R5 services commit da4fcec `feat(updater): add testable POS install stages for package updates`: `PackageDownloader`, `UpdateInstallWorkflow` (preflight, download, SHA256 + `WinVerifyTrust` signature + configured publisher via the existing `PackageVerifier`, sale-in-progress refusal, `IBranchNodeQuiescence`, unpackaged refusal, `SqliteUpgradeBackup`, restart registration, `PackageManager.AddPackageAsync` with `ForceApplicationShutdown`), `PendingUpgradeStore` (next-start report), `WindowsPackageSignatureVerifier`; POS TFM moved to `net10.0-windows10.0.19041.0` (Integration tests too); MSIX manifest template declares `packageManagement`.
- R5 wizard commit aff6180 `feat(pos): add the update install wizard and footer entry point`: link-styled footer button, themed `UpdateWizardWindow`, Settings "Buscar actualizaciones", startup report of a pending upgrade.
- Decisions: the crash journal phases (database migration) and `UpgradeOutcome` do not fit a package swap, so the pending install is a separate `pending-upgrade.json` marker; only MSIX is installable (MSI and packages that require attestation are refused); `Valid` is the only accepted signature status; HTTP range resume is not implemented (retry restarts, staged file reused when its hash matches); the default staging dir is `<LocalAppData>/Incoders/Commerce/updates`.
- Checks observed: see the final report of the writer in the parent thread (commands and counts): `dotnet build Commerce.sln` 0 errors; `dotnet test tests/Commerce.Upgrade` 84/84 (34 before); focused (Pos composition + wizard text, 39/39) and full `dotnet test tests/Commerce.Integration` 1051/1051 in a throwaway worktree; TDD RED = compile failure on missing types before each implementation, then GREEN; `WinVerifyTrust` P/Invoke smoke on a real MSIX built with a throwaway PFX (no certificate installed): `UntrustedRoot` with signer `CN=Incoders Commerce (Interim)` (matches `Get-AuthenticodeSignature`), `Invalid` (0x80096010) after flipping one byte; one unauthenticated GET to the GitHub releases endpoint returned HTTP 200 `[]`.
- Not observed: a real install (`AddPackageAsync`), restart registration and the next-start report on an installed terminal; `Valid` with the certificate in `LocalMachine\TrustedPeople`; anything against a published release.

### Owner actions

1. Create the PFX: `$env:POS_SIGNING_PFX_PASSWORD='...'; pwsh deploy/release/new-dev-signing-cert.ps1 -OutputDir <secure dir>`; keep the PFX private.
2. Add repository secrets `POS_SIGNING_PFX_BASE64` (base64 of the PFX) and `POS_SIGNING_PFX_PASSWORD`.
3. Set `publication_authorized: true` for the channel in `.github/release-authorization.yml`.
4. Push a first tag (for example `v0.2.0-internal.1`) or run the `POS Release` workflow manually.
5. Install the `.cer` on each terminal per `deploy/pos-terminal-certificate.md`.
6. Terminals that should follow prereleases set `Commerce:UpdateChannel=internal` (environment variable `Commerce__UpdateChannel`); the default is `stable`. `Commerce:UpdateTrustedPublisher` only changes when the commercial certificate replaces the interim one.
7. Cutting a first release is required before any terminal can see an update (the repository has no releases yet; the check reports "Manifest de updates no configurado").

### R6 installed-VM checklist (added by R4..R5)

- Install the baseline MSIX (interim `.cer` in `LocalMachine\TrustedPeople`), pair, sell once, then publish a newer release.
- The wizard verification returns `Valid` for the interim certificate in `TrustedPeople` only (not yet observed); otherwise the wizard refuses with "Verificación fallida".
- MSIX file virtualization: the staged package under `%LocalAppData%\Incoders\Commerce\updates` must be readable by the deployment service (`AddPackageAsync`), and `branch.db`, `upgrade-backups` and `pending-upgrade.json` must land where the next start reads them. If not, set `Commerce:UpdateStagingDirectory` outside AppData.
- `AddPackageAsync` with `ForceApplicationShutdown` from a standard (non-admin) user: does it succeed, and does the POS relaunch through `RegisterApplicationRestart`? Confirm the footer reports "Actualización a la versión X completada" and `branch.db` (pending operations, cash session) survived.
- The `packageManagement` restricted capability is accepted for a package signed by the interim certificate.
- A sale in progress refuses the wizard; a tampered package, wrong publisher and offline check are refused or reported without blocking sales.
- Publisher change (interim to commercial certificate) still needs the one-time reinstall (ADR-013).

## References

- Issue: https://github.com/Incoders-Tools/incoders-commerce/issues/69
- `docs/pos-product-updates.md`
- `docs/architecture/decisions/ADR-004-release-signing-and-upgrades.md`
- `docs/architecture/decisions/ADR-005-signing-and-windows-fleet.md`

- 2026-09-30 (parent): range `c1cf3be..b5c2381` (includes the POS C5 footer
  commit and R1..R3) assessed HIGH (963 lines, 16 files: update path,
  process-starting tests, workflow shell); owner DECLINED the review for
  this candidate. Parent spot check in a throwaway worktree: `dotnet test
  tests/Commerce.Upgrade` 34/34. Surfaced to the owner: switching to the
  commercial certificate changes the MSIX publisher (new package family),
  so every terminal installed with the interim certificate needs a one-time
  backup/uninstall/reinstall/restore (ADR-013).

- 2026-09-30 (parent): R4..R5 range `ace4138..7a41168` assessed HIGH (2727
  lines, 32 files). Owner GRANTED the review: four lenses, lineage
  `review-506238d7f4ade4e5`, APPROVED with no correction and acknowledged
  (authority burned). Parent spot check: `dotnet test tests/Commerce.Upgrade`
  84/84. Advisory (non-blocking) findings, open as follow-ups, not yet
  accepted as scope:
  - WARNING `R1-staging-clean-deletes-all-files` / `R3-staging-cleanup-deletes-foreign-files`:
    `PackageDownloader.CleanStaging` deletes every file in a configurable
    staging dir except the target.
  - WARNING `R4-cancel-misclassification` / `R2-cancel-stage` /
    `R3-http-timeout-reported-as-cancel`: every `OperationCanceledException`
    is reported as an operator cancel at the Download stage, including
    HttpClient timeouts (default 100 s) and cancels in later stages.
  - WARNING `R3-cancel-during-install-leaks-marker`: cancelling during install
    leaves the pending-upgrade marker.
  - WARNING `R3-untyped-backup-and-marker-io-failures`: plain IO/SQLite
    failures in backup/marker escape as a generic InstallFailed.
  - WARNING `R2-wizard-unexpected-error`: the wizard marks earlier stages Done
    on any unexpected exception.
  - WARNING `R2-placeholder-verifier`: `PackageVerifier.Verify` is fed
    hardcoded placeholder inputs.
  - SUGGESTION `R1-publisher-subject-only-pin` (pin thumbprint/key instead of
    subject text), `R2-legacy-local-check`, `R3-happy-path-progress-race`,
    `R3-manual-check-noop-while-inflight` / `R4-manual-check-dropped`.
  Parent concern (unverified, R6): a self-signed certificate only in
  `LocalMachine\TrustedPeople` likely makes WinVerifyTrust return
  `UntrustedRoot`, so the wizard would refuse every interim-signed update;
  fix by also trusting it as a root during the interim period, or by
  pinning the certificate thumbprint and accepting `UntrustedRoot` only for
  that exact signer.

