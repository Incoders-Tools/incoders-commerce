# Tenant Access Foundation Specification

## Purpose

Define organization, branch, identity, authorization, audit, and offline-access boundaries for the walking skeleton. The functional acceptance fixture contains one organization with two branches and a second organization used only to prove isolation.

## Requirements

### Requirement: Organization and Branch Isolation

Every business record, action, and authorization decision MUST carry an organization context and MUST enforce the user's permitted branch scope. A request from one organization or unauthorized branch MUST NOT disclose or mutate data in another scope.

#### Scenario: Authorized branch access

- GIVEN a user is authorized for Organization A, Branch 1
- WHEN the user reads or changes an allowed record in Branch 1
- THEN the action succeeds and retains Organization A and Branch 1 scope

#### Scenario: Cross-branch and cross-organization denial

- GIVEN a user is authorized only for Organization A, Branch 1
- WHEN the user requests Branch 2 data or any Organization B data
- THEN the request is denied without revealing protected record existence

### Requirement: Role and Revocation Enforcement

Administrative actions MUST be authorized by configurable roles and permissions consistently through local and web management contracts. A revoked, suspended, or disabled user or customer credential MUST be denied when the enforcing node has the applicable revocation state; remote revocation timing while offline MUST follow an approved offline-identity ADR and MUST NOT be implied as immediate.

#### Scenario: Revoked access

- GIVEN an installation has received a credential revocation
- WHEN that credential attempts a protected action
- THEN authorization fails and the denial is auditable

#### Scenario: Offline revocation boundary

- GIVEN a branch cannot obtain newer remote revocation information
- WHEN a credential is presented offline
- THEN the node follows the approved offline policy, labels freshness, and makes no claim that an unseen remote revocation was applied immediately

### Requirement: Installation Identity and Audit

Each branch installation MUST have a distinct identity. Sensitive authentication, authorization, scope, revocation, and synchronization decisions MUST be auditable with actor or credential, organization, branch, time, action, outcome, and correlation information.

#### Scenario: Auditable sensitive action

- GIVEN an authorized user performs a sensitive management action
- WHEN the action completes or is denied
- THEN an immutable audit entry records the required context and outcome

#### Scenario: Installation replacement identity

- GIVEN a replacement installation is prepared
- WHEN it is enabled
- THEN it uses a new installation identity and the prior identity remains revocable and traceable
