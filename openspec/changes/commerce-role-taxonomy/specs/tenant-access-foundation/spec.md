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
request-body or payload fields submitted by the caller. Role names used in
any authorization decision MUST resolve only to entries in the server-owned
role catalog; a persisted or presented role name outside the catalog MUST
NOT grant any permission.

(Previously: actor identity fields such as roles and branch scope could be
supplied directly in the request body, e.g. `Catalog.cs`'s
`RenameProductRequest.ActorId/ActorBranchScope/ActorRoles`, with no
verification against a persisted store, and role names were not constrained
to any canonical catalog.)

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

#### Scenario: Role name outside the catalog grants nothing

- GIVEN a persisted role entry carries a name that is not in the server-owned
  role catalog
- WHEN authorization for a protected action evaluates that role
- THEN no permission is granted from that entry

## ADDED Requirements

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
