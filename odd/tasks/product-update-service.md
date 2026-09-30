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
- [ ] R4. HTTP manifest source for GitHub Releases per channel, local-file
      override kept, timeouts, offline-safe, tests.
- [ ] R5. POS install wizard: footer indicator opens it; details,
      download to a staging dir, verify SHA256 + Authenticode publisher,
      quiesce (no sale in progress), back up `branch.db`, install through the
      Windows package deployment API, restart, typed failures; tests for the
      pure steps.
- [ ] R6. Installed VM validation per `docs/pos-product-updates.md` (owner
      or parent with an authorized VM; not runnable in CI).
Route: R1..R3 one writer, then R4..R5 (delegated direct).

### Progress (R1..R3, delegated direct, one writer)

- R1 commit 6e6a22f `docs(release): ...`: ADR-013 (interim self-signed signing), `docs/launch/go-live-requirements.md`, `deploy/pos-terminal-certificate.md`, open decisions updated in `docs/pos-product-updates.md`.
- R2 commit 1e584c3 `feat(release): add MSIX packaging ...`: `deploy/release/{new-dev-signing-cert,build-pos-msix}.ps1`, `AppxManifest.template.xml`; `.gitignore` covers `*.pfx *.p12 *.cer *.msix artifacts/`.
- R3 commit 45d2914 `feat(release): add tag-triggered POS release workflow ...`: `.github/workflows/pos-release.yml`, `deploy/release/new-release-manifest.ps1`, `tests/Commerce.Upgrade/ReleaseManifestGeneratorTests.cs`.
- Checks observed: PowerShell parse of both packaging scripts = 0 errors; throwaway cert + real `build-pos-msix.ps1 -Version 0.2.0-internal.1` produced and signed an MSIX with SDK 10.0.26100 tools, `Get-AuthenticodeSignature` shows the signer `CN=Incoders Commerce (Interim)` with status UnknownError (untrusted root, expected because the cert was not installed); TDD RED (3 tests failing, script missing) then GREEN, `dotnet test tests/Commerce.Upgrade` 34/34; `dotnet build Commerce.sln` 0 errors in a throwaway worktree (the main tree is locked by the running POS); workflow YAML parsed with PyYAML.
- Not observed: `new-release-manifest.ps1` was only exercised through the tests; the workflow itself has not run (needs push, tag and secrets); the MSIX was never installed.
- Decision: the workflow honors `.github/release-authorization.yml` (ADR-004 publication gate); all channels are `false`, so the owner must flip the flag.
- Finding: a publisher change (interim to commercial certificate) is a new MSIX package family, so the swap needs a one-time reinstall per terminal (documented in ADR-013).
- Risk for R6: MSIX file virtualization may redirect `%LocalAppData%` writes (`branch.db`, `update-manifest.json`).

### Owner actions

1. Create the PFX: `$env:POS_SIGNING_PFX_PASSWORD='...'; pwsh deploy/release/new-dev-signing-cert.ps1 -OutputDir <secure dir>`; keep the PFX private.
2. Add repository secrets `POS_SIGNING_PFX_BASE64` (base64 of the PFX) and `POS_SIGNING_PFX_PASSWORD`.
3. Set `publication_authorized: true` for the channel in `.github/release-authorization.yml`.
4. Push a first tag (for example `v0.2.0-internal.1`) or run the `POS Release` workflow manually.
5. Install the `.cer` on each terminal per `deploy/pos-terminal-certificate.md`.

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

