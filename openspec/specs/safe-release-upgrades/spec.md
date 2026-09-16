# Safe Release Upgrades Specification

## Purpose

Define a recoverable application release path for Windows x64 branch installations without selecting a packaging provider or version. The walking skeleton covers the primary organization's two branches; a second organization remains a security fixture. Application upgrade is separate from coordinated notebook replacement. Cloud deployment is independently released on per-environment authorization.

## Requirements

### Requirement: Per-Channel Live Cloud Deploy Step

The release pipeline MUST include a live deploy step for the cloud-facing channel mapping (dev→internal, staging→pilot, main→stable) that builds and pushes the Linux container for Commerce.Cloud.Api and Commerce.Web and triggers the corresponding Railway environment deploy. This step is separate from, and MUST NOT be gated by, the Windows client MSIX/MSI publication gate.

#### Scenario: Staging channel deploy on push

- GIVEN a commit is pushed to the branch mapped to the `staging` channel
- WHEN the release pipeline runs
- THEN it builds the container image, pushes it, and Railway deploys Commerce.Cloud.Api and Commerce.Web to the staging environment

#### Scenario: Cloud deploy step failure does not corrupt client artifacts

- GIVEN the cloud deploy step fails
- WHEN the pipeline reports its result
- THEN the Windows client build/test/packaging stages are unaffected and reported independently

### Requirement: Signed and Compatible Release Artifacts

An upgrade MUST use an immutable, verifiably signed release artifact with explicit application, synchronization-contract, and schema compatibility metadata. An installation MUST reject an unsigned, tampered, incompatible, or unauthorized artifact before changing operational state.

#### Scenario: Compatible authorized upgrade

- GIVEN an authorized user initiates a signed artifact compatible with the installation
- WHEN preflight validation succeeds during a safe operational window
- THEN the upgrade may proceed and records artifact identity and authorization

#### Scenario: Unsafe artifact rejection

- GIVEN an artifact is altered, unsigned, incompatible, or not authorized
- WHEN the client validates it
- THEN it rejects the artifact, leaves the current version operational, and records the reason

### Requirement: Backup, Migration, and Committed-Work Preservation

Before changing local state, the upgrade MUST create a verifiable backup and MUST apply a reversible, compatibility-checked migration plan. It MUST NOT lose committed local sales, cash, stock, orders, audit entries, or unacknowledged synchronization work.

#### Scenario: N to N+1 migration

- GIVEN a compatible release and committed local work exist
- WHEN the upgrade runs
- THEN backup verification, migration outcome, and preserved work are recorded before the new version is declared healthy

#### Scenario: Backup or migration failure

- GIVEN backup verification or migration fails
- WHEN the failure is detected
- THEN the upgrade stops or restores the prior state and the prior version remains usable

### Requirement: Health, Interruption Recovery, and Rollback

The process MUST perform a post-upgrade health check covering operational data, synchronization, identity, and required local capabilities. An interrupted or unhealthy upgrade MUST support recovery to a known usable state, and rollback MUST be auditable.

#### Scenario: Interrupted upgrade

- GIVEN an upgrade is interrupted after local state changes begin
- WHEN the installation restarts
- THEN it either completes safely or recovers the prior known-good state without lost committed work

#### Scenario: Unhealthy release rollback

- GIVEN the new version fails its health check
- WHEN recovery is authorized or automatically required by policy
- THEN the installation returns to a usable compatible state, preserves evidence, and marks the release unavailable for that installation

### Requirement: Safe Timing and Replacement Separation

A client-initiated upgrade MUST NOT interrupt an active sale and MUST distinguish application upgrade from coordinated notebook replacement. Replacement MUST require separate reconciliation, backup, bootstrap, validation, and prior-identity revocation controls. Release publication (for both client packaging and cloud deploy channels) MUST be gated by a per-channel `publication_authorized` flag in the long-lived `.github/release-authorization.yml` repo policy file, not by any reference to an archived SDD change's `state.yaml`.

#### Scenario: Active sale protection

- GIVEN a sale is in progress
- WHEN an upgrade is requested
- THEN the request waits for a safe window and the sale remains uninterrupted

#### Scenario: Publication gate references a live authorization source

- GIVEN the release pipeline reaches its publication gate step
- WHEN it reports the gate status
- THEN it reads the channel's `publication_authorized` flag from `.github/release-authorization.yml` and does not reference any archived change's `state.yaml`

#### Scenario: Cloud deploy channel respects the same gate

- GIVEN the cloud deploy channel's `publication_authorized` flag in `.github/release-authorization.yml` is `false`
- WHEN the pipeline would otherwise deploy to a production cloud channel
- THEN the deploy step is blocked or clearly marked as unauthorized, consistent with the client packaging gate
