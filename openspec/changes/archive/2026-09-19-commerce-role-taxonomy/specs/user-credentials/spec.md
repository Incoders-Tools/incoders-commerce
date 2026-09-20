# Delta for User Credentials

## ADDED Requirements

### Requirement: Canonical Role Catalog

The system MUST maintain a server-owned role catalog mapping fixed English
technical identifiers (`business-admin`, `seller`, `provider`,
`platform-admin`) to a `Permission` set. Role assignment MUST reference a
catalog name; the system MUST NOT accept or persist a `Permission` set
supplied directly in a request body, and MUST reject a role name that is not
in the catalog. Catalog keys MUST NOT be accepted or stored in a translated
form.

#### Scenario: Catalog permissions are used, not body-supplied ones

- GIVEN a request to create or update a user's role references `seller`
- WHEN the request body also includes a `permissions` field with a different
  permission set
- THEN the user is assigned exactly the catalog's `seller` permissions
  (`ViewSales` only), and the body-supplied permissions are ignored

#### Scenario: Unknown role name is rejected

- GIVEN a request references a role name not present in the catalog
- WHEN the create-user or role-assignment endpoint is called with that name
- THEN the request is rejected and no user or role change is persisted

#### Scenario: Translated role name is rejected

- GIVEN a request supplies a Spanish label (e.g. "Vendedor") instead of an
  English catalog key
- WHEN the create-user or role-assignment endpoint is called with that value
- THEN the request is rejected as an unknown role name, and it is not
  silently mapped to `seller`

#### Scenario: Reserved provider role grants no capability

- GIVEN a caller with sufficient grant cap assigns the `provider` role to a
  user
- WHEN the assignment succeeds
- THEN the user's effective permissions are `Permission.None` and no
  `provider`-specific endpoint exists to exercise

### Requirement: Staff User Creation and Role Assignment

The system MUST expose `POST /account/users` and
`PUT /account/users/{userId}/roles` on the existing `ManageUsers`-gated
`/account/users` group, both scoped to the acting user's organization. The
target user's branch scope MUST be contained within the caller's
organization. The system MUST enforce a grant cap: the caller MUST NOT
create or promote a user to a permission set that is not a subset of the
caller's own `EffectivePermissions`. The `platform-admin` role MUST NOT be
assignable by an org-scoped caller under any circumstance, including when
the caller's own permissions would otherwise satisfy the grant-cap subset
check.

#### Scenario: Business-admin creates a seller in the same organization

- GIVEN an authenticated `business-admin` in Organization A
- WHEN they call `POST /account/users` with role `seller` and a branch in
  Organization A
- THEN the user is created with exactly the `seller` catalog permissions and
  that branch scope

#### Scenario: Cross-organization branch scope is rejected

- GIVEN an authenticated `business-admin` in Organization A
- WHEN they call `POST /account/users` or the roles endpoint with a branch
  belonging to Organization B
- THEN the request is rejected and no user or role change is persisted

#### Scenario: Grant-cap violation is rejected

- GIVEN an authenticated caller holding `ManageUsers` and `ViewSales` but not
  `RecordSales`
- WHEN they attempt to assign a role whose permission set includes a
  permission they do not themselves hold
- THEN the request is rejected and no role change is persisted

#### Scenario: Org-scoped caller cannot grant platform-admin

- GIVEN an authenticated `business-admin` holds every `Permission` flag in
  Organization A
- WHEN they call the roles endpoint with role `platform-admin` for a user in
  Organization A
- THEN the request is rejected regardless of the caller's own permission set

### Requirement: Business-Admin Rename Migration

The system MUST rewrite every persisted role entry with the name `"admin"`
to `"business-admin"`, preserving the existing permission set unchanged, and
MUST update the bootstrap flow to mint `"business-admin"` instead of
`"admin"` for newly bootstrapped organizations. A user whose role entry was
renamed MUST be able to sign in after the migration with unchanged
permissions.

#### Scenario: Pre-existing admin signs in as business-admin after migration

- GIVEN a user was persisted before this change with a role named `"admin"`
  and a given permission set
- WHEN the migration runs and the user subsequently signs in
- THEN the user's role is named `"business-admin"`, the permission set is
  unchanged, and sign-in succeeds

#### Scenario: New bootstrap mints business-admin

- GIVEN a new organization is bootstrapped after this change
- WHEN the first user is created
- THEN that user's role is named `"business-admin"`, not `"admin"`
