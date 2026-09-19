# Delta for User Credentials

## MODIFIED Requirements

### Requirement: Per-Organization First-Admin Bootstrap

The system MUST expose a bootstrap endpoint that creates the first admin user
for a specific organization, gated by a one-time bootstrap token scoped to
that organization only. The system MUST reject bootstrap when the target
organization already has at least one persisted user, and MUST invalidate the
token after one successful use. The system MUST also require a human-friendly
`organizationName` and accept an optional `branchName` on the bootstrap
request. The system MUST create the organization, exactly one branch, and the
first admin user in a single transaction, and MUST seed the created admin's
branch scope with the id of that created branch rather than an empty list.

(Previously: bootstrap created only the admin user with `branch_scope = []`,
because organizations and branches were not persisted; the admin could sign
in but never pass branch-scoped authorization checks.)

#### Scenario: Bootstrap creates the first admin

- GIVEN an organization has zero persisted users and a valid bootstrap token
  scoped to that organization
- WHEN the bootstrap endpoint is called with that token, an organization
  name, and admin credentials
- THEN an organization row, one branch row, and an admin user are created,
  the admin's branch scope contains the created branch's id, and the token
  becomes invalid

#### Scenario: Bootstrap rejected when an admin already exists

- GIVEN an organization already has at least one persisted user
- WHEN the bootstrap endpoint is called with a token scoped to that
  organization
- THEN the request is rejected and no user is created

#### Scenario: Bootstrap rejected when the organization is already persisted

- GIVEN a persisted organization already exists for the requested
  organization id
- WHEN the bootstrap endpoint is called with a token scoped to that
  organization id
- THEN the request is rejected and no organization, branch, or user row is
  created

#### Scenario: Bootstrap token cannot be reused

- GIVEN a bootstrap token was already used to create an organization's first
  admin
- WHEN the same token is submitted again
- THEN the request is rejected and no user is created

#### Scenario: Bootstrap token is scoped to one organization

- GIVEN a bootstrap token was issued for Organization A
- WHEN it is submitted to bootstrap Organization B
- THEN the request is rejected and no user is created

#### Scenario: Bootstrap branch defaults to "Main" when unnamed

- GIVEN a bootstrap request omits `branchName`
- WHEN bootstrap completes successfully
- THEN the created branch's name is "Main" and the admin's branch scope
  contains that branch's id
