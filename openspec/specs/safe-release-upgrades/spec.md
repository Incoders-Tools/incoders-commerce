# Safe Release Upgrades Specification

## Purpose

Define a recoverable application release path for Windows x64 branch installations without selecting a packaging provider or version. The walking skeleton covers the primary organization's two branches; a second organization remains a security fixture. Application upgrade is separate from coordinated notebook replacement.

## Requirements

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

A client-initiated upgrade MUST NOT interrupt an active sale and MUST distinguish application upgrade from coordinated notebook replacement. Replacement MUST require separate reconciliation, backup, bootstrap, validation, and prior-identity revocation controls.

#### Scenario: Active sale protection

- GIVEN a sale is in progress
- WHEN an upgrade is requested
- THEN the request waits for a safe window and the sale remains uninterrupted
