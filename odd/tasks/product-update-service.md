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

- [ ] Add POS version metadata
  - Evidence: pending.
  - Checks: pending.

- [ ] Add release manifest model and parser
  - Evidence: pending.
  - Checks: pending.

- [ ] Add local-file manifest source for VM testing
  - Evidence: pending.
  - Checks: pending.

- [ ] Wire footer/settings update status
  - Evidence: pending.
  - Checks: pending.

- [ ] Add update compatibility tests
  - Evidence: pending.
  - Checks: pending.

## References

- Issue: https://github.com/Incoders-Tools/incoders-commerce/issues/69
- `docs/pos-product-updates.md`
- `docs/architecture/decisions/ADR-004-release-signing-and-upgrades.md`
- `docs/architecture/decisions/ADR-005-signing-and-windows-fleet.md`
