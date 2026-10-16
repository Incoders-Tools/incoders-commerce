# Delta for Safe Release Upgrades

## ADDED Requirements

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

## MODIFIED Requirements

### Requirement: Signed and Compatible Release Artifacts

An upgrade MUST use an immutable, verifiably signed release artifact with explicit application, synchronization-contract, and schema compatibility metadata. An installation MUST reject an unsigned, tampered, incompatible, or unauthorized artifact before changing operational state.

(Previously: no change to this requirement's text; retained unchanged as part of the full block per MODIFIED-workflow requirements. Only the publication gate reference below changes.)

#### Scenario: Compatible authorized upgrade

- GIVEN an authorized user initiates a signed artifact compatible with the installation
- WHEN preflight validation succeeds during a safe operational window
- THEN the upgrade may proceed and records artifact identity and authorization

#### Scenario: Unsafe artifact rejection

- GIVEN an artifact is altered, unsigned, incompatible, or not authorized
- WHEN the client validates it
- THEN it rejects the artifact, leaves the current version operational, and records the reason

### Requirement: Safe Timing and Replacement Separation

A client-initiated upgrade MUST NOT interrupt an active sale and MUST distinguish application upgrade from coordinated notebook replacement. Replacement MUST require separate reconciliation, backup, bootstrap, validation, and prior-identity revocation controls. Release publication (for both client packaging and cloud deploy channels) MUST be gated by a per-channel `publication_authorized` flag in the long-lived `.github/release-authorization.yml` repo policy file, not by any reference to an archived SDD change's `state.yaml`.

(Previously: gate referenced `openspec/changes/commerce-foundation/state.yaml -> release_publication_authorized`, which is stale because commerce-foundation is archived. Corrected to a dedicated `.github/release-authorization.yml` rather than `openspec/config.yaml`, because SDD change state and tooling config are not the right home for permanent, per-channel release policy, and the cloud deploy channel needs its own per-channel flag alongside the existing client packaging channels.)

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
