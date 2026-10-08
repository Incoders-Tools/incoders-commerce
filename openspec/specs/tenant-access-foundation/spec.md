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
request-body or payload fields submitted by the caller. This principle
applies equally to the ordering path: `CustomerOrderingAccess` evaluation
MUST resolve the credential and enabled state from the persisted access
store and MUST NOT accept a caller-supplied enabled flag from the request
body. Role names used in any authorization decision MUST resolve only to
entries in the server-owned role catalog; a persisted or presented role name
outside the catalog MUST NOT grant any permission.

(Previously: actor identity fields such as roles and branch scope could be
supplied directly in the request body, e.g. `Catalog.cs`'s
`RenameProductRequest.ActorId/ActorBranchScope/ActorRoles`, with no
verification against a persisted store. The ordering path was a known
violation of this same principle: `CustomerCatalogAccessService.Evaluate`
read a self-asserted `AccessEnabled` boolean from the request body with no
persisted store consulted anywhere in that path.)

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

#### Scenario: Ordering access is resolved from the persisted store, not the request

- GIVEN a customer ordering access credential's enabled state exists only in
  the persisted access store
- WHEN an order submission includes a self-asserted enabled flag for that
  credential
- THEN authorization is evaluated using the persisted store's state and the
  request-supplied flag is ignored

#### Scenario: Role name outside the catalog grants nothing

- GIVEN a persisted role entry carries a name that is not in the server-owned
  role catalog
- WHEN authorization for a protected action evaluates that role
- THEN no permission is granted from that entry

### Requirement: Platform-Admin Scheme Isolation

A platform-admin identity MUST be authenticated through a distinct
credential/session scheme (`PlatformAdminAuth`), separate from the
org-scoped cookie scheme, and MUST carry no `organization_id` on the
identity itself. An org-scoped caller authenticated under the org-scoped
scheme MUST NOT be authorized against any platform-admin-only endpoint, and
a platform-admin authenticated under `PlatformAdminAuth` MUST NOT be
authorized against any org-scoped, `TenantScopeResolver`-gated endpoint.

#### Scenario: Org-scoped caller cannot reach a platform-admin endpoint

- GIVEN a `business-admin` is authenticated under the org-scoped cookie
  scheme
- WHEN that session calls a platform-admin-only endpoint
- THEN the request is rejected without authorizing the action

#### Scenario: Platform-admin cannot reach an org-scoped endpoint

- GIVEN a platform-admin is authenticated under `PlatformAdminAuth`
- WHEN that session calls an org-scoped, `TenantScopeResolver`-gated endpoint
- THEN the request is rejected without authorizing the action

### Requirement: Audit Logging for User-Management Actions

Every user-management action in scope for this change — user creation, role
assignment or change, and any platform-admin action — MUST write an
append-only audit record in the same transaction as the mutating action,
capturing actor id, actor kind (org user vs. platform admin), acted-on
entity type and id, organization id, action, timestamp, and old/new value
where applicable. No mutating action MUST succeed while its audit record
fails to persist, and no audit record MUST exist for an action that did not
occur.

#### Scenario: Audit row is written atomically with the action

- GIVEN a `business-admin` assigns a new role to a user
- WHEN the role-assignment transaction commits
- THEN an audit row exists in that same transaction recording the actor,
  the acted-on user, the organization, the action, the timestamp, and the
  old and new role values

#### Scenario: Failed audit write rolls back the action

- GIVEN a user-management action is being persisted
- WHEN the audit-record write fails
- THEN the mutating action is rolled back and does not take effect

#### Scenario: No orphaned audit row without a corresponding action

- GIVEN a user-management transaction is rolled back for any reason
- WHEN the transaction outcome is inspected
- THEN no audit row exists for that attempted action

### Requirement: Anonymous Request Organization Resolution

A request that arrives with no authenticated principal (a guest order)
MUST still resolve to exactly one organization and branch before being
processed; this resolution MUST NOT depend on any authenticated
principal's claims. For the current deployment this resolution MUST
target the single configured organization (Vaca Verde) and its
principal/default branch. The resolution mechanism MUST be isolated behind
a single responsibility so a future per-request resolution (e.g.
multi-organization) can replace the fixed default without changing the
domain or endpoint shape it feeds.

#### Scenario: Anonymous guest request resolves to the fixed default organization and branch

- GIVEN a guest request carries no authenticated principal
- WHEN the request is processed
- THEN it resolves to the single configured organization and its
  principal/default branch

#### Scenario: Guest order carries organization/branch context despite no principal

- GIVEN a guest order was admitted from an anonymous request
- WHEN the order is read back
- THEN it carries the same organization and branch context required of
  every business record under "Organization and Branch Isolation"

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

### Requirement: Selected Branch Scopes Every Branch-Owned Staff Request

Every staff request to an endpoint that reads or writes branch-owned data
MUST carry a selected branch, supplied per request in the `X-Branch-Id`
header (never persisted server-side against the caller's identity, never
taken from a request body or route). The system MUST honor the selected
branch only when it is a persisted branch of the request's scoped
organization AND the caller may act on it: it is contained in the caller's
freshly loaded `BranchScope`, or the caller is a verified system
administrator acting on a selected organization (platform-administration,
"Sysadmin Selects Any Branch Of The Selected Organization").

A branch-owned request with no selected branch MUST be rejected with a
distinct "branch selection required" outcome and MUST NOT fall back to any
branch. A selected branch that is malformed, unknown, belongs to another
organization, or lies outside the caller's `BranchScope` MUST be rejected
without revealing whether that branch exists. Organization-level endpoints
(organization, branch management, identity, session) MUST NOT require a
selected branch.

A device-authenticated request MUST take its branch from its server-issued
device credential; an `X-Branch-Id` header on a device request MUST be
ignored.

#### Scenario: Vaca Verde staff works only on Ruta 51

- GIVEN organization "Vaca Verde" has branches "Ruta 51" and "Centro", and
  a `business-admin` of Vaca Verde whose `BranchScope` contains only
  "Ruta 51"
- WHEN they list products, price lists, customers, orders, and payments
  with "Ruta 51" selected
- THEN only rows owned by "Ruta 51" are returned, and every row they create
  is owned by "Ruta 51"

#### Scenario: Selecting a branch outside the caller's scope is denied

- GIVEN the same Vaca Verde `business-admin` whose `BranchScope` contains
  only "Ruta 51"
- WHEN they select "Centro", or a branch of another organization
- THEN the request is denied, no data is read or written, and the response
  does not distinguish an out-of-scope branch from a nonexistent one

#### Scenario: Branch-owned request without a selected branch is rejected

- GIVEN an authenticated staff member with a non-empty `BranchScope`
- WHEN they call a branch-owned endpoint without a selected branch
- THEN the request is rejected as "branch selection required" and no
  branch is chosen on their behalf

#### Scenario: A device request ignores a supplied branch header

- GIVEN a POS paired to "Ruta 51"
- WHEN its device-authenticated sync request carries `X-Branch-Id` naming
  "Centro"
- THEN the request is scoped to "Ruta 51" exactly as if no header had been
  supplied
