# Staff Roles and POS Operator UX

## Objective
Make staff creation assign branches strictly and identically from the web and
the POS, add a distinct POS cashier role enforced by a new permission, and
make the POS operator experience robust: friendly Spanish errors with a
technical log, busy states during requests, and a logged-operator menu
(switch operator, sign out) with operator provisioning moved to "Personal".

## Problem
- `src/Commerce.Web/src/screens/UsersScreen.tsx:72` sends `branchIds: []`
  always; `POST /account/users` accepts an empty list, so a staff user created
  from the web (e.g. `montes_patricio@yahoo.com.ar` in Vaca Verde, created
  with "Ruta 51" selected) gets `branch_scope = {}` and can never use a POS
  (`/device/operators/verify` 403 `branch-not-in-scope`). A unit test locks
  the empty list in. No endpoint edits a user's branches, so the account
  cannot be repaired from any UI.
- Roles: `seller` (field salesperson taking orders on the web, `ViewSales`)
  is the only non-admin staff role; the POS enforces no permission, so any
  in-scope user can operate the till. There is no cashier role.
- POS: `OperatorProvisioningClient.VerifyAsync`/`GetStatusAsync` call
  `ReadFromJsonAsync` without checking the status, so an empty-body 401
  throws an unhandled `JsonException` from an `async void` handler and kills
  the app (observed 2026-09-30). No logging exists in the POS. Pairing and
  login windows neither disable inputs nor show progress while awaiting.
  Messages are English. The logged-operator nav button reopens the
  provisioning/login window; `CurrentOperator.Clear()` has no UI (no sign
  out).

## Why
Owner decisions (2026-09-30): the backend must be equally strict for web and
desktop because both use the same API; cashier is a real, different role from
seller (option A: new `OperatePos` permission required by the POS); errors
must be logged for technical review and shown to users in friendly language;
the logged-user button must show user options, and adding operators belongs
in "Personal".

## Scope (authorized)
- API: staff users require at least one branch of the organization on create;
  new endpoint to replace a user's branch scope; user listing exposes branches.
- Domain/API: `Permission.OperatePos`; `cashier` role; POS device operator
  endpoints require `OperatePos`; existing stored roles upgraded if
  permissions are persisted per row.
- Web: branch selection on create (defaults to the selected branch), branch
  editing for existing users, cashier role, Spanish role labels.
- POS: typed outcomes for every HTTP status, file logger, global unhandled
  exception handler, friendly Spanish messages, busy states, logged-operator
  menu with switch/sign-out, provisioning moved to "Personal".

Out of scope: repairing `montes_patricio` data (the owner reassigns it from
the web after T3), release/versioning work.

## Constraints
- Commit directly on `dev` (owner workflow); Conventional Commits; no
  feature branch, no PR per task. `main` only through a promotion PR.
- Strict TDD: observed RED before GREEN, then refactor.
- Spanish operator/user-facing copy through existing i18n (web) or Spanish
  literals (POS); code, identifiers, docs and commits in English.
- Keep `DynamicResource` palette keys in the POS (Dark, Light, Vaca Verde).
- Integration tests need `incoders-commerce-postgres-1` and
  `incoders-commerce-pgbouncer-1` up (otherwise they skip green); run the full
  .NET suite from a throwaway worktree (`git worktree add $TEMP/wt HEAD`) when
  the local stack holds the DLLs; never `--artifacts-path`; never
  `docker compose down`.
- DOM-structural web changes may break E2E (`src/Commerce.Web/e2e/`, runs only
  in CI): grep selectors and typecheck `e2e/` with standalone `tsc --noEmit`.

## TDD
- Mode: strict, source: global Claude Code instructions ("Strict TDD Mode:
  enabled").
- Runners: `dotnet test tests/Commerce.Integration` (xunit, filters by
  class), `npm test` in `src/Commerce.Web` (vitest).

## Delivery
- Forecast: about 1500 authored changed lines across 5 tasks. The owner's
  trunk-on-`dev` workflow replaces chained PRs; slices are work-unit commits
  on `dev`, reviewed per commit under RDD (`gentle-ai review assess
  --committed-only --base-ref <last reviewed boundary>`).
- First review boundary: `dd105b4`.

## Tasks
- [x] T1 API: strict staff branch scope. `POST /account/users` rejects a
  staff user (no `customerId`) with no branch (400, typed status) and
  branches outside the org; new `PUT /account/users/{id}/branches` (ManageUsers,
  same validation, grant cap consistent with roles); `GET /account/users`
  returns branch ids. Integration tests. Route: delegated direct.
- [x] T2 Domain/API: `Permission.OperatePos`, `cashier` role
  (`OperatePos`), `business-admin` gains `OperatePos`, sysadmin full set;
  `/device/pair` and `/device/operators/verify` require `OperatePos` (typed
  403), operator status reports inactive without it; upgrade persisted role
  permissions if roles store them; update `openspec/specs/pos-operator-session`.
  Route: delegated direct.
- [ ] T3 Web: Users screen branch picker (default selected branch), edit
  branches, show branches, `cashier` role, Spanish role labels via i18n;
  fix the test that locked `branchIds: []`; E2E selector check.
  Route: delegated direct.
- [ ] T4 POS robustness: typed outcomes for all statuses in device clients,
  file logger under `%LOCALAPPDATA%\Incoders\Commerce\logs`, global
  unhandled-exception handler (log + friendly dialog, no crash on handler
  exceptions), friendly Spanish messages (incl. the new `OperatePos` 403),
  busy state (disabled inputs + progress) in pairing and login.
  Route: delegated direct.
- [ ] T5 POS operator menu: logged-operator button opens a menu with the
  active operator, switch operator (PIN picker) and sign out
  (`CurrentOperator.Clear()`); provisioning a new operator moves to
  "Personal". Route: delegated direct.

## Acceptance criteria
- A staff user cannot be created or left without a branch from any client;
  the API returns the same typed error to web and POS.
- A `seller` cannot sign into the POS; a `cashier` and a `business-admin` can.
- No HTTP response shape crashes the POS; every failure is logged with
  technical detail and shown in friendly Spanish.
- Pairing and login inputs are disabled with visible progress while a request
  is in flight.
- The logged-operator button never opens provisioning; sign out works.

## Progress
- 2026-09-30: document created after mapping (route: delegated mapping,
  4-file trigger). No code changes yet.
- 2026-09-30: T1 done, commit `4ae703c` (route: delegated direct, one writer).
  - RED: 11 new tests failed against the unchanged API (empty branches
    accepted, branches endpoint absent, list without `branchIds`); GREEN after
    implementation. Focused run (Account, SystemAdminOrganizationScope,
    UserAccountStore, RoleTaxonomy, PosStaffManagement, CustomerRegistry):
    91 passed, 0 skipped. Full `Commerce.Integration` from a throwaway
    worktree: 1067 passed, 0 failed, 0 skipped. `npm test`: 298 passed
    (web untouched; `UsersScreen.test.tsx` still locks `branchIds: []`, T3).
    `dotnet build Commerce.sln`: 0 errors.
  - Decisions: staff = no `customerId`; customer-linked users keep an empty
    scope. Typed 400 bodies use the Catalog-style `{ "error": <code> }`:
    `branch-required` (empty/omitted) and `branch-not-in-organization`.
    Caller cap: a caller may only assign branches inside their own
    `BranchScope` (403, mirrors the role grant cap and the Catalog copy
    precedent); a sysadmin acting on an org may assign any branch of it.
    Organization check runs before the cap. `PUT /account/users/{id}/branches`
    (`{ "branchIds": [...] }`) answers 204, 404 for a foreign/unknown user,
    400 for a customer-linked target, audited `user.branches.assigned`; no
    `session_version` bump (roles endpoint does not either; scope is read
    fresh per request). `GET /account/users` items gain `branchIds`.
  - Note for T3: a branch created by an admin is NOT added to the creator's
    own `branch_scope`, so a business-admin cannot assign a branch they do
    not hold (403) until an existing scope covers it.
  - POS `UserAdminClient` still compiles and works (sends `[_branchId]`,
    ignores the extra `branchIds` field); no POS change.
  - Spec: new "Mandatory Staff Branch Scope" requirement and `branchIds` on
    the listing in `openspec/specs/user-credentials/spec.md`.
  - Review: RDD assess `dd105b4..bdc3095` medium, 664 lines, due
    (`slice_budget_reached`); owner GRANTED; one lens (reliability) approved,
    acknowledged (lineage `review-e81f699502f8ae3c`, authority burned). Next
    boundary `bdc3095`. Advisory, non-blocking: WARNING
    `CustomerRegistryTests.cs:732` customer-target PUT test is not
    discriminating (random branch id also yields 400); SUGGESTION
    `ReplaceBranchScopeAsync` ignores affected rows and still audits;
    SUGGESTION sysadmin PUT branches path uncovered. Folded into T2 as test
    hardening.
- 2026-09-30: T2 done, commits `9a43ee1` (test hardening) and `a38e044`
  (feature) (route: delegated direct, one writer).
  - RED: domain tests failed to compile (no `OperatePos`/`Cashier`); then 6
    behavioral failures against unchanged endpoints/migration (seller verify
    and pair answered 200, status `active`, migration file absent, client
    status unmapped); bootstrap-admin test failed against the old 15 literal.
    GREEN after implementation. Test hardening was added alongside already
    shipped T1 behavior (zero-row store test is new behavior).
  - Decisions: `OperatePos = 1 << 4`; `cashier = OperatePos` only (nothing in
    the API or POS checks `ViewSales`; the POS gates only `ManageUsers` for
    the customer screen); `business-admin` gains `OperatePos`; `seller`
    unchanged; `RoleCatalog.BusinessAdminPermissions` constant is now used by
    the catalog and by both bootstrap paths (`POST /account/organizations`,
    self-bootstrap) and the E2E seed, so a new organization's admin is not
    locked out. Grant cap needed no change (subset math handles cashier).
    `/device/pair` also requires `OperatePos` (pairing is done with an
    operator account and the issued credential authorizes the terminal; no
    spec reason otherwise), checked after the password is proven and before
    the branch checks; 403 `operator-not-permitted`. `/device/operators/verify`
    same 403 after credentials, before the branch check. Status reports
    `inactive` without the bit. Sysadmins never reach device endpoints in
    practice (no roles, empty branch scope) and are not elevated there.
  - Migration `deploy/db/migrations/0020_operate_pos_permission.sql`
    (bitwise OR 16 on every persisted `business-admin` entry, FORCE RLS
    toggled off inside the transaction like 0006, post-condition assert,
    idempotent); test fixtures glob all migrations so it is picked up.
  - POS: both clients map `operator-not-permitted` to Failed with the Spanish
    message ("...asigne el rol Cajero."); T4 does the broad rework.
  - Spec: `pos-operator-session` "No Permission Gating Introduced" replaced by
    "Operating The POS Requires OperatePos" (sale commit still unchecked);
    `user-credentials` catalog gains cashier/OperatePos scenarios.
  - Hardening: `ReplaceBranchScopeAsync` returns the affected count (no audit
    on zero, endpoint 404); customer-target PUT test uses a real in-scope
    branch and asserts body and no audit row; sysadmin-acting PUT branches
    covered.
  - Checks: focused run 182 passed, 0 skipped; full `Commerce.Integration`
    from a throwaway worktree 1083 passed, 0 failed, 0 skipped;
    `Commerce.Upgrade` 123 passed; `Commerce.Bootstrap.Tests` 1 passed;
    `npm test` 298 passed (web untouched); `dotnet build Commerce.sln` 0
    errors.
  - Note for T3: role list must include `cashier` (label "Cajero"); a seller
    or cashier cannot be told apart by permissions int alone: cashier=16,
    seller=1, business-admin=31.

- 2026-09-30: T2 review. RDD assess `8b08a7d..b9c9374` high (process-starting
  test code), 687 lines; owner GRANTED; four lenses, approved and acknowledged
  (lineage `review-8a0110c1de1f25c6`, authority burned). Next boundary
  `b9c9374`. Parent applied two confirmed advisory warnings inline in
  `ca00f8c`: `DevicePairingClient.cs` was saved as Windows-1252 (re-encoded to
  UTF-8), and the 0020 post-condition ran after FORCE RLS was restored (moved
  before it). Focused check `OperatePosMigration|OperatorProvisioning|
  DevicePairing|DeviceEndpoint|RoleCatalog` in a throwaway worktree: 43/43,
  0 skipped. Remaining advisories: seller operators already provisioned are
  deprovisioned at the next status check (intended by the owner's option A;
  T4 must show the friendly message); migration test runs as an RLS-bypassing
  owner (does not exercise the hazard); cross-client message constant coupling
  (T4 may centralize POS messages); misleading `RoleCatalogTests` name; the
  PUT-branches 404 path unproven; deploy ordering note (apply 0020 before
  shipping the API).

## Next step
T3 (Web: Users screen branch picker, branch editing, `cashier` role, Spanish role labels).
