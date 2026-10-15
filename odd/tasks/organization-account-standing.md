# Organization Account Standing

## Objective

Let a system administrator mark an organization as overdue on its payment to
Incoders, give it a 30-day tolerance with a visible countdown, and suspend web
access when the tolerance runs out, without ever stopping a branch from
selling.

## Why

There is no way today to stop an organization that does not pay from using
the product. Cutting access has to be predictable, auditable and fair: the
business owner gets warned every day with the exact number of days left, the
staff is not exposed to a commercial matter, and no sale, cash session or
piece of data is ever lost.

## Owner decisions (2026-10-09)

- The POS only warns. It never blocks sales, cash sessions or sync, in any
  state. This keeps ADR-002 ("local sales are never blocked") and the
  `pos-installation-identity` spec intact, so no new ADR is needed.
- Tolerance is 30 days after the due date, with a countdown of the days left.
- Web: only administrators see the warnings and the countdown: holders of
  `Permission.ManageUsers`, the same check `RequireAdmin` already uses.
  Other web users see nothing while the account is overdue.
- POS: EVERY signed-in operator sees the warning, in red, in the footer
  status bar, so cashiers relay it to the owner. The closing line differs by
  role (`ManageUsers` in `MainWindow.xaml.cs` nav gating decides "admin").
- Lightweight ODD, not SDD.

## Standing model

The standing is DERIVED from dates on every read, never stored as a status,
so it changes on its own when a day passes and needs no scheduled job.

| Standing | Rule (business day, `IBusinessClock`) | Web | POS |
|---|---|---|---|
| `Active` | no due date set, or today <= due date | normal | normal |
| `Overdue` | due date < today <= due date + grace days | normal; admins see a banner with the days left | normal; every signed-in operator sees a red footer notice with the days left |
| `Suspended` | today > due date + grace days, or manually suspended | organization users get the suspended screen; the sysadmin is exempt | normal; every signed-in operator sees a red footer notice |

- `SuspendsOn` = due date + grace days + 1. `DaysLeft` = `SuspendsOn` - today.
- An organization with no due date is never overdue, so every existing
  organization stays `Active` after the migration.
- Proposed UI copy (Spanish, product convention):
  - Web, overdue, admins (banner): "Hay un pago vencido. El acceso web se
    suspende en N días (el DD/MM/AAAA). Comuníquese con Incoders para
    regularizar."
  - Web, suspended, admins (screen): "La cuenta está suspendida por falta de
    pago. Comuníquese con Incoders para reactivarla."
  - Web, suspended, non-admins (screen): "El acceso está suspendido.
    Contacte al administrador de su empresa."
  - POS footer, overdue: "Pago pendiente: el acceso web se suspende en N
    días." followed by "Informe al administrador." (cashier) or
    "Comuníquese con Incoders." (admin). "en 1 día" in singular.
  - POS footer, suspended: "Acceso web suspendido por pago pendiente." with
    the same role-based closing line.
  - POS tooltip: "Fecha de suspensión: DD/MM/AAAA".
  - The POS says "acceso web" on purpose: the POS itself keeps working, so
    "servicio suspendido" would be false for the person reading it.

## Scope

- Organization fields: billing due date (nullable), grace days (default 30,
  0-90) and a manual suspension timestamp (nullable).
- Domain rule that derives `Status`, `SuspendsOn` and `DaysLeft`.
- Sysadmin actions on an organization: set due date and grace days (also how a
  payment is recorded: move the due date to the next period), suspend now,
  reactivate. Every action is audited.
- Web enforcement for cookie-authenticated organization users while
  `Suspended`.
- Standing exposed to the web (`/account/me`) and to the POS
  (`/device/organization/settings`, read on every sync).
- Web: standing column and manage dialog in the sysadmin Organizations screen;
  admin banner; suspended screen.
- POS: red footer notice for every signed-in operator, computed from the
  last synced standing, so it keeps counting offline.

## Out of scope (later features)

- Email reminders before and after the due date (`EmailOptions` exists).
- Invoicing, payment gateway, automatic payment detection.
- Public and customer ordering surfaces (`PublicOrdering`, `CustomerSession`):
  unchanged in every state until the owner decides otherwise.
- Data export for a suspended organization.

## Constraints

- Clean domain: the standing rule lives in `src/Commerce.Domain`, pure, with
  `today` passed in; "today" comes from `IBusinessClock`
  (`America/Argentina/Buenos_Aires`), never `DateTime.Now`.
- Business rules on the server; the web only renders what the API returns
  (AGENTS.md). The POS receives the rule's INPUTS (due date, grace days,
  manual suspension) and runs the same `AccountStandingRules.Evaluate` from
  `Commerce.Domain` (already referenced) on the local business day, so it
  also catches the Active -> Overdue -> Suspended transitions while offline
  and the rule exists once.
- Migration `0052_organization_account_standing.sql`: forward-only,
  idempotent (`ADD COLUMN IF NOT EXISTS`, constraints added only when
  missing), appended verbatim to `deploy/dev/db/init-rls.sql`. The
  `organizations` RLS policy is unchanged; writes go through the existing
  sysadmin store path that scopes with `set_config` to the target id.
- Enforcement never touches the `DeviceBearer` scheme: POS sales push, pulls
  and admin calls keep working while suspended.
- Allowlist while suspended: sign-in, sign-out, `/account/me`, password
  recovery and `renew-password`. Everything else returns 403 with
  `{ "error": "organization-suspended" }` so the SPA can tell it apart from a
  permission 403. Implemented as `OrganizationSuspensionMiddleware` after
  `UseAuthorization`: it acts only on endpoints that require authorization,
  only for the staff cookie, skips the `DeviceBearer` and `Customer`
  policies, and an endpoint opts out with `.AllowWhileOrganizationSuspended()`.
  Every business group declares `RequireAuthorization`; the root `/account`
  group holds only the allowlisted and anonymous routes.
- Standing reads go through a 60s-TTL cache with write-through on every
  sysadmin change, the same pattern and staleness bound as
  `SessionVersionCache`.
- `/account/me` returns `status` to every user, but `suspendsOn` and
  `daysLeft` only to `ManageUsers` holders and the sysadmin.
- Audit with the existing `UserManagementAuditEntry`
  (`organization.standing_updated`, `organization.suspended`,
  `organization.reactivated`; underscore form like
  `organization.branding_updated`).
- `platform_readonly` keeps its `(id, name, created_at)` column grant (0007:
  the only cross-organization read). The sysadmin list reads each
  organization's standing through `app_runtime` scoped to that organization.
- Cross-layer: the banner texts exist in both layers; record them in
  `docs/architecture/cross-layer-parity.md` (skill `cross-layer-parity`).
- TDD: Strict (RED -> GREEN -> REFACTOR). Runners: `dotnet test`, `npm test`
  (Vitest), `npm run lint`, `npm run build` in `src/Commerce.Web`.
- Commit straight to `dev`, Conventional Commits, no AI attribution.

## Tasks

- [x] T1 Domain: `AccountStanding` rule (`Active`/`Overdue`/`Suspended`, `SuspendsOn`, `DaysLeft`) with boundary tests: no due date, due date today, first overdue day, last grace day, first suspended day, grace 0, manual suspension overriding a future due date, reactivation. RED: `AccountStandingRulesTests` did not compile (no `AccountStandingRules`); GREEN: 17/17. `src/Commerce.Domain/Tenancy/AccountStanding.cs`.
- [x] T2 Migration `0052`: due date, grace days (CHECK 0-90, default 30), manual suspension timestamp; mirrored in `init-rls.sql`; migration tests including a re-run. RED: 6 `OrganizationAccountStandingTests` failed (migration file missing); GREEN: 6/6, plus 95/95 with the migration, RLS and organization settings suites. Columns `billing_due_on`, `billing_grace_days`, `suspended_at`; constraint `organizations_billing_grace_days_ck`. Applied to local `commerce_dev`.
- [x] T3 API sysadmin: standing in the organizations list; `PUT /account/organizations/{id}/standing` (due date, grace days), `POST .../standing/suspend`, `POST .../standing/reactivate` (requires a new due date); audited; non-sysadmin gets 403. RED: `OrganizationAccountStandingEndpointTests` did not compile (no contracts); GREEN: 11/11, and 35/35 with `AdminConsoleTests` and the treasury recurrence suites. Also `GET .../standing`; unknown organization 404; reactivation rejects a due date before today and keeps the current grace days when none is sent; a second suspension keeps the first time. `Endpoints/OrganizationAccountStanding.cs`.
- [x] T4 API enforcement: standing cache with write-through; suspension filter with the allowlist; `/account/me` gains `accountStanding`. Tests: a suspended organization's admin gets 403 `organization-suspended` on a business endpoint and 200 on `/account/me`; a sysadmin acting on a suspended organization is not blocked; an overdue organization is not blocked; a POS sale push from a suspended organization is accepted. RED: `OrganizationSuspensionEnforcementTests` did not compile (no `AccountStanding` on `SignedInResponse`); GREEN: 8/8, 44/44 with the T1-T3 suites. Control run with the middleware disabled: the 3 blocking tests fail. `Authentication/OrganizationStandingCache.cs` (60s TTL, invalidated by every T3 write), `Authentication/OrganizationSuspensionMiddleware.cs`. The POS check sends a device-bearer `/sync/inbox` push and `/device/organization/settings` read, both 200 while suspended. Also A6: the reactivation audit records the grace days actually stored (`RETURNING billing_grace_days`); RED showed `null`.
- [x] T5 API device: `GET /device/organization/settings` gains `accountStanding { dueOn, graceDays, suspended }` (the rule's inputs; additive, an older POS ignores it). RED: `DeviceOrganizationSettingsTests` did not compile (no `AccountStanding`); GREEN: 5/5. Read straight from the store, not the T4 cache: it runs once per sync sweep and the terminal never sees an older standing than the database.
- [x] T6 Web sysadmin: standing badge and due date column in `OrganizationsScreen`; manage dialog (due date, grace days, suspend now, reactivate with confirmation). RED: 2 `OrganizationsScreen` tests failed and `OrganizationStandingForm.test.tsx` could not resolve its module; GREEN: 27/27; full type check (`tsc -b --force`) clean, lint clean, web suite 752 passed (one A10 timeout, passes alone). `screens/OrganizationStandingForm.tsx`: not suspended by hand = Save (due date, empty = not tracked; grace 0-90 required) + "Suspender ahora" behind `ConfirmDialog`; suspended by hand = "Reactivar" (due date today or later, as the server enforces). `noValidate` so the form's own Spanish message shows instead of the browser's `max` bubble. Copy in `organizations.json` (es/en).
- [x] T7 Web tenant: admin banner in `AppLayout` with the countdown; suspended screen (admin and non-admin copy); `apiFetch` routes a 403 `organization-suspended` to that screen. The SPA's session is the SIGN-IN response kept in memory (not `/account/me`), so the server now returns `accountStanding` on sign-in too (shared `OrganizationAccountStandingEndpoints.SummaryForAsync`; RED: sign-in carried none). A suspension during an open session: `apiFetch` dispatches `commerce:organization-suspended` on `window` and `AuthProvider` marks the session Suspended. RED: 6 web tests (client, AuthContext, AppLayout) failed; GREEN: web suite 740 passed, lint clean, build passes. `components/layout/AccountStanding.tsx`; copy in `common.json` (es/en) with `_one`/`_other` for "1 día".
- [x] T8 POS: persist the synced standing inputs in `BranchSyncStore` next to the organization settings and evaluate them with `AccountStandingRules`; red notice in the footer status bar (`MainWindow.xaml` row 2, `DangerBrush`/`DangerSurfaceBrush`) for every signed-in operator, hidden while locked; countdown computed from `SuspendsOn` and the local business day; role-based closing line; tooltip with the date. Test that a sale completes while `Suspended`. RED: `PosAccountStandingTests` did not compile (no `AccountStandingReplica`, store methods, `AccountStandingNotice`); GREEN: 52/52 with the organization settings replica, POS markup, domain rule and device settings suites. Own `account_standing_replica` table (one row; an older server's answer without a standing keeps the last known one). `AccountStandingNotice.Compose` (pure, testable) evaluates the domain rule; a value the rule rejects shows nothing instead of throwing. The pill is `AccountStandingPill` (not `AccountStandingNotice`, which would shadow the class inside `MainWindow`). Refreshed after every sweep (60s, so a day change offline moves it), on sign-in and on lock. Not exercised on screen by the tests (WPF cannot be instantiated in the test host): markup and wiring are asserted structurally.
- [x] T9 Docs: cross-layer parity entry for the banners; update this document's progress. Row "Organization account standing" in `docs/architecture/cross-layer-parity.md`, recorded as an intentional difference.

## Acceptance criteria

- A sysadmin sets a due date for Vaca Verde of yesterday: its admin sees the
  30-day countdown in the web banner and the POS footer; a cashier sees it in
  red in the POS footer with "Informe al administrador." and sees nothing in
  the web.
- One business day later both banners show 29 days, including on a POS that
  has been offline since the previous sync.
- When the grace runs out, the Vaca Verde admin signs in to the web and sees
  only the suspended screen; any business endpoint returns 403
  `organization-suspended`.
- The POS of a suspended organization opens a cash session, completes a sale,
  closes the cash session and syncs the sale.
- "Suspend now" suspends immediately; "reactivate" with a new due date
  restores web access within 60 seconds. Every change has an audit entry.
- An organization with no due date behaves exactly as before.
- All checks pass.

## Progress

- Feature document created 2026-10-09.
- 2026-10-09: owner approved the model; POS notice widened to every signed-in
  operator, in red, in the footer.
- 2026-10-09: T1 done. The device payload carries the rule's inputs instead
  of a computed status, so the POS evaluates the same domain rule offline.
- 2026-10-09: T2 done.
- 2026-10-09: T3 done. The full `Commerce.Integration` run did not finish
  within an hour and was stopped; the affected suites pass. Root cause found
  during T4: Docker Desktop updated itself (engine 29.8.1 -> 29.8.2) and the
  Postgres container exited mid-run. These suites RETURN EARLY without
  Postgres and count as passed, so a run without a healthy container proves
  nothing; T2-T4 were re-run with the container checked healthy before and
  after.
- 2026-10-10: T5 done; A9 fixed.
- 2026-10-10: polish review (`review-56c395c2f8a73ebf`, approved) fixed
  after: a failed re-read after a successful "Suspender ahora" said "No se
  pudo guardar" as if the suspension had failed; it now says the organization
  is suspended and offers Retry. The time zone test could not fail here:
  changing `TZ` (even at process start) has no effect on Windows' Node, and
  the explicit Buenos Aires zone canonicalizes to the system one, so the date
  is formatted by `lib/businessDate.ts` `formatInstantDate(instant, zone)`,
  whose unit test passes an explicit zone (Madrid vs Buenos Aires) and does
  discriminate.
- 2026-10-10: polish commit closed A11, A12, A13 and the A8 comment. The web
  countdown now subtracts the days elapsed since the standing was received
  (`receivedOn`, stamped by `AuthProvider`) from the server's `daysLeft`, so
  only a RELATIVE use of the PC clock remains; an unreadable date shows no
  banner. The sysadmin list says "Vencida" for an Overdue standing without a
  count; suspending keeps unsaved edits; "Suspender ahora" is hidden once the
  dates suspended the organization; the manual suspension date is formatted
  in Buenos Aires time (its test passes on this machine either way, since the
  machine is in Argentina's time zone; the code sets the zone explicitly).
- 2026-10-10: T8 and T9 done.
- 2026-10-10: T6 done.
- 2026-10-10: T7 done.
- 2026-10-10: T7 review fixes (`review-5daed399ddad7724`, approved): the
  banner counts the days left from today against `suspendsOn` (the session
  lives in memory; a tab open for days showed the sign-in day's count) and
  hides once the date is reached; the suspended screen has "Volver a
  verificar" (`refreshStanding`, re-reads `/account/me`) so a reactivation is
  picked up without signing in again; the 403 body is now
  `{ "error": "organization-suspended" }`, the repository's typed-error
  shape, so the client needs no special case; the moved doc comment in
  `client.ts` is back on `ApiError`; sign-in test asserts `SuspendsOn`.
- 2026-10-10: follow-ups A1, A2, A3, A5, A8 closed, plus the T5 review
  notes (device DTO doc, explicit surface in the segment test, the device
  test now pins the JSON names the POS reads). A1 mattered more than it
  looked: the POS evaluates inputs from the wire, so a manual suspension must
  never become an exception (RED: `ArgumentOutOfRangeException`). Open: A4
  (scale, not needed yet) and A7 (repository-wide, owner's call).
- 2026-10-09: T4 done. Full `Commerce.Integration` with Postgres healthy
  throughout: 2676/2677 in 52 min; the one failure is the pre-existing
  `PublicRateLimitTests` case. Found a failure
  that predates this feature (fails with these changes stashed):
  `PublicRateLimitTests.WithGuestOrderingConfigAbsent_EveryPublicRoute_IsUnreachable_AndAppStillStarts`
  expects 404 and gets 200.

## Reviews

- T1 domain rule `42408bb..b3cc244`: approved, reliability lens
  (`review-f42c6d2691110ef7`).
- T2 migration `b3cc244..35e7b55`: approved, reliability lens
  (`review-a0c3a69078bbaf87`).
- T3 sysadmin API `35e7b55..dc0be74`: approved, reliability lens
  (`review-03529b634f97a2ed`). Fixed right after: an omitted `graceDays` on
  `PUT .../standing` was read as 0 and would have suspended an overdue
  organization at once (now 400, test proves it was 204); reactivation keeps
  the current grace days inside the UPDATE (`COALESCE`, no read-then-write
  race); the keep-current-grace and suspend-twice tests now use values that
  can actually fail (45 days; a suspension time in the past).
- T4 web enforcement `79d828e..480ae4c`: approved, reliability lens
  (`review-5163af7bdcb1ca7a`). Fixed right after: (1) a read loading while a
  write committed could cache the pre-write standing for 60s after the
  invalidation; the cache now keeps a per-organization generation and stores
  a load only if no invalidation happened meanwhile (deterministic RED in
  `OrganizationStandingCacheTests`, plus TTL coverage); (2) the block covers
  only endpoints that declare authorization, so
  `OrganizationSuspensionCoverageTests` now fails on any route that neither
  requires authorization nor opts out, which flagged `POST /account/sign-in`
  (now explicitly `AllowAnonymous`); (3) the sysadmin test never reached the
  exemption because the sysadmin's own organization was active; it is
  suspended now, and a control run with the exemption disabled fails.

## Follow-ups (non-blocking review findings)

- [x] A1 `Evaluate` validates grace days before the manual suspension, so a
  manually suspended organization with invalid grace days throws instead of
  returning Suspended. The `0052` CHECK keeps such data out; revisit if the
  inputs ever come from somewhere else (`AccountStanding.cs:41-48`).
- [x] A2 The last-grace-day test does not assert `SuspendsOn`
  (`AccountStandingRulesTests.cs:46-52`).
- [x] A3 T2 review flagged, location only: the 0-90 acceptance test has no
  explicit assertion (`OrganizationAccountStandingTests.cs:67-75`, warning);
  suggestions at `OrganizationAccountStandingTests.cs:46-61` and on the
  name-only guard of the CHECK (`0052...sql:34-36`, the repository-wide
  idiom).
- [ ] A4 The sysadmin list does one scoped read per organization; move to
  one query if organizations grow to the thousands.
- [x] A6 The reactivation audit logged `graceDays: null` when the current
  value was kept; it now records the stored value (T4).
- [x] A9 T4 fix review: the coverage guard matched surfaces by text prefix,
  so `/customer` also excluded the staff `/customers` routes from the check;
  it now matches whole path segments (with its own test).
- [x] A8 T4 review suggestion: `UpdateAccountStandingAsync` treats any
  non-int scalar as "not found" (`PostgresOrganizationStore.cs`, the
  `storedGraceDays is not int` check); fine for the `integer` column, but a
  type change would read as 404.
- [ ] A10 Repository-wide, not this feature: the web suite has timing
  flakiness. Under load, different screen tests (treasury, orders, catalog,
  customers) time out at 5s in each full run (13, 1, 1, 0 failures across
  runs), with or without this feature's changes (checked against `3a89937`);
  each passes when run alone.
- [x] A11 T7 fix review: the web countdown counts from the browser's date,
  not the server's business day (Buenos Aires); a wrong PC clock shows a
  wrong count (the block itself is always the server's). A malformed
  `suspendsOn` would render NaN; the "Volver a verificar" failure path has no
  test.
- [x] A12 T6 review (`review-ccaaee1519e6854f`, approved; locations only,
  read as): the list falls back to "Al día" for an Overdue standing without
  `daysLeft` (should say Vencida); suspending reloads the form and silently
  drops unsaved due date / grace edits; "Suspender ahora" is offered when the
  organization is already suspended by its dates; the manual suspension date
  is shown in the browser's time zone.
- [x] A13 T8 review (`review-8da608c2c37f6ff6`, approved; locations only):
  the offline day change relied on the 60s sweep running offline (true: the
  `DispatcherTimer` runs regardless and `RunAsync` tolerates unreachable
  endpoints, now pinned by `AFullyOfflineSweep_Completes...`), and the notice
  refresh now sits in a `finally` so even a sweep that throws moves it; the
  footer test now requires the pill inside the status-bar grid. Kept by
  decision: grace days the rule rejects, without a manual suspension, show no
  notice rather than throw (the 0052 CHECK keeps such values out; only
  corrupt data could carry one, and the POS must keep selling).
- [ ] A14 Suspend re-read fix review (`review-7a59b07c5699097f`,
  approved): while the re-read failure is shown, the form keeps the previous
  standing (e.g. "Vencida") and "Suspender ahora" stays enabled (a second
  suspension is a no-op on the server); no test proves that Retry recovers.
- [ ] A7 Repository-wide, not this feature: Postgres-backed tests `return`
  when `TryPing` fails and are reported as Passed. A stopped container turns
  a red suite green. Make them skip visibly (or fail in CI) instead.
- [x] A5 T3 review suggestions: the clear-due-date test does not assert the
  Overdue precondition (`OrganizationAccountStandingEndpointTests.cs`
  "Sysadmin_ClearsTheDueDate"); the grace rejection test checks only the
  status code, not that nothing changed.

## Next step

Owner: review the standing model and the proposed copy, then start T1.
