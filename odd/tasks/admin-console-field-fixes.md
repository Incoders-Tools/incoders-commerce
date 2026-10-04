# Admin Console Field Fixes

## Objective

Fix the problems the owner found while testing the web admin console and the
desktop management sections: branch-owned web screens fail, the customer
address fields are redundant and hard to fill, and the desktop asks the
signed-in admin for their password again on every management section.

## Why

- Price lists, catalog, receptions and stock answer
  `{"error":"branch-selection-required"}`. Root cause: `BranchProvider`
  (`src/Commerce.Web/src/branch/BranchContext.tsx`) was built and tested but
  never mounted in `App.tsx`, so `apiFetch` never sends `X-Branch-Id`, the
  branch switcher never renders and `useMissingBranch` always answers false.
- The customer form asks for City (a Georef city rendered as
  "city, province"), Locality and Province as free text, and Postal code by
  hand. Locality and Province are empty for all 87 local customers; the
  province is already implied by the city.
- Organizations carry no country; cities carry no postal code (Georef/INDEC
  does not publish postal codes).
- Desktop Customers and Staff each open their own cookie sign-in with the
  admin's password and drop it when the section closes, so an admin who
  unlocked the terminal by PIN is asked for the password once per section.

## Decisions (owner, 2026-10-03)

- Customer address: Province first, then City limited to that province, then
  Postal code. City shows only the city name (no ", province"). The Locality
  field is removed; City replaces it. Province options are every province of
  the organization's country; the organization's country defaults to
  Argentina.
- Postal code is inferred from the selected city when the city has a known
  postal code; the user can still edit it. Cities get an optional postal code
  maintained in the platform Cities screen. No postal code is invented.
- Customer sections (data, contact, etc.) stay separate but get visible
  dividers and spacing so they no longer look glued to the fields.
- Address layout: row 1 Province, City, Postal code; row 2 Neighborhood,
  Street, Number; row 3 Delivery notes across the full row.
- Customer name: one name field, never both. A new customer field
  Person / Company (independent of the commercial Retail / Wholesale kind)
  decides it: a person stores "Nombre y apellido"; a company stores
  "Razón social". Existing customers are backfilled as Company when their
  tax id type is CUIT, otherwise Person (editable). The name of the
  person at a company goes in the contact persons. Stored in `display_name`;
  `legal_name` (empty for all local customers) is no longer written.
- Every email field validates its format with one shared, correct pattern,
  in real time while typing, and shows a green check when valid; the server
  applies the same rule.
- Geography is already normalized (`countries` -> `provinces` -> `cities`,
  verified 2026-10-03); "city, province" is only the picker label. Only
  Argentina is loaded for now.
- Desktop: no password re-prompt for management. If an operator sees a
  management menu entry, they may use it. The menu shows an entry only when
  the operator can actually manage it (`ManageUsers` today). A time-limited
  PIN confirmation may be added later; not now.
  - Accepted trade-off: the server authorizes desktop management from the
    paired device credential plus the signed-in operator. Whoever holds a
    paired terminal and knows an admin's PIN can manage staff and customers
    of that branch.

## Tasks

- [x] T1 Web branch selection: mount `BranchProvider` inside the
  authenticated tree (inside `OrganizationProvider`), render the branch
  switcher in the top navbar, and keep the "select a branch" state for
  branch-owned screens; App-level regression test that a branch-owned screen
  request carries `X-Branch-Id` (route: delegated web writer) - done 5493587:
  provider mounted, Catalog/Price lists guarded like Stock/Receptions,
  `apiFetchForm`/`apiFetchOutcome` now send the tenant headers too (price
  import, catalog rename, orders); RED 3 App + 2 screen + 2 client tests,
  GREEN `npm test` 557 passed (75 files), `npm run lint` 0 errors, `npm run
  build` ok.
- [x] T2 Data and API: `organizations.country_code` (FK `countries`, default
  `AR`, backfilled), exposed in organization settings; `cities.postal_code`
  (optional, validated format) returned by `/geo/cities` and editable in
  `/geo/cities` create/update; `/geo/provinces` filtered to the caller
  organization's country; customer admin API stops reading/writing
  `locality` and `province` (columns kept, no longer written; sync contract
  stays additive-only, replica fields kept and sent as null / derived
  province name) (route: delegated backend writer) - done e4b5a44 (with the
  T3b backend): migrations 0039 (organization country, city postal code) and
  0040 (customer party type, backfilled Company when CUIT); RED every new
  migration/endpoint/domain test failing, GREEN focused 73/73, full `dotnet
  test` 2189 passed / 1 known failure; RDD review approved (4 lenses, 9
  advisory findings; no stored malformed email exists locally).
- [x] T3 Web customer form: Province select (organization country) ->
  City picker filtered by province showing the city name only -> Postal code
  prefilled from the city when known and still editable; Locality and free
  text Province removed; editing an existing customer preselects the
  province of its city; section dividers and spacing; Cities screen edits the
  postal code (route: delegated web writer) - done 098e5dc (with the T3b
  web part): RED 36 failing tests + 3 missing modules, GREEN `npm test` 647
  passed (78 files), lint 0 errors, build ok; RDD review approved
  (reliability, 5 advisory findings).
- [x] T3b Customer name and email: single name field driven by the customer
  type (person -> "Nombre", company -> "Razón social"), contact person for
  companies; shared email validator (web field with live check, server rule,
  desktop field) applied to customer, contact, staff and supplier emails
  (route: delegated backend writer, then delegated web writer) - done
  e4b5a44 (server rule) + 098e5dc (EmailField, live check) + b58956f
  (desktop).
- [x] T4 Desktop customer form: same Province -> City -> Postal code flow
  and no Locality/Province text boxes in `CustomersView` (route: delegated
  POS writer) - done b58956f + 50591b6 (RDD correction R3-001: an update no
  longer clears a stored city the combo cannot show; RED compile failure,
  GREEN 2 new tests, targeted validation passed).
- [x] T5 Desktop management without password: server accepts customer and
  staff management requests authenticated by the paired device credential
  plus the signed-in operator id, and authorizes them only when that user
  exists in the organization, is not revoked, holds `ManageUsers` and has the
  device's branch in scope; every such mutation is audited with the operator
  as actor and the device as origin. POS drops the inline password panel from
  Customers and Staff and shares one management client across sections.
  Menu entries stay gated by `ShellNavigation.Allowed` (route: delegated
  backend writer, then delegated POS writer) - done b58956f: opt-in per
  endpoint (`.AllowDeviceOperator()`), cookie stays the default scheme;
  DeviceOperatorManagementTests 21/21 (RED 11 failing), non-opted endpoints
  proven unchanged for device callers; POS drops both password panels and
  shares one management connection. Two writers crashed (infrastructure);
  finished by the orchestrator. Full `dotnet test`: 2223 passed, 12 failed,
  the same 12 fail on HEAD without this change (see follow-ups).

- [x] T6 Security fixes from the post-review audit of b58956f (owner,
  2026-10-03: fix now, server-checked operator proof later): reset-password
  and PUT roles apply the same target rules as revoke/restore (the target
  never holds permissions beyond the caller's and is inside the caller's
  branch scope), reset-password writes an audit row; staff created through
  a terminal are forced to the terminal's branch. Applies to cookie and
  device callers (route: delegated backend writer, after the staff order
  backend writer finishes) - done: shared `AuthorizeTarget` (403
  permissions-exceed-caller / branch-not-in-scope) for status, roles and
  reset-password; `user.password.reset` audit row; terminal-created staff
  forced to the terminal's branch; a staff order id reused for another
  customer answers 409 order-id-conflict. RED 13 of 18 new tests failing,
  GREEN focused 22/22, full `dotnet test` 2272 passed / 1 known failure.

## Follow-ups

- Server-checked operator proof ("PIN with validity"): today the server
  trusts the device credential plus `X-Operator-Id`; anyone running as the
  terminal's Windows user can act as any cached admin without the PIN.
  After T6 the reach is limited to that branch's staff and customers.

- Tests publish prices with the UTC date while pricing resolves with the
  Argentina business day (0aee664), so OrderPricing, GuestOrdering,
  CatalogPriceSync and PublicRateLimit full-flow tests fail between 21:00
  and 24:00 Argentina time. Fix the test helpers to use the business-day
  clock.
- Audit rows cannot record that a mutation came from a terminal (no origin
  column); needs a migration.
- Desktop customer form: tell the operator when the stored city is kept but
  cannot be shown (deactivated city or failed city list).
- [x] T7 Owner decisions 2026-10-03, web and desktop: (a) an email field
  shows the invalid state on every keystroke while the value is not a valid
  address and the green check only once it is valid (empty stays neutral);
  (b) choosing tax id type CUIT suggests Company: the party type switches to
  Company, and the user can still change it back (route: after the staff
  order web writer and T6 finish) - done a1649da (web: RED 4, GREEN 646
  tests) + desktop `PartyTypeAfterTaxIdTypeChange` (RED compile failure,
  GREEN 99 POS/customer tests); the desktop email already flagged every
  keystroke.

## Acceptance criteria

- Signed in as `admin@vacaverde.local`, Price lists, Catalog, Receptions and
  Stock load for branch Ruta 51 with no `branch-selection-required` error,
  and the navbar shows the selected branch.
- New customer: choosing Santa Fe lists only Santa Fe cities; choosing a city
  with a known postal code fills Postal code; the city is shown as its name
  only; there is no Locality field.
- Editing an existing customer with a city shows its province preselected.
- A company customer shows only "Razón social" and a person only "Nombre".
- Typing `ana@` shows the email as invalid; `ana@mail.com` shows a green
  check; the API refuses an invalid email.
- An organization without an explicit country lists Argentina's provinces.
- On the desktop, an admin unlocked by PIN opens Staff, then Customers, and
  is never asked for a password; a cashier does not see either entry.
- A desktop management request from a revoked operator, an operator without
  `ManageUsers`, or an operator outside the device's branch is refused.

## Constraints

- TDD: Strict (RED -> GREEN -> REFACTOR), source: global config. Runners:
  `dotnet test`; `npm test`, `npm run lint`, `npm run build`.
- Run one test-running writer at a time (shared `commerce_test` deadlocks).
- Migrations forward-only, idempotent, appended to `deploy/dev/db/init-rls.sql`;
  sync contract additive-only.
