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

## References

- Issue: https://github.com/Incoders-Tools/incoders-commerce/issues/69
- `docs/pos-product-updates.md`
- `docs/architecture/decisions/ADR-004-release-signing-and-upgrades.md`
- `docs/architecture/decisions/ADR-005-signing-and-windows-fleet.md`
