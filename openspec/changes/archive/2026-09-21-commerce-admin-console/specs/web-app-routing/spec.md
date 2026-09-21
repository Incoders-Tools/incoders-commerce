# Delta for Web App Routing

## ADDED Requirements

### Requirement: Guarded Admin Routes For Staff and Branch Management

The system MUST expose a `RequireAdmin`-gated Users route and a
`RequireAdmin`-gated Branches route, following the same redirect-on-
unauthenticated-access guard semantics as the existing guarded staff
routes.

#### Scenario: Unauthenticated visitor is redirected from an admin route

- GIVEN a visitor with no session navigates directly to the Users route
  or the Branches route
- WHEN the route guard evaluates the request
- THEN the visitor is redirected to `/login`
- AND no guarded content is rendered

#### Scenario: Authenticated caller without ManageUsers cannot reach the Users route

- GIVEN an authenticated staff member without `Permission.ManageUsers`
- WHEN they navigate to the Users route
- THEN they are denied, matching the existing `RequireAdmin` guard used by
  the Customers route

### Requirement: Guarded Organization-Onboarding Route Requires Sysadmin Capability

The system MUST expose a guarded organization-onboarding route reachable
only by an authenticated identity holding cross-org sysadmin capability.
An authenticated identity without that capability MUST be denied, not
merely hidden from navigation.

#### Scenario: Business-admin is denied direct navigation to onboarding

- GIVEN an authenticated `business-admin` without cross-org sysadmin
  capability
- WHEN they navigate directly to the organization-onboarding route
- THEN they are denied and the screen does not render

#### Scenario: Sysadmin reaches the onboarding route

- GIVEN an authenticated identity holding cross-org sysadmin capability
- WHEN they navigate to the organization-onboarding route
- THEN the organization-onboarding screen renders
