# Tenant Access Foundation Specification

## Purpose

Define organization, branch, identity, authorization, audit, and offline-access boundaries for the walking skeleton. The functional acceptance fixture contains one organization with two branches and a second organization used only to prove isolation.

## Requirements

### Requirement: Organization and Branch Isolation

Every business record, action, and authorization decision MUST carry an
organization context and MUST enforce the user's permitted branch scope. A
request from one organization or unauthorized branch MUST NOT disclose or
mutate data in another scope. Organizations and branches referenced in
authorization decisions MUST be persisted, RLS-scoped rows rather than
unvalidated ids — a user's branch scope is contained only when it references
a branch that actually exists under that user's organization.

(Previously: "the branch" a user's branch scope referenced had no persisted
existence — `Organization` and `Branch` were domain types with zero
persistence, so branch-containment checks in practice always failed for
bootstrap-created admins whose `branch_scope` was seeded empty. This delta
changes what "the branch" IS, not the containment check itself —
`TenantAuthorizationService.Evaluate` requires no code change.)

#### Scenario: Authorized branch access

- GIVEN a user is authorized for Organization A, Branch 1
- WHEN the user reads or changes an allowed record in Branch 1
- THEN the action succeeds and retains Organization A and Branch 1 scope

#### Scenario: Cross-branch and cross-organization denial

- GIVEN a user is authorized only for Organization A, Branch 1
- WHEN the user requests Branch 2 data or any Organization B data
- THEN the request is denied without revealing protected record existence

#### Scenario: Bootstrapped admin passes branch containment against a real branch

- GIVEN an admin was created via organization bootstrap and its branch scope
  contains the id of the branch created in that same transaction
- WHEN the admin performs catalog management scoped to that branch
- THEN the branch-containment check succeeds because the referenced branch
  is a persisted row under the admin's organization

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

### Requirement: Installation Identity and Audit

Each branch installation MUST have a distinct identity, established only
through server-verified operator sign-in and never self-minted or
caller-asserted by the installation itself. The installation's identity
MUST be backed by a server-issued, independently verifiable device
credential bound to a specific organization, branch, and installation.
Sensitive authentication, authorization, scope, revocation, and
synchronization decisions MUST be auditable with actor or credential,
organization, branch, time, action, outcome, and correlation
information.

#### Scenario: Auditable sensitive action

- GIVEN an authorized user performs a sensitive management action
- WHEN the action completes or is denied
- THEN an immutable audit entry records the required context and outcome

#### Scenario: Installation replacement identity

- GIVEN a replacement installation is prepared
- WHEN it is enabled
- THEN it uses a new installation identity and the prior identity remains
  revocable and traceable

#### Scenario: Device bearer credential must be server-verified, not caller-asserted

- GIVEN a POS installation presents a `Bearer` device credential to
  `Cloud.Api`
- WHEN the credential is not a server-issued credential recognized by
  server-side lookup or signature verification
- THEN authentication fails regardless of whether the presented value is
  a well-formed organization/installation id pair

#### Scenario: Server-verified device credential resolves branch scope

- GIVEN a POS installation presents a device credential that the server
  issued and can verify
- WHEN authentication succeeds
- THEN the resulting principal carries the organization and branch scope
  the server bound to that credential at issuance time
