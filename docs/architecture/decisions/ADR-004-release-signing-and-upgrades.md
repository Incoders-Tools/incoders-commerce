# ADR-004: Release signing, provenance, and safe upgrades

## Status

Accepted

## Context

`docs/architecture/decisions/README.md` lists "Installation and updates" as pending: signed installation, compatibility, backups, migration, health checks, and recovery require an operational decision. `openspec/changes/commerce-foundation/design.md` requires that upgrades never corrupt local branch state and that N→N+1 compatibility, backup, migration, health, interruption recovery, and rollback all be provable.

## Decision

- **Provenance mapping**: `dev` branch builds map to the internal channel, `staging` to the pilot channel, and `main` to the stable channel. Only an authorized, protected source/ref may create an immutable tag/release attestation. An arbitrary signed or tagged commit that did not go through this pipeline does not qualify as a releasable artifact.
- **Immutable releases**: releases use GitHub immutable releases, which lock the tag and assets and attest to the commit and assets. No immutable-release configuration exists yet; this ADR authorizes adding it, not publishing under it (release publication remains separately gated per `openspec/changes/commerce-foundation/state.yaml` → `release_publication_authorized`).
- **Client-side verification before applying an upgrade**: the client (`src/Commerce.Updater`) verifies, in order: (1) release attestation, (2) publisher signature and package hash, (3) application/sync/schema compatibility. If any check fails, the client stays on its current, operational version — it never partially applies an update it cannot fully verify.
- **Public asset hygiene**: public release assets exclude secrets and tenant data.
- **Upgrade sequencing**: an upgrade blocks new sales and incoming sync, waits for in-flight work to finish (quiesce), takes a verified SQLite Backup API snapshot, migrates, then performs a health check. A crash journal records the boundary between these phases.
- **Recovery while still quiesced (pre-reopen)**: if the upgrade fails before writes reopen, recovery restores the verified snapshot together with the prior compatible binaries — this is the only phase where restoring the snapshot is the answer.
- **Recovery after writes reopen (post-reopen)**: once writes have reopened against the new schema, the snapshot must never be restored. Recovery instead is either (a) roll the binaries back against an explicitly N/N+1-compatible live schema while retaining the current database, or (b) forward-repair with compatible code while preserving and replaying already-committed durable operations.
- **Updater execution model**: the updater stages downloads in a controlled directory and invokes the OS deployment API with typed arguments. It never shells out to an arbitrary command, and it treats an inconclusive release-endpoint response (e.g., HTTP 404) as inconclusive, not as "no update" or "safe to proceed".
- **Interruption and tampering are typed rejections**: path traversal, wrong publisher/type, tampering, incompatibility, privilege denial, and interruption are each distinct, typed rejection outcomes that the updater must detect and reject, never silently downgrade to a generic failure.

## Consequences

- Tests (`tests/Commerce.Upgrade/UpgradeTests.cs`) must cover each typed rejection independently, plus both the pre-reopen (snapshot restore) and post-reopen (binary rollback/forward-repair) recovery boundaries.
- `.github/workflows/release.yml` must implement the `dev`→internal, `staging`→pilot, `main`→stable mapping and must not allow releases from unprotected refs.
- This ADR does not authorize actual release publication; that remains gated by `release_publication_authorized` in `openspec/changes/commerce-foundation/state.yaml`.
- Any change to the recovery boundary (pre- vs post-reopen) requires updating this ADR, since it is the load-bearing safety invariant for upgrades.
