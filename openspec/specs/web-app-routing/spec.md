# Web App Routing Specification

## Purpose

Give `Commerce.Web` addressable client-side routes, a public front door at
`/`, and redirect-based auth guard semantics for staff routes, replacing
`App.tsx`'s auth ternary with a real route tree.

## Requirements

### Requirement: Public Routes Render Without Auth Dependency

The system MUST render `/`, `/login`, `/forgot-password`, and
`/reset-password/:token` with zero dependency on auth/session machinery.
These routes MUST NOT import or execute code from the authenticated shell.

#### Scenario: Unauthenticated visitor loads the public landing page

- GIVEN a first-time visitor with no session
- WHEN they navigate to `/`
- THEN the public `HomeScreen` renders successfully
- AND no auth/session request is made

#### Scenario: Public routes are addressable on hard refresh

- GIVEN a visitor's browser is pointed directly at `/login`,
  `/forgot-password`, or `/reset-password/some-token`
- WHEN the page is hard-refreshed (full navigation, not client routing)
- THEN the server's SPA fallback serves `index.html`
- AND the router mounts the correct screen for that URL

### Requirement: Signed-In Visitor at `/` Sees the Public Page

`/` MUST always render the public page for any visitor, authenticated or
not. The system MUST NOT auto-redirect an authenticated visitor away from
`/`, and MUST show them a visible link into the app.

#### Scenario: Authenticated visitor navigates to `/`

- GIVEN a visitor has an active authenticated session
- WHEN they navigate to `/`
- THEN the public `HomeScreen` renders unchanged, not the authenticated app
- AND a visible link into the app is shown

### Requirement: Single Shared `/login` Route

The system MUST expose exactly one `/login` route used by all account
types. What renders after a successful sign-in MUST depend on the
authenticated identity's role/account type, not on the URL used to reach
`/login`.

#### Scenario: Staff member signs in via `/login`

- GIVEN a staff member with valid credentials
- WHEN they submit the sign-in form at `/login`
- THEN they land on the staff destination appropriate to their role

### Requirement: Guarded Staff Routes Redirect on Unauthenticated Access

Guarded staff routes (catalog, order console, renew-password) MUST redirect
an unauthenticated visitor to `/login` instead of blank-rendering. The
guard MUST preserve the originally-requested destination so that a
successful sign-in returns the visitor to it.

#### Scenario: Unauthenticated deep link redirects to login

- GIVEN a visitor with no session navigates directly to a guarded route
  (e.g., the order console)
- WHEN the route guard evaluates the request
- THEN the visitor is redirected to `/login`
- AND no guarded content is rendered

#### Scenario: Post-login return to originally-requested route

- GIVEN a visitor was redirected to `/login` from a guarded deep link
- WHEN they complete sign-in successfully
- THEN they land on the originally-requested guarded route, not a default
  landing page

### Requirement: Reset-Password Uses a Path Param, Not a Query String

The reset-password link MUST use the path-param shape
`/reset-password/:token`. The system MUST NOT accept or interpret the
legacy `?token=` query-string shape after this change ships; there is no
dual-format support.

#### Scenario: Path-param reset link resolves the token

- GIVEN a reset email was sent after this change is deployed, containing a
  link of the form `/reset-password/{token}`
- WHEN the recipient opens the link within the token's 1-hour validity
  window
- THEN `ResetPasswordScreen` reads the token from the URL path
- AND the reset flow proceeds normally

#### Scenario: Legacy query-string link is not supported post-deploy

- GIVEN a reset email was sent before this change, containing a link of
  the form `/reset-password?token={token}`
- WHEN the recipient opens that link after deployment, whether or not the
  token's 1-hour TTL has elapsed
- THEN the route does not extract a token from the query string
- AND the visitor sees the same not-found/expired treatment as an invalid
  token, with no silent dual-format acceptance

### Requirement: OrderScreen Behavior Is Unchanged

Introducing the route tree MUST NOT alter `OrderScreen`'s existing
behavior. Only its mount point in the tree changes; it remains reachable
solely through the guarded order-console route.

#### Scenario: Order console behaves identically after routing is introduced

- GIVEN a staff member is authenticated and navigates to the order
  console route
- WHEN they use `OrderScreen`'s existing functionality (e.g., completing
  an order)
- THEN its behavior, inputs, and outputs are identical to before the
  routing shell was introduced
- AND the route remains staff-only behind the auth guard
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
