# Platform Administration Specification

## Purpose

Define a minimal, genuinely separate cross-organization operator identity
(Incoders platform admin), distinct from every org-scoped identity, used to
bootstrap organizations and their first `business-admin`. This is the
smallest possible slice: sign in, list organizations, bootstrap one. It is
not a general platform console.

## Requirements

### Requirement: Platform Admin Identity and Credentials

The system MUST persist platform admins in a dedicated store separate from
`UserAccount`/`user_directory`, with their own password hash verified via a
dedicated `PasswordHasher<PlatformAdmin>`. A platform-admin row MUST NOT
carry an `organization_id` column or any organization scope.

#### Scenario: Platform admin has no organization scope

- GIVEN a persisted platform-admin row
- WHEN the row's schema is inspected
- THEN it carries no `organization_id` column and no organization
  association

#### Scenario: Platform-admin credentials are verified separately

- GIVEN a persisted platform admin with a hashed password
- WHEN sign-in is attempted with a matching email and password
- THEN verification uses `PasswordHasher<PlatformAdmin>`, independent of the
  org-scoped `UserAccount` verification path

### Requirement: Platform-Admin Sign-In

The system MUST expose a sign-in endpoint for platform admins that issues a
`PlatformAdminAuth` cookie distinct from the org-scoped
`CookieAuthenticationDefaults` scheme, carrying no `org_id` claim. Sign-in
MUST reject an unknown email, a wrong password, or credentials presented
against the org-scoped `UserAccount` store.

#### Scenario: Successful platform-admin sign-in

- GIVEN a persisted platform admin with known credentials
- WHEN that email and password are submitted to the platform-admin sign-in
  endpoint
- THEN sign-in succeeds and the issued cookie carries no `org_id` claim

#### Scenario: Org-scoped credentials do not authenticate as platform-admin

- GIVEN an email and password belong to an org-scoped `UserAccount`, not a
  platform admin
- WHEN they are submitted to the platform-admin sign-in endpoint
- THEN the request is rejected and no `PlatformAdminAuth` cookie is issued

### Requirement: List Organizations

The system MUST expose an endpoint, gated by the `PlatformAdminAuth`
scheme, that lists organizations without relying on
`TenantScopeResolver`/`app.current_org_id` row-level security, since a
platform admin has no current organization.

#### Scenario: Platform admin lists organizations across tenants

- GIVEN two organizations are persisted
- WHEN an authenticated platform admin calls the list-organizations endpoint
- THEN both organizations are returned in the response

### Requirement: Bootstrap Organization's First Business-Admin

The system MUST expose a `PlatformAdminAuth`-gated endpoint that creates a
new organization, at least one branch, and that organization's first
`business-admin` user in a single transaction, given an explicit target
organization name supplied by the platform admin (never inferred from
request-scoped claims, since none exist). This endpoint replaces the
log-only bootstrap-token operator flow as the operator path for new
organizations.

#### Scenario: Platform admin bootstraps a new organization

- GIVEN an authenticated platform admin and a new organization name
- WHEN the bootstrap endpoint is called
- THEN an organization row, at least one branch, and a `business-admin` user
  are created in a single transaction, and the created admin can sign in

### Requirement: Platform-Admin Scope Isolation

Platform-admin-only endpoints MUST require the `PlatformAdminAuth` scheme
and MUST NOT be reachable by a session authenticated under the org-scoped
cookie scheme, and vice versa. Platform-admin data access MUST use an
explicit, per-call target `organization_id` rather than deriving one from
claims.

#### Scenario: Business-admin cannot reach a platform-admin endpoint

- GIVEN an authenticated `business-admin` under the org-scoped cookie scheme
- WHEN they call any platform-admin-only endpoint
- THEN the request is rejected

#### Scenario: Platform-admin action targets an explicit organization id

- GIVEN an authenticated platform admin bootstraps a new organization
- WHEN the request is processed
- THEN the organization id acted upon is the one explicitly supplied in the
  request, not derived from any claim

### Requirement: Platform-Admin Action Auditing

Every platform-admin action MUST write an append-only audit record in the
same transaction as the action, capturing actor id, actor kind
(`platform-admin`), acted-on entity type and id, organization id, action,
timestamp, and old/new value where applicable.

#### Scenario: Bootstrap action is audited

- GIVEN a platform admin bootstraps a new organization
- WHEN the bootstrap transaction commits
- THEN an audit row exists in that same transaction recording the platform
  admin as actor, the created organization and admin user as acted-on
  entities, the action, and the timestamp
