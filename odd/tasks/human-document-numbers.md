# Human Document Numbers

## Objective
Never show GUIDs to regular users. Give branches a fixed short code, POS
terminals a register number, and POS sales a human number
`V{branch}-C{register}-{seq}` (e.g. `V01-C2-125`), explained in the UI and
documented. Web orders (`P{branch}-W-{seq}`) follow once orders are persisted.

## Problem
- The POS shows the sale GUID after every sale (`MainWindow.xaml.cs:523-524`,
  `828-829`, also "en branch.db") and GUIDs of the organization, branch and
  installation in the identity summary (`MainWindow.xaml.cs:455-466`);
  `SyncRunner.cs:111,122` puts operation GUIDs in failure text. The web shows
  the branch GUID in `BranchesScreen.tsx:75-77`.
- Branches have only `id, name`; terminals have no number; sales have no
  number. POS sales are not projected into any Postgres sales table (only
  `sync_inbox` jsonb). No counter/lock pattern exists in the codebase.
- Web orders live in memory (`CloudOrderStore`, singleton `Dictionary`): a
  restart loses them, so they cannot be numbered reliably yet.

## Why
Owner decisions (2026-09-30):
- GUIDs only for sysadmin technical views; regular users see short codes.
- Format `V01-C2-125` for POS sales and `P01-W-37` for web orders; type letter
  first (V venta, P pedido, later N notas), numeric branch code, origin
  (`C{n}` register / `W` web), sequence. Letters avoid date-like reading;
  origin keeps POS and web separable in reports.
- Branch code: numeric, per organization, assigned by the server at creation,
  never editable (changing it would require rebuilding data).
- The UI must explain the composition (tooltips/placeholders) and the logic
  must be documented.

## Scope (authorized)
- Branch short codes (migration, allocation at creation, backfill, DTOs, web
  display; branch GUID column only for sysadmin).
- Register numbers per branch (durable registry keyed by installation,
  allocation at pairing, backfill, authenticated fetch for already-paired
  terminals, local persistence).
- POS sale numbers (local per-terminal counter in the sale transaction,
  additive payload fields, server projection with uniqueness, ingestion never
  blocked by numbering problems, display + composition tooltip).
- Remove GUIDs from POS operator-facing text.
- Documentation: `docs/document-numbering.md`, specs, `deploy/README.md`.

Pending owner decision: web order persistence + `P` numbers (T6).

## Constraints
- Commit on `dev`; Conventional Commits; no AI attribution; strict TDD.
- Migrations: next is `0021`; keep `deploy/dev/db/init-rls.sql` in sync; add
  new migrations to the test fixture chains (`PostgresTestFixture`,
  `MigrationRlsTests`); RLS with `app.current_org_id`; idempotent.
- Sync contract frozen at v1: payload fields additive, optional, trailing.
- Pre-feature sales/terminals keep NULL numbers; never fabricate or renumber
  history; show nothing (not a GUID) when absent.
- Allocation must be race-free (per-org lock for branch codes, per-branch for
  registers); the POS counter lives in the same SQLite transaction as the sale,
  after the idempotency check, keyed by (branch, register).
- Spanish UI copy; English code/docs; UTF-8.
- Integration tests need Postgres + pgbouncer; full suite from a throwaway
  worktree; never `--artifacts-path`; never `docker compose down`. The running
  dev stack holds DLLs in the main checkout.
- Dev DB has no migration tracking: apply new migrations by hand after merge.

## TDD
Strict (global instructions). Runners: `dotnet test tests/Commerce.Integration
--filter <Class>`, `npm test` in `src/Commerce.Web`.

## Delivery
Forecast about 2000 authored lines over 5 tasks (+T6 if approved); trunk on
`dev`; work-unit commits reviewed under RDD. First review boundary: `18a9046`.

## Tasks
- [x] T1 Branch codes: `branches.code smallint` (unique per org, NOT NULL after
  backfill ordered by `created_at, id`, immutable via trigger), allocation in
  `CreateBranchAsync` and bootstrap under a per-org lock, DTOs
  (`BranchOption`, `BranchSummaryDto`, `SelectableBranch`, device branch
  options) carry the code, web Branches screen shows "Código" and hides the
  GUID column unless sysadmin. Route: delegated direct.
- [x] T2 Register numbers: `terminal_registers(organization_id, branch_id,
  installation_id, register_number)` unique per branch and per installation,
  allocated in `IssueAsync` under a per-branch lock, reused on re-pair to the
  same branch; backfill from live credentials; `DevicePairResponse` +
  `GET /device/identity` return branch code + register; POS persists them in
  `installation.json` and refreshes when missing. Route: delegated direct.
- [ ] T3 POS sale numbers: local `terminal_counters` keyed by
  (branch, register) seeded from local max; number assigned in the sale
  transaction after idempotency; `SaleEffect`/`SalePayloadV1` optional
  trailing fields; server `pos_sales` projection (or equivalent) in the inbox
  transaction with `UNIQUE (organization_id, branch_id, register_number,
  sequence)`; conflicts/missing numbers logged, never block ingestion. POS
  shows "Venta V01-C2-125 registrada" with a composition tooltip; no GUID.
  Route: delegated direct.
- [ ] T4 GUID cleanup in the POS: identity summary shows branch name + code
  and register, no GUIDs; sync failure text without operation GUIDs (log keeps
  them). Route: delegated direct (may join T3).
- [ ] T5 Docs: `docs/document-numbering.md` (format, parts, who assigns,
  offline rules, reports, examples, future types), specs
  (`organization-persistence`, `pos-installation-identity`, `pos-scan-sale`,
  `branch-offline-sync`), `deploy/README.md` migration entries.
  Route: delegated direct (may join each task).
- [ ] T6 (pending decision) Persist web orders in Postgres and number them
  `P{branch}-W-{seq}`.

## Acceptance criteria
- No GUID visible to non-sysadmin users in the POS or the web for branches
  and sales.
- Every new branch has a code; every paired terminal a register; every new
  POS sale a unique `V..-C..-..` number, offline included.
- The UI explains the composition; `docs/document-numbering.md` explains the
  logic.

## Progress
- 2026-09-30: document created after delegated mapping (4-file trigger).
- 2026-09-30: T1 done (delegated direct writer). Commits: `2f1cf98`
  migration `0021_branch_codes.sql` + `BranchCode` + store + init-rls/README;
  `63fc076` DTOs (`BranchOption`, `SelectableBranch`, `BranchSummaryDto`,
  `CreateBranchResponse`, `DeviceBranchOption`, `DevicePairResponse.BranchCode`);
  `c22afcd` web Code column + tooltip, GUID column sysadmin-only, switcher
  `01 · name`; `06d6d71` spec `organization-persistence` "Branch Short Code".
  - RED: `BranchCodesMigrationTests` failed on HEAD (migration missing: 3
    failures); domain/store/endpoint tests failed to compile (no `BranchCode`,
    no `Code` members); web column/tooltip/sysadmin/switcher tests failed (4).
    GREEN after implementation.
  - Checks: `dotnet build Commerce.sln` (worktree) 0 errors; full
    `tests/Commerce.Integration` 1461 passed, 0 failed, 0 skipped; `npm test`
    330 passed; `tsc -b` clean; `npm run build` ok; `npm run lint` only the
    pre-existing warnings; e2e standalone `tsc --noEmit` clean.
  - Decision, allocation: ONE source of truth in the database. A BEFORE INSERT
    trigger (`branches_code_allocate`, function `branches_allocate_code()`) takes `pg_advisory_xact_lock(
    hashtextextended(org_id::text, 0))` and assigns `MAX(code)+1` when the
    insert omits `code`. The app insert (bootstrap and `CreateBranchAsync`)
    omits it and reads it back with `RETURNING code`; raw-SQL test/seed inserts
    keep working. Chosen over app-side `SELECT MAX` + retry (racy, duplicated
    logic) and a counter table (extra state to backfill). Lock is per org,
    transaction-scoped (pgbouncer-safe); branches are never deleted so a
    committed code is never reissued.
  - Decision, immutability: BEFORE UPDATE trigger rejects any change to `code`;
    CHECK 1..999 and `UNIQUE (organization_id, code)` back it up.
  - Decision, type: `Commerce.Domain.Tenancy.BranchCode` (validated 1..999,
    `Format()` = at least 2 digits). DTOs carry the plain `int`; web mirrors the
    formatting in `src/lib/branchCode.ts`.
  - Fixture chains: `0021` added after `0003` in the inline-chain fixtures that
    exercise branches via the store/endpoints; glob-based fixtures pick it up.
  - Pending (parent): apply `0021` by hand to `commerce_dev` before running the
    new API against it.
- 2026-09-30: T1 review follow-ups (lineage review-bbe00c52ade4b9da, approved)
  resolved. Commits: `545f1d4` exhausted branch codes map to `409
  {"error":"branch-codes-exhausted"}` (`BranchCodesExhaustedException`, store +
  endpoint tests), trigger name corrected to `branches_code_allocate` (function
  `branches_allocate_code()`) in README/comments/doc, `hashtextextended`
  collision comment fixed in 0021 and init-rls.sql, spec scenario added;
  `a27ffd1` web fixtures: no `code` on organization objects, codes unique per
  organization. `BranchCode` stays the single .NET formatter and is now used by
  the POS label; the web `branchCode.ts` remains its documented mirror.
- 2026-09-30: T2 done (delegated direct writer). Commits: `9915d16` migration
  `0022_terminal_registers.sql` + init-rls/README + migration tests; `879f32e`
  API (`PostgresTerminalRegisterStore`, `IssueAsync` assigns in the pairing
  transaction, `DevicePairResponse.RegisterNumber`, `GET /device/identity`) +
  store/endpoint tests + fixture chains; `8b434fe` POS (pairing DTOs,
  `installation.json` fields, `DeviceIdentityClient`, `TerminalIdentityRefresher`,
  `TerminalLabel`, identity summary line); `881f9a1` spec "Register Number".
  - RED: `TerminalRegistersMigrationTests` 6 failures (migration missing);
    store/endpoint tests failed to compile (no `RegisterNumber`,
    `PostgresTerminalRegisterStore`, `DeviceIdentityResponse`,
    `RegisterNumbersExhaustedException`); POS tests failed to compile
    (`TerminalIdentityRefresher`). Note: the POS implementation draft existed
    before its tests ran; RED was observed against a clean HEAD worktree. GREEN
    after implementation.
  - Checks: `dotnet build Commerce.sln` (worktree) 0 errors; focused classes
    green (36 + 43); full `tests/Commerce.Integration` 1498 passed, 0 failed, 0 skipped;
    `npm test` 330 passed; `tsc -b` clean.
  - Decision, table: one row per (branch, installation) kept FOREVER with
    `released_at` (history) instead of deleting freed rows. `UNIQUE
    (organization_id, branch_id, register_number)` + `UNIQUE (organization_id,
    branch_id, installation_id)` + partial unique index on `installation_id
    WHERE released_at IS NULL` (one live register per installation). FORCE RLS,
    asymmetric like `device_credentials` (unscoped SELECT/release for the
    cross-org re-pair, INSERT/re-activation pinned to the org); an immutability
    trigger protects identity columns.
  - Decision, reuse policy: NUMBERS ARE NEVER REUSED. Sale numbers are
    generated offline from (branch, register, seq); handing a freed number to
    another installation could duplicate a sale number. Next number is
    `MAX(ever assigned)+1` per branch. An installation returning to a branch it
    already held gets its OWN old number back (re-activates its row), which is
    safe because (branch, register) names one installation forever.
  - Decision, width: 1..999 (not 1..99). Every POS reinstall is a new
    installation and numbers are never freed, so a long-lived branch needs
    headroom; `C{n}` is not zero-padded so the width is free. Exhaustion fails
    `terminal_registers_number_ck` and maps to `409` (`register-numbers-exhausted`
    in pairing and identity); the pairing transaction rolls back whole (old
    credential stays live).
  - Decision, allocation: ONE SQL function `terminal_registers_assign` (advisory
    xact locks, installation then branch, pgbouncer-safe) called by `IssueAsync`
    inside the pairing transaction and by `GET /device/identity`, which also
    covers terminals paired before the feature. Backfill numbers live
    credentials per branch by `issued_at, installation_id`.
  - Decision, POS offline: identity (`BranchCode`, `RegisterNumber`) is stored
    in `installation.json` (nullable for old files). `MainWindow` refreshes it
    fire-and-forget at startup through `TerminalIdentityRefresher` (UI-free; no
    request when complete; offline keeps it unknown; never overwrites a newer
    pairing). The label `Sucursal 01 · Ruta 51 · Caja 2` omits unknown parts and
    never shows a GUID; the settings identity summary now has a "Terminal" line
    instead of the branch GUID (organization/installation GUIDs stay for T4).
  - Notes for T3: sales without a known register must still commit; show a
    friendly "Número pendiente" (no GUID) and assign the number when the
    identity arrives (call `TerminalIdentityRefresher.EnsureAsync` before the
    first sale and retry later); the local counter must be keyed by (branch,
    register) and never created before the register is known. Add the
    `PosMessages` text then. Format with `BranchCode.Format()` and
    `RegisterNumber.Format()` (`C2`).
  - Pending (parent): apply `0022` by hand to `commerce_dev` before running the
    new API against it (needs `0021` first).

## Next step
T3 (POS sale numbers). Read the identity from `DevicePairing.BranchCode/RegisterNumber`; reuse `BranchCode.Format()` and `RegisterNumber.Format()`.
