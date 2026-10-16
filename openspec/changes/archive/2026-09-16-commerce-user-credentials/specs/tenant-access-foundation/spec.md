# Delta for Tenant Access Foundation

## MODIFIED Requirements

### Requirement: Role and Revocation Enforcement

Administrative actions MUST be authorized by configurable roles and
permissions consistently through local and web management contracts. A
revoked, suspended, or disabled user or customer credential MUST be denied
when the enforcing node has the applicable revocation state; remote
revocation timing while offline MUST follow an approved offline-identity ADR
and MUST NOT be implied as immediate. Actor identity — user id, roles, and
branch scope — used in any authorization decision MUST be derived from the
authenticated principal via the persisted user-credentials store, never from
request-body or payload fields submitted by the caller.

(Previously: actor identity fields such as roles and branch scope could be
supplied directly in the request body, e.g. `Catalog.cs`'s
`RenameProductRequest.ActorId/ActorBranchScope/ActorRoles`, with no
verification against a persisted store.)

#### Scenario: Revoked access

- GIVEN an installation has received a credential revocation
- WHEN that credential attempts a protected action
- THEN authorization fails and the denial is auditable

#### Scenario: Offline revocation boundary

- GIVEN a branch cannot obtain newer remote revocation information
- WHEN a credential is presented offline
- THEN the node follows the approved offline policy, labels freshness, and
  makes no claim that an unseen remote revocation was applied immediately

#### Scenario: Actor identity is loaded from the persisted store, not the request body

- GIVEN an authenticated user is signed in with a persisted role and branch
  scope
- WHEN that user submits a management request (e.g. a catalog rename) with a
  body claiming a different actor id, roles, or branch scope
- THEN the system authorizes and executes the action using the actor id,
  roles, and branch scope loaded from the persisted store, and the
  body-supplied identity fields are ignored

#### Scenario: Privilege escalation via request body is denied

- GIVEN an authenticated user has no elevated role in the persisted store
- WHEN that user submits a request body claiming an elevated role or a wider
  branch scope than persisted
- THEN authorization is evaluated against the persisted role and branch
  scope, and the action is denied if the persisted grant does not permit it
