# Admin Console Specification

## Purpose

Give staff and cross-org sysadmins the UI to drive administrative
capability that already exists server-side but has no interface today:
staff/role management, branch management, and organization onboarding.
Reuses the existing `CustomersScreen`/`RequireAdmin` (Web) and
`CustomersWindow` (POS.Windows) precedents rather than introducing a new
UI architecture, and exposes exactly one sign-in surface whose rendered
capability is driven entirely by the signed-in identity's permissions.

## Requirements

### Requirement: Unified Sign-In Surface

`Commerce.Web` MUST expose exactly one sign-in form/route, used by every
account type including a cross-org sysadmin. The system MUST NOT ship a
separate platform-admin login screen, form, or route. What the signed-in
session can see and do MUST be derived entirely from that session's
permissions, not from which login form or endpoint was used to establish
it.

#### Scenario: Sysadmin and business-admin share the same login form

- GIVEN a cross-org sysadmin and a business-admin both hold valid
  credentials
- WHEN each signs in through `Commerce.Web`
- THEN both submit credentials to the same `/login` form and the same
  sign-in endpoint

#### Scenario: No separate platform-admin login route exists

- GIVEN the complete set of `Commerce.Web` routes
- WHEN that set is enumerated
- THEN none is a platform-admin-only sign-in route distinct from `/login`

### Requirement: Sysadmin Capability Is Permission-Driven, Not Route-Driven

After sign-in, `Commerce.Web` MUST render organization-onboarding and
cross-org navigation only when the signed-in identity's permissions grant
cross-org (sysadmin) capability. A signed-in identity without that
capability MUST NOT be able to reach those screens by direct navigation.

#### Scenario: Sysadmin sees cross-org navigation

- GIVEN an authenticated identity holding cross-org sysadmin capability
- WHEN the authenticated shell renders
- THEN organization-onboarding navigation is visible and reachable

#### Scenario: Business-admin cannot reach organization onboarding

- GIVEN an authenticated `business-admin` without cross-org sysadmin
  capability
- WHEN they navigate directly to the organization-onboarding route
- THEN they are denied and the screen does not render

### Requirement: Cross-Org Read Stays Fail-Closed Under the Unified Model

The safety property already shipped for cross-org organization listing —
never silently degrading to an org-scoped read — MUST survive the merge
into the unified identity model, regardless of which specific mechanism
(claim, flag, or table) backs the sysadmin capability.

#### Scenario: Missing sysadmin capability fails closed, not org-scoped

- GIVEN an authenticated identity without cross-org sysadmin capability
- WHEN they call the list-organizations endpoint
- THEN the request is rejected outright, and the response is not silently
  narrowed to that identity's own organization

### Requirement: Web Staff/Role Management Screen

`Commerce.Web` MUST expose a `RequireAdmin`-gated Users screen, following
the `CustomersScreen` precedent, that lists staff in the caller's
organization, creates a new staff user, reassigns an existing staff
user's roles, and forces a password reset — driving `GET /account/users`,
`POST /account/users`, `PUT /account/users/{id}/roles`, and
`POST /account/users/{id}/reset-password` respectively.

#### Scenario: Business-admin lists and creates staff from the Users screen

- GIVEN an authenticated `business-admin`
- WHEN they open the Users screen and submit the create-user form
- THEN the new staff user appears in the screen's list, sourced from
  `GET /account/users`

#### Scenario: Users screen is unreachable without ManageUsers

- GIVEN an authenticated staff member without `Permission.ManageUsers`
- WHEN they navigate to the Users route
- THEN they are denied, matching the existing `RequireAdmin` guard
  behavior used by the Customers screen

### Requirement: Users Screen Cannot Assign Platform-Admin

The Users screen's role-reassignment control MUST NOT offer
`platform-admin` as a selectable role for an org-scoped caller, matching
`RoleCatalog.OrgAssignable`'s server-side exclusion.

#### Scenario: Platform-admin role is absent from the picker

- GIVEN an authenticated `business-admin` opens the role-reassignment
  control for a staff user
- WHEN the list of assignable roles renders
- THEN `platform-admin` is not among the offered options

### Requirement: POS Staff/Role Management Window

`Commerce.Pos.Windows` MUST expose a new modal window, following the
`CustomersWindow` precedent exactly, that provides the same staff
list/create/role-reassign/reset-password capability as the Web Users
screen, scoped to the POS device's paired organization and branch. The
window's entry point on `MainWindow` MUST be visible only when the
current operator holds `Permission.ManageUsers`, MUST open via
`ShowDialog()`, and MUST use a fresh per-window HTTP client whose cookie
is discarded on close.

#### Scenario: Operator without ManageUsers does not see the entry point

- GIVEN a signed-in POS operator without `Permission.ManageUsers`
- WHEN `MainWindow` renders
- THEN no menu entry for the staff/role window is visible

#### Scenario: Staff window follows the CustomersWindow lifecycle

- GIVEN an operator with `Permission.ManageUsers` opens the staff/role
  window
- WHEN the window closes
- THEN its per-window HTTP client and cookie are discarded, matching
  `CustomersWindow`'s existing lifecycle

### Requirement: POS Application Branding Is Configurable Per Installation

`Commerce.Pos.Windows` MUST keep `Commerce.Pos.Windows.exe` as its stable
binary and process identity while using the validated
`Commerce:ApplicationName` value for the main-window and dialog titles.
The product default MUST be `Vaca Verde`. An installation MAY override the
default through `%LOCALAPPDATA%\Incoders\Commerce\branding.json`, which MUST
remain separate from the security-sensitive `installation.json`, or through
the `Commerce__ApplicationName` environment variable. Precedence MUST be
default, then the per-install file, then the environment variable. Blank,
control-character-containing, or longer-than-80-character values MUST fall
back safely to `Vaca Verde`.

The same validated value MUST be the source for a future installer-created
shortcut display name, but this change MUST NOT fabricate an installer that
does not yet exist. Upgrades MUST preserve the per-install branding file.

#### Scenario: Per-install branding changes window titles only

- GIVEN `branding.json` contains a valid `Commerce:ApplicationName`
- WHEN the POS application starts
- THEN the main window and its dialogs use that name in their titles
- AND the running process remains `Commerce.Pos.Windows.exe`

#### Scenario: Environment branding overrides the per-install file

- GIVEN the per-install file and `Commerce__ApplicationName` contain
  different valid names
- WHEN the POS application starts
- THEN the environment value is used for window and dialog titles

#### Scenario: Invalid branding fails safely

- GIVEN the highest-precedence configured application name is blank,
  contains a control character, or exceeds 80 characters
- WHEN the POS application starts
- THEN `Vaca Verde` is used instead

#### Scenario: Upgrade preserves installation branding

- GIVEN an installation has a valid `branding.json` in its POS data directory
- WHEN the application binaries are upgraded
- THEN the branding file remains in place and the configured display name is
  still used

### Requirement: Branch Management UI

`Commerce.Web` MUST expose a `RequireAdmin`-gated UI, scoped to the
caller's own organization, that lists existing branches and creates a new
branch, driving the branch-creation and branch-listing endpoints added by
this change to `organization-persistence`.

#### Scenario: Business-admin creates a branch from the UI

- GIVEN an authenticated `business-admin`
- WHEN they submit the create-branch form with a branch name
- THEN the new branch appears in the UI's branch list for their own
  organization

### Requirement: Organization Onboarding UI

`Commerce.Web` MUST expose a sysadmin-only screen that lists existing
organizations (`GET /platform/organizations`) and creates a new
organization with its first branch and business-admin
(`POST /platform/organizations`), reachable only per the
permission-driven visibility defined above.

#### Scenario: Sysadmin onboards a new organization

- GIVEN an authenticated identity holding cross-org sysadmin capability
- WHEN they submit the organization-onboarding form with an organization
  name
- THEN a new organization, branch, and business-admin are created, and
  the organization appears in the onboarding screen's list
