# Platform Administration Specification

## Purpose

Define a minimal, genuinely separate cross-organization operator identity
(Incoders platform admin), distinct from every org-scoped identity, used to
bootstrap organizations and their first `business-admin`. This is the
smallest possible slice: sign in, list organizations, bootstrap one. It is
not a general platform console.

## Requirements

### Requirement: Sysadmin Identity Lives in the Unified Model

Cross-org sysadmin capability MUST be expressed as a claim, flag, or
associated record within the single existing org-scoped identity model
(`UserAccount`/`user_directory` and its one cookie scheme), not as a
categorically separate identity store, password hasher, or cookie scheme.
The exact storage shape (a cross-org `Permission` bit, an `IsSystemAdmin`
flag, or a dedicated table still authenticating through the same cookie
scheme) is a design-time choice; this requirement constrains only the
observable outcome — one identity model, one authentication path.

(Previously: platform admins were persisted in a dedicated store separate
from `UserAccount`/`user_directory`, verified via a distinct
`PasswordHasher<PlatformAdmin>`. This requirement reverses that isolation
per the confirmed decision to merge platform-admin into the single
org-scoped identity model.)

#### Scenario: Sysadmin authenticates through the same verification path as any staff user

- GIVEN an identity holding cross-org sysadmin capability
- WHEN that identity signs in
- THEN password verification uses the same mechanism used for every other
  `UserAccount`, not a distinct hasher or store

#### Scenario: No dedicated platform-admin table drives authentication

- GIVEN the complete set of tables consulted during sign-in
- WHEN that set is enumerated
- THEN no table exists whose sole purpose is a separate, isolated
  platform-admin credential store distinct from the unified identity model

### Requirement: Single Sign-In Endpoint For Every Identity

The system MUST expose exactly one sign-in endpoint, issuing exactly one
cookie scheme, used by every account type including a cross-org sysadmin.
Sign-in MUST reject an unknown email, a wrong password, or a revoked
account, identically regardless of whether the account holds cross-org
sysadmin capability.

(Previously: platform admins signed in through a distinct endpoint issuing
a distinct `PlatformAdminAuth` cookie carrying no `org_id` claim, entirely
separate from the org-scoped `CookieAuthenticationDefaults` scheme. This
requirement reverses that separation.)

#### Scenario: Sysadmin and business-admin sign in through the same endpoint

- GIVEN an identity holding cross-org sysadmin capability and a
  business-admin identity
- WHEN each submits valid credentials
- THEN both authenticate through the same sign-in endpoint and receive a
  cookie under the same scheme

#### Scenario: No separate platform-admin cookie scheme is issued

- GIVEN the complete set of authentication cookie schemes the system
  issues
- WHEN that set is enumerated
- THEN exactly one scheme exists, used by every authenticated identity

### Requirement: Cross-Org Read Requires Explicit Sysadmin Capability, Fail-Closed

The system MUST expose an endpoint that lists organizations across
tenants without relying on `TenantScopeResolver`/`app.current_org_id` row
scoping, gated by an explicit cross-org sysadmin capability check on the
unified identity. A caller lacking that capability MUST be rejected
outright; the endpoint MUST NOT silently fall back to an org-scoped
result for such a caller.

(Previously: this endpoint was gated by the separate `PlatformAdminAuth`
scheme rather than a permission check on the unified identity. The
fail-closed safety property — never silently narrowing to an org-scoped
read — is preserved unchanged by this delta; only the gating mechanism
changes.)

#### Scenario: Sysadmin lists organizations across tenants

- GIVEN two organizations are persisted
- WHEN an authenticated identity holding cross-org sysadmin capability
  calls the list-organizations endpoint
- THEN both organizations are returned in the response

#### Scenario: Caller without sysadmin capability is rejected, not narrowed

- GIVEN an authenticated `business-admin` without cross-org sysadmin
  capability
- WHEN they call the list-organizations endpoint
- THEN the request is rejected outright, not silently narrowed to their
  own organization's data

### Requirement: Bootstrap Organization's First Business-Admin

The system MUST expose an endpoint, gated by cross-org sysadmin capability
on the unified identity, that creates a new organization, at least one
branch, and that organization's first `business-admin` user in a single
transaction, given an explicit target organization name supplied by the
caller (never inferred from request-scoped claims, since none exist for a
brand-new organization).

(Previously: this endpoint was gated by the separate `PlatformAdminAuth`
scheme. Behavior is otherwise unchanged.)

#### Scenario: Sysadmin bootstraps a new organization

- GIVEN an authenticated identity holding cross-org sysadmin capability
  and a new organization name
- WHEN the bootstrap endpoint is called
- THEN an organization row, at least one branch, and a `business-admin`
  user are created in a single transaction, and the created admin can
  sign in

### Requirement: Cross-Org Endpoint Isolation From Org-Scoped Callers

Cross-org sysadmin endpoints MUST require the explicit cross-org sysadmin
capability check and MUST reject any authenticated caller lacking it,
regardless of how many org-scoped permissions that caller otherwise holds.
Cross-org data access MUST use an explicit, per-call target
`organization_id` rather than deriving one from claims.

(Previously: isolation was enforced by requiring a categorically separate
`PlatformAdminAuth` scheme, unreachable from the org-scoped cookie scheme
and vice versa. Under the unified model there is only one scheme, so
isolation is enforced by an explicit capability check on that one scheme
instead of by scheme separation. The isolation outcome — an org-scoped
caller cannot reach cross-org endpoints — is unchanged.)

#### Scenario: Business-admin cannot reach a cross-org endpoint

- GIVEN an authenticated `business-admin` holding every org-scoped
  `Permission` flag but not cross-org sysadmin capability
- WHEN they call any cross-org sysadmin endpoint
- THEN the request is rejected

#### Scenario: Sysadmin action targets an explicit organization id

- GIVEN an authenticated sysadmin bootstraps a new organization
- WHEN the request is processed
- THEN the organization id acted upon is the one explicitly supplied in
  the request, not derived from any claim

### Requirement: Platform-Admin Action Auditing

Every cross-org sysadmin action MUST write an append-only audit record in
the same transaction as the action, capturing actor id, actor kind
(sysadmin), acted-on entity type and id, organization id, action,
timestamp, and old/new value where applicable.

(Previously: actor kind was recorded as `platform-admin`, reflecting the
separate identity store. The audit obligation itself is unchanged; only
the actor-kind vocabulary may change to match the unified model's
capability representation, decided at design time.)

#### Scenario: Bootstrap action is audited

- GIVEN a sysadmin bootstraps a new organization
- WHEN the bootstrap transaction commits
- THEN an audit row exists in that same transaction recording the
  sysadmin as actor, the created organization and admin user as acted-on
  entities, the action, and the timestamp

