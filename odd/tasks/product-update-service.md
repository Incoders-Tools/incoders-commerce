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
- [ ] R1. Docs: ADR for interim self-signed signing (amending ADR-004/005
      expectations), a go-live requirements checklist (commercial cert, MSI
      fallback decision, VM validation), and a terminal certificate install
      runbook; update `docs/pos-product-updates.md` open decisions.
- [ ] R2. Packaging: script that publishes the POS self-contained, builds an
      MSIX (Windows SDK MakeAppx), signs it (SignTool) with a PFX, and a dev
      script to generate the self-signed certificate. Version from the tag.
- [ ] R3. Release workflow: tag-triggered job on `windows-latest` that runs
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

## References

- Issue: https://github.com/Incoders-Tools/incoders-commerce/issues/69
- `docs/pos-product-updates.md`
- `docs/architecture/decisions/ADR-004-release-signing-and-upgrades.md`
- `docs/architecture/decisions/ADR-005-signing-and-windows-fleet.md`
