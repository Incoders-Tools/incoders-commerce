# POS Shell Sections and Lock Screen

## Objective
Replace the POS's stacked modal windows with sections inside the main shell
(Venta, Clientes, Personal), turn operator sign-in into a full-window lock
screen with two ways in (operator tile + PIN, or email + password), and let an
administrator deactivate staff from both the web and the POS.

## Problem
- Operator provisioning, staff and customer management open modal windows.
  The provisioning window has no scroll: on 2026-09-30 the owner could not
  see a "wrong email or password" error (log: `POST /device/operators/verify
  -> 401`) because the content overflowed.
- Sign-out leaves the sale screen loaded behind the login modal.
- The login window is noisy; "Personal" mixes admin staff management with
  operator sign-in (`TerminalOperatorsWindow` provisioning).
- No endpoint changes `users.is_revoked`: an admin cannot deactivate staff
  anywhere (the web only displays the status).

## Why
Owner decisions (2026-09-30):
- Tools open inside the POS; the nav bar returns to Venta. Now: Clientes and
  Personal; other windows negotiated later.
- Personal is admin-only staff management, never a sign-in place. New staff
  enter through the login after the current operator signs out.
- Admin creates staff with email + password and hands them over; the staff
  member creates their PIN on first sign-in at a terminal and can then enter
  either way (tile + PIN, or email + password).
- PIN collisions are harmless because the operator is chosen first and the
  PIN only confirms that person; no PIN-only login.
- Admin can deactivate ("dar de baja") staff.

## Scope (authorized)
- API: deactivate/reactivate a staff user, same rules for web and POS.
- Web: deactivate/reactivate action on the Users screen.
- POS: shell sections for Clientes and Personal (no modal windows for them);
  Personal = admin staff management (create with email + password + role,
  branch = terminal branch; list; deactivate; remove an operator from this
  terminal).
- POS: lock screen replacing the operator login/provisioning windows.

Out of scope: pairing window, cash prompts, other windows (later), reset-PIN
by admin (the staff member re-enters with email + password to set a new PIN).

## Constraints
- Commit directly on `dev`; Conventional Commits; no AI attribution.
- Strict TDD (observed RED before GREEN).
- Backend equally strict for web and POS (same API, same typed errors).
- POS admin actions keep today's server authorization (`UserAdminClient`
  cookie sign-in as the signed-in admin); the password prompt moves inline
  into the Personal section instead of a modal.
- Spanish operator copy via `PosMessages`; English code/docs/commits; UTF-8.
- Keep `DynamicResource` palette keys (Dark, Light, Vaca Verde); every section
  scrolls when content exceeds the window.
- Integration tests need `incoders-commerce-postgres-1` and
  `incoders-commerce-pgbouncer-1` up; run the full suite from a throwaway
  worktree; never `--artifacts-path`; never `docker compose down`.
  `run-all.ps1` populated Cloud.Api `wwwroot`, so `PublicRateLimitTests`
  fails spuriously in the main checkout.

## TDD
- Mode: strict, source: global instructions.
- Runners: `dotnet test tests/Commerce.Integration --filter <Class>`,
  `npm test` in `src/Commerce.Web`.

## Delivery
- Forecast about 1800 authored lines over 4 tasks; trunk on `dev`, one or more
  work-unit commits per task, reviewed per commit under RDD.
- First review boundary: `f75bdbd` (the previous feature's pending slice
  `f75bdbd..3ec7bc3` joins the first review here).

## Tasks
- [x] T1 API: `PUT /account/users/{id}/status` (`{ "revoked": bool }`),
  `ManageUsers`, same tenant/branch cap as roles/branches, cannot deactivate
  yourself, cannot deactivate someone holding permissions you lack, bumps
  `session_version` so web sessions end, audited; revoked users already fail
  sign-in and device verify (confirm). Route: delegated direct.
- [x] T2 Web: "Dar de baja" / "Reactivar" on the Users screen with confirm,
  friendly errors. Route: delegated direct (may share the T1 writer).
- [x] T3 POS shell sections: MainWindow hosts Venta, Clientes, Personal as
  views in the content area (nav switches, returns to Venta); convert
  `CustomersWindow` and `UsersWindow` into views; Personal admin-only with
  create/list/deactivate and terminal-operator removal, no provisioning;
  Spanish copy; scrolling. Route: delegated direct.
- [ ] T4 POS lock screen: full-window sign-in view when no operator (sale not
  visible or interactive); operator tiles + PIN; "Ingresar con usuario y
  contraseña" -> online verify -> create PIN when new to this terminal (or
  replace it); sign-out returns to the lock screen; remove
  `OperatorLoginWindow` / `ProvisionOperatorWindow` modals; first run after
  pairing lands on the lock screen. Route: delegated direct.

## Acceptance criteria
- No modal window opens for Clientes, Personal or operator sign-in.
- With no operator, the sale screen is neither visible nor usable.
- A new staff member signs in with email + password, creates a PIN, and next
  time enters with tile + PIN.
- A deactivated user cannot sign into the web or the POS (both paths), and
  cached POS operators for them are dropped at the next status check.
- Every error is visible (scrolling) and logged.

## Progress
- 2026-09-30: document created (route: parent, direct; mapping reused from
  `staff-roles-and-pos-operator-ux`).
- 2026-09-30 T1+T2 done (route: delegated direct, one writer).
  - T1 `0af1336` feat(account): `PUT /account/users/{userId:guid}/status`
    `{ "revoked": bool }` in the `/account/users` admin group (ManageUsers,
    tenant scope, sysadmin acting allowed). 204, idempotent (no second audit
    row or session bump). Errors: 400 `revoked-required`, 400
    `cannot-revoke-self`, 400 `not-a-staff-user` (customer-linked), 404
    unknown/foreign, 403 `permissions-exceed-caller` (target permissions not
    a subset of the caller's, or sysadmin target for non-sysadmin), 403
    `branch-not-in-scope` (target branch outside caller scope; sysadmin
    acting exempt). Revoke sets `is_revoked`, bumps `session_version`
    (+ `SessionVersionCache.Set`, so the session ends immediately), audits
    `user.revoked` / `user.reactivated`. Spec: user-credentials "Deactivate
    And Reactivate Staff".
  - T2 `cec16fa` feat(web): Users row action "Dar de baja" / "Reactivar" with
    an inline confirm step (no dialog component exists in the codebase),
    hidden on the caller's own row (`useOptionalAuth().user.userId`),
    friendly es/en errors; `updateUserStatus` in `api/account.ts`.
  - RED: 14 new `RoleTaxonomyTests.Status_*` failed 14/14 (405 MethodNotAllowed;
    session test 200 instead of 401) in a worktree seeded with the test +
    request record only; 8 new UsersScreen vitest cases failed 8/8.
    GREEN: RoleTaxonomyTests 38/38; UsersScreen 39/39.
  - Checks: `dotnet build Commerce.sln` (worktree) 0 errors; full
    `dotnet test tests/Commerce.Integration` (worktree) 1206 passed, 1 failed
    (`PosCompositionRootTests.Build_Resolves_BranchNodeService`, SQLitePCL
    disposed object: the known launcher trap, unrelated); `npm test` 320/320;
    `npm run build` ok (dist); `npm run lint` 0 errors, warning count
    unchanged (27); e2e standalone `tsc --noEmit --ignoreConfig` exit 0
    (no e2e selector touches the new row actions).
  - Decisions: revoked users already failed sign-in, `/device/pair`,
    `/device/operators/verify` (401) and status reports inactive; now proven
    end to end through the new endpoint. Reactivation does not bump the
    session version. Commits reviewed per RDD: not yet assessed (pending
    parent).

- 2026-09-30 review of T1+T2 (plus the previous feature's pending slice): RDD
  lineage `review-9eb96e481c8c1028` approved and acknowledged (next boundary
  `9a060cf`). Advisories, route: direct (the T3 writer), commits `078a7e4` and
  `81b0f0e`:
  - WARNING no test that an org admin gets 403 `permissions-exceed-caller` when
    targeting a system administrator: added
    `Status_OrganizationAdminTargetingASystemAdministrator_Returns403_AndNothingChanges`.
    It PASSED immediately (missing coverage, not a bug; `RoleTaxonomyTests`
    39/39).
  - SUGGESTION `confirmStatusChange` reports failure if `refresh()` throws:
    `refresh()` already catches its own errors (it sets the load error and never
    throws), so a failed reload after a successful PUT already shows the load
    alert and not "no se pudo cambiar el estado". Added the vitest case
    `does not claim the change failed when it succeeded but the reload failed`;
    it PASSED immediately (no RED, no code change). UsersScreen 40/40.
- 2026-09-30 T3 done (route: delegated direct, one writer), commits `1433da1`
  (shell + Clientes) and `ec8ce44` (Personal).
  - Shell: `ShellNavigation` (UI-free) holds the current section
    (`Sale`/`Customers`/`Staff`) and the allowed sections by permissions
    (`ManageUsers` gates Clientes and Personal); `Reconcile` falls back to the
    sale when the operator changes (called from `RefreshIdentityText`).
    `MainWindow` content area: `SaleScreen` (Grid, unchanged) and a
    `SectionHost` `ContentControl`. Decision, sale state: the sale was NOT
    extracted into a `SaleView` UserControl (985 lines of code-behind and the
    tests rely on its names and handlers); the shell toggles
    `SaleScreen.Visibility` instead, so the cart, scan box and cash session are
    never rebuilt or cleared. The cash-closed overlay is hidden while a section
    shows. `PosNavBar`: "Nueva Venta" is now "Venta" (raises `SaleRequested`),
    `SetActiveSection` marks the active entry (`Tag="Active"`). A section with a
    request in flight (`ISectionView.IsBusy`) keeps focus until it ends. Each
    visit builds a fresh view and a fresh admin client and disposes them on
    leaving, so the admin cookie lives only while the section is open.
  - Admin password prompt: shared `AdminSignInPanel` ("Confirmá tu contraseña";
    the email of the signed-in operator is shown read-only), cleared right after
    the sign-in call, never stored; the section owns the call.
  - Clientes: `CustomersView` (same behavior as the window) with Spanish
    labels via `CustomerFormChoices` (wire values unchanged), busy state,
    status above the scroll area, no MessageBox. `CustomersWindow` deleted.
  - Personal: `StaffView` = create staff (email, initial password, role Cajero
    default / Vendedor / Administrador from `StaffRoleOptions`, branch = this
    terminal's), staff list (`StaffRowPresenter`: Spanish role labels, branch
    membership, Activo/De baja), "Dar de baja"/"Reactivar" with inline confirm,
    hidden on the admin's own row, password reset (inline panel), and "Operadores
    de esta terminal" with "Quitar de esta terminal" (inline confirm; raises
    `OperatorsChanged`, the host runs `Reconcile`). No provisioning entry.
    `UserAdminClient.SetStatusAsync` + Spanish mapping of `cannot-revoke-self`,
    `not-a-staff-user`, `permissions-exceed-caller`, `branch-not-in-scope` (also
    for create); `UserAdminRecordDto` reads `branchIds`. `UsersWindow` and
    `TerminalOperatorsWindow` deleted. `ProvisionOperatorWindow` and
    `OperatorLoginWindow` stay for T4.
  - Not carried over: role reassignment of an existing user from the POS (the
    window had it; the web Users screen keeps it; spec "POS Staff Management
    Section" says so). Branding keeps the now unused `UsersWindowTitle` /
    `CustomersWindowTitle` (a test locks them).
  - Specs: `pos-operator-session` ("Provisioning Lives in Personal" becomes
    "Personal Is Admin Staff Management, Not Operator Sign-In" plus "Clientes And
    Personal Are Sections Of The Main Window"), `admin-console` ("POS Staff
    Management Section").
  - RED: `PosShellNavigationTests`/`PosShellMarkupTests` failed to compile
    (no `ShellNavigation`/`ShellSection`); `PosStaffViewTests` failed to compile
    (no `PosMessages.CannotDeactivateSelf` etc., then no `SetStatusAsync`,
    `StaffRowPresenter`, `StaffView`). GREEN: `Pos|ApplicationBranding|
    OperatorProvisioning` 324/324.
  - Checks (throwaway worktree): `dotnet build Commerce.sln` 0 errors; full
    `dotnet test tests/Commerce.Integration` 1255 passed, 0 failed, 0 skipped
    (the flaky `PosCompositionRootTests.Build_Resolves_BranchNodeService`
    passed); `npm test` 321/321; `file` checks UTF-8. Render check (throwaway
    harness in the scratchpad, not committed) at 1120x700 in Dark, Light and
    Vaca Verde: Venta, Clientes (prompt and list), Personal (prompt, list, and
    scrolled to the end): nav highlight correct, scrollbars present, nothing
    clipped, status area fixed above the scroll area.
  - Not exercised in a real session: click-through of the WPF handlers (no UI
    harness): owner check of create/deactivate/remove against the running stack.
  - Review of T3 commits: not yet assessed (pending parent).

## Next step
T4 (POS lock screen). Hosting notes: `MainWindow` content area is `SaleScreen` + `SectionHost`; the lock screen can be a third full-window layer (covering the nav and the content) driven by `CurrentOperator`, and `ShellNavigation.Reconcile(null)` already returns the shell to the sale when there is no operator. Replace `OperatorSignInFlow`/`OperatorLoginWindow`/`ProvisionOperatorWindow` (still modal).
