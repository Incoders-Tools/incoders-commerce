# User Credentials Specification

## Purpose

Define Postgres-backed persistence of user accounts and credentials, real
password-based sign-in verification, and one-time bootstrap of the first
admin user per organization, replacing the walking-skeleton stand-in that
trusted client-submitted identity.

## Requirements

### Requirement: Persisted User Account Storage

The system MUST persist each user as a row scoped to exactly one
organization, carrying: a stable user id, an email used as the sign-in
identifier, a hashed password, a roles collection stored as a single
jsonb/array column (no separate roles table), a branch-scope list, and a
revoked/deactivated flag. Storage MUST enforce Postgres row-level security
scoped by `app.current_org_id`, following the `sync_inbox` table's
`FORCE ROW LEVEL SECURITY` / non-owner `app_runtime` role pattern, with a
bare `uuid` `organization_id` column and no foreign key to any
organizations table.

#### Scenario: User row is isolated by organization

- GIVEN a user exists in Organization A
- WHEN a request scoped to Organization B queries for that user by email
- THEN no row is returned

#### Scenario: Revoked user is flagged, not deleted

- GIVEN an administrator revokes a user
- WHEN the user row is read
- THEN the revoked/deactivated flag is true and the row still exists

### Requirement: Password Hashing

The system MUST hash passwords using `PasswordHasher<TUser>`
(`Microsoft.Extensions.Identity.Core`, ASP.NET Core shared framework) before
persistence. The system MUST NOT store or log plaintext passwords, and MUST
NOT implement custom password-hashing cryptography.

#### Scenario: Password is never stored in plaintext

- GIVEN a new user is created with a plaintext password
- WHEN the user row is persisted
- THEN only the hashed representation is stored, and the plaintext is not
  retained anywhere

### Requirement: Real Sign-In Verification

`/account/sign-in` MUST authenticate by looking up a user by email within the
organization implied by the request, verifying the submitted password against
the stored hash via `PasswordHasher<TUser>`, and rejecting sign-in when the
user does not exist, the password does not verify, or the user is revoked.
On success, the system MUST stamp the same `org_id` claim path as before,
sourced from the persisted user row rather than an unverified request field.

#### Scenario: Successful sign-in

- GIVEN a persisted, non-revoked user with a known email and password
- WHEN that email and the correct password are submitted to `/account/sign-in`
- THEN the sign-in succeeds and the Identity cookie carries that user's
  organization and identity claims

#### Scenario: Wrong password is rejected

- GIVEN a persisted user with a known email
- WHEN sign-in is submitted with that email and an incorrect password
- THEN the request is rejected with 401 and no cookie is issued

#### Scenario: Unknown email is rejected

- GIVEN no user exists with a submitted email
- WHEN sign-in is submitted with that email
- THEN the request is rejected with 401, without revealing whether the email
  exists

#### Scenario: Revoked user cannot sign in

- GIVEN a user is persisted with the revoked/deactivated flag set
- WHEN that user submits correct credentials to `/account/sign-in`
- THEN the request is rejected with 401

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

### Requirement: Customer-Scoped Session Distinct From Staff Scheme

A `UserAccount` carrying a non-null `CustomerId` MUST authenticate into a
customer-scoped session issued under its own authentication scheme and
cookie, separate and distinct from the staff scheme issued by
`/account/sign-in`. This is enforced at the authentication layer, in
addition to and independent of the existing `EffectivePermissions → None`
denial for customer-linked accounts.

#### Scenario: Registered customer sign-in issues a customer-scoped session

- GIVEN a `UserAccount` with a non-null `CustomerId` and valid credentials
- WHEN that account signs in through the customer-scoped sign-in endpoint
- THEN the issued session uses the customer-scoped scheme, not the staff
  scheme

#### Scenario: Customer-scoped session is rejected by the staff scheme

- GIVEN a session was issued under the customer-scoped scheme
- WHEN it is presented to an endpoint that requires the staff scheme
- THEN authentication for that scheme fails before any authorization check
  runs

### Requirement: Admin-Provisioned Customer Login Only

Creation of a `CustomerId`-linked `UserAccount` MUST remain a
`ManageUsers`-gated administrative operation. No endpoint, form, or route
MAY allow a caller to create or activate their own registered-customer
login.

#### Scenario: No public self-registration endpoint exists

- GIVEN the complete set of authentication-related endpoints
- WHEN that set is enumerated
- THEN none accepts an unauthenticated or guest request to create a
  `CustomerId`-linked login

### Requirement: Optional Customer Link on User Account

`UserAccount` MUST carry an optional (nullable) `CustomerId` referencing a
`Customer` row in the same organization. A `UserAccount` with no `CustomerId`
remains a staff user, unaffected by this capability. Provisioning a
`CustomerId`-bearing login (creating or linking a `UserAccount` to a
`Customer`) MUST be a `ManageUsers`-gated operation, separate from customer
creation itself. Customer login provisioning and customer creation MUST NOT
be coupled: creating a customer MUST NOT implicitly create a login, and
linking a login MUST NOT implicitly create a customer.

#### Scenario: Login without CustomerId remains staff

- GIVEN a `UserAccount` row has no `CustomerId`
- WHEN its role and permissions are evaluated
- THEN it is treated as a staff user exactly as before this change

#### Scenario: Provisioning a customer login requires ManageUsers

- GIVEN an actor lacks the `ManageUsers` permission
- WHEN they attempt to link a `UserAccount` to a `Customer` via `CustomerId`
- THEN the request is denied and no link is persisted

#### Scenario: Customer creation does not implicitly create a login

- GIVEN a `ManageUsers` holder creates a new `Customer`
- WHEN the customer row is persisted
- THEN no `UserAccount` row is created as a side effect

### Requirement: Customer-Linked User Is Denied Staff Permissions

A `UserAccount` carrying a non-null `CustomerId` MUST be denied all staff
permissions, regardless of any `Role` rows or role assignments recorded
against that account. This denial MUST be enforced by construction in
permission evaluation, not by convention or by omitting role assignment.

#### Scenario: Customer-linked user's effective permissions are always none

- GIVEN a `UserAccount` has a non-null `CustomerId` and also has one or more
  staff `Role` rows assigned to it (e.g. through misconfiguration or a prior
  state)
- WHEN that user's effective staff permissions are evaluated
- THEN the result grants no staff permission, regardless of the assigned
  roles

#### Scenario: Granting a staff role to a customer-linked user is rejected

- GIVEN a `UserAccount` has a non-null `CustomerId`
- WHEN an admin attempts to grant that account a staff role or staff
  permission
- THEN the grant is rejected and the account's effective staff permissions
  remain none

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

### Requirement: Forgot-Password Reset Request

The system MUST expose an anonymous reset-request endpoint that accepts an
email address and, regardless of whether a matching non-revoked user exists,
responds with an empty-body 202. When the email matches a persisted
non-revoked user, the system MUST create a single-use, per-user reset token,
hashed at rest, expiring 1 hour after issuance, and MUST send an email
containing the reset link/token to that user via the transactional email
provider. When no match exists, the system MUST perform a dummy hash
verification to preserve timing parity with the matching path and MUST NOT
create a token or send an email. The endpoint MUST enforce a cooldown per
submitted email AND per source IP, rejecting requests that arrive before the
cooldown elapses.

#### Scenario: Known email receives a reset token and email

- GIVEN a persisted, non-revoked user with a known email
- WHEN a reset request is submitted for that email
- THEN a single-use token is created, hashed at rest, expiring in 1 hour,
  and an email containing the reset link is sent to that user
- AND the response is an empty-body 202

#### Scenario: Unknown email looks identical to a known one

- GIVEN no user exists with a submitted email
- WHEN a reset request is submitted for that email
- THEN no token is created and no email is sent
- AND the response is an empty-body 202, indistinguishable in status, body,
  and timing from the known-email case

#### Scenario: Repeated requests are throttled

- GIVEN a reset request was already submitted for an email or from an IP
  within the cooldown window
- WHEN another reset request arrives for that same email or from that same IP
  before the cooldown elapses
- THEN the request is rejected without creating a new token or sending
  another email

### Requirement: Forgot-Password Reset Confirm

The system MUST expose an anonymous confirm endpoint that accepts a reset
token and a new password. The system MUST reject the request generically
when the token is invalid, already used, or expired (more than 1 hour past
issuance), without revealing which condition applied. On success, the system
MUST hash the new password using the existing `PasswordHasher<UserAccount>`
convention, persist it, mark the token as used so it cannot be reused, and
invalidate the user's existing session(s).

#### Scenario: Valid token sets a new password

- GIVEN a persisted, unused reset token issued less than 1 hour ago
- WHEN the confirm endpoint is called with that token and a new password
- THEN the user's password hash is updated, the token becomes unusable, and
  the user's prior session(s) no longer authenticate

#### Scenario: Token cannot be reused

- GIVEN a reset token was already used to set a new password
- WHEN the same token is submitted again
- THEN the request is rejected and the password is not changed

#### Scenario: Expired token is rejected

- GIVEN a reset token was issued more than 1 hour ago
- WHEN the confirm endpoint is called with that token
- THEN the request is rejected and the password is not changed

### Requirement: Authenticated Password Renewal

The system MUST expose an authenticated endpoint that lets a signed-in user
change their own password by supplying their current password and a new
password. The system MUST verify the current password against the stored
hash before accepting the new one, rejecting the request generically when
the current password does not verify. On success, the system MUST hash the
new password using the existing `PasswordHasher<UserAccount>` convention,
persist it, and invalidate the user's existing session(s).

#### Scenario: Correct current password renews the password

- GIVEN an authenticated, non-revoked user
- WHEN they submit their correct current password and a new password to the
  renewal endpoint
- THEN their password hash is updated and their prior session(s) no longer
  authenticate

#### Scenario: Wrong current password is rejected

- GIVEN an authenticated user
- WHEN they submit an incorrect current password to the renewal endpoint
- THEN the request is rejected, the password is not changed, and no other
  detail is revealed

### Requirement: Admin-Forced Password Reset

The system MUST expose an authenticated endpoint that lets a user holding
`Permission.ManageUsers` force-set a new password for another user, scoped
to users within the same organization as the acting admin. The system MUST
reject the request when the target user belongs to a different organization
than the acting admin, without revealing whether the target user id exists
elsewhere. On success, the system MUST hash the new password using the
existing `PasswordHasher<UserAccount>` convention, persist it, and
invalidate the target user's existing session(s).

#### Scenario: Admin resets a same-organization user's password

- GIVEN an authenticated user holding `Permission.ManageUsers` and a target
  user in the same organization
- WHEN the admin submits a new password for the target user
- THEN the target user's password hash is updated and the target user's
  prior session(s) no longer authenticate

#### Scenario: Cross-organization target is rejected

- GIVEN an authenticated user holding `Permission.ManageUsers` and a target
  user in a different organization
- WHEN the admin submits a new password for that target user
- THEN the request is rejected and the target user's password is not changed

### Requirement: Session Invalidation on Password Change

The system MUST ensure that any successful password change — self-service
reset confirm, authenticated renewal, or admin-forced reset — invalidates
all previously issued cookie sessions for the affected user, such that a
cookie issued before the change no longer authenticates requests after it.

#### Scenario: Prior session cookie stops authenticating after a change

- GIVEN a user has an active, previously issued session cookie
- WHEN that user's password is changed by any of reset confirm, renewal, or
  admin-forced reset
- THEN a request authenticated with the prior cookie is rejected
