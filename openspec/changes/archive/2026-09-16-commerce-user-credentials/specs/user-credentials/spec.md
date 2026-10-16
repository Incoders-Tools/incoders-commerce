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
token after one successful use.

#### Scenario: Bootstrap creates the first admin

- GIVEN an organization has zero persisted users and a valid bootstrap token
  scoped to that organization
- WHEN the bootstrap endpoint is called with that token and admin credentials
- THEN an admin user is created for that organization and the token becomes
  invalid

#### Scenario: Bootstrap rejected when an admin already exists

- GIVEN an organization already has at least one persisted user
- WHEN the bootstrap endpoint is called with a token scoped to that
  organization
- THEN the request is rejected and no user is created

#### Scenario: Bootstrap token cannot be reused

- GIVEN a bootstrap token was already used to create an organization's first
  admin
- WHEN the same token is submitted again
- THEN the request is rejected and no user is created

#### Scenario: Bootstrap token is scoped to one organization

- GIVEN a bootstrap token was issued for Organization A
- WHEN it is submitted to bootstrap Organization B
- THEN the request is rejected and no user is created
