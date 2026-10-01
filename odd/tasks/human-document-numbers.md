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
- [x] T3 POS sale numbers: local `terminal_counters` keyed by
  (branch, register) seeded from local max; number assigned in the sale
  transaction after idempotency; `SaleEffect`/`SalePayloadV1` optional
  trailing fields; server `pos_sales` projection in the inbox transaction with
  a partial `UNIQUE (organization_id, branch_id, register_number,
  sale_sequence)`; conflicts/missing numbers audited, never block ingestion. POS
  shows "Venta V01-C2-125 registrada" with a composition tooltip; no GUID.
  Route: delegated direct.
- [x] T4 GUID cleanup in the POS: the settings identity block shows the terminal
  label (`Sucursal 01 · Ruta 51 · Caja 1`), the pairing operator and the current
  operator, no organization/installation GUIDs; the sync summary is Spanish via
  `PosMessages` and names no operation (the log keeps the ids); the lock screen shows the
  terminal label with a composition tooltip; source guard test. Route: delegated direct.
- [x] T5 Docs: `docs/document-numbering.md`, linked from `docs/architecture/README.md` and the
  four specs (`organization-persistence`, `pos-installation-identity`, `pos-scan-sale`,
  `branch-offline-sync`), `deploy/README.md` entries for 0022/0024. Route: delegated direct.
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
Owner manual verification, then start `odd/tasks/persist-web-orders.md` (T6: persist web orders,
number them `P{branch}-W-{seq}`).
1. Apply by hand to `commerce_dev`, in order: `0021`, `0022`, `0023`, `0024` (parent does this).
2. Restart the API and the POS (run-all.ps1). Pair or re-open a terminal: the lock screen shows
   `Sucursal 01 · <branch> · Caja N` and its tooltip explains it.
3. Make an offline sale: the result line reads "Venta V01-CN-1 registrada ..." (tooltip explains the
   parts); sync it and check `pos_sales` and that settings > identity shows no GUID.
4. Web: Branches shows "Código" (hint) and the GUID column only for the sysadmin.
Review boundary for the next native review: `94551a3` was the end of slices A and B; the T3
follow-ups, T4 and T5 are commits `3ef3973`..`6d7015b`.
Follow-up for the orders feature: `StaffOrderScreen.tsx` still takes a raw branch GUID (staff/testing).
- 2026-10-01: T2 review follow-ups (lineage review-2bc7f10bf36ab9f8, approved) and T3 done (single
  writer, delegated direct). Commits: `cc7e79c` identity refresh never releases (SQL function takes
  `p_release_others`, re-verifies a live credential under the installation lock, returns NULL otherwise;
  `DeviceCredentialNotLiveException` -> 401), each NEW allocation audited as `terminal.register.assigned`,
  lock seeds documented, the earlier 3-argument overload dropped (0022 edited in place: not yet applied
  anywhere but the test DB); `aa189aa` `/device/pair` per-IP rate limit
  (`DeviceRateLimitPolicies.Pair`, default 30 per 15 min, `RateLimits:DevicePairPermitLimit`),
  `/health/ready` also checks `branches.code`, `terminal_registers` (forced RLS) and the 4-argument
  assign function and logs "migration 0021/0022 missing", one typed 409 body
  `RegisterNumbersExhaustedResponse` for pairing and identity, deploy order in `deploy/README.md`;
  `49e21ec` POS maps a 409 only when the body says `register-numbers-exhausted`, and
  `TerminalIdentityRefresher.EnsureAsync` returns `TerminalIdentityRefresh(Pairing, Persisted)` that
  `MainWindow` applies only when persisted and still current; `495de8e` T3 domain + local store;
  `e32254d` T3 POS message and wiring; `92dcdfd` migration `0023_pos_sales.sql` + projection;
  `0d28c69` specs.
  - RED: follow-ups: tests failed to compile on HEAD (no `DeviceCredentialNotLiveException`,
    `RegisterNumbersExhaustedResponse`, `DeviceRateLimitPolicies`, `TerminalIdentityRefresh`); T3: tests
    failed to compile (`SaleNumber`, `SaleNumbering`, 3-argument `TryApplyInbound`). The new tests were
    copied into a clean worktree of HEAD for the observation. GREEN after implementation (one test, a
    stale live row in another branch, was dropped: that state is unreachable because only pairing
    changes credentials and pairing releases in the same transaction).
  - Checks: `dotnet build Commerce.sln` (worktree) 0 errors; focused classes green; full
    `tests/Commerce.Integration` 1574 passed, 0 failed, 0 skipped; UTF-8 verified with iconv on every
    changed file; `0023` is a verbatim substring of `init-rls.sql`.
  - Decision, sale numbering: `SaleNumber` (domain, `Format`/`Parse`/`TryParse`, sequence unpadded,
    branch two digits minimum) and `SaleNumbering(BranchCode, RegisterNumber)` (the terminal identity).
    `BranchSyncStore` takes the next sequence of `terminal_counters(branch_id, register_number)` with one
    `INSERT .. ON CONFLICT DO UPDATE .. RETURNING`, seeded from `MAX(sale_sequence)` of that pair, AFTER
    the idempotency and open-cash-session checks, then stamps the effect and re-serializes the queued
    `SalePayloadV1` (additive optional `BranchCode`, `RegisterNumber`, `SaleSequence`). `sale_effects` gets
    nullable `branch_code`, `register_number`, `sale_sequence`. Unknown register -> no number, no counter.
  - Decision, server: `pos_sales` is append-only (SELECT, INSERT), FORCE RLS, composite FK to branches,
    CHECK that register and sequence are both present or both absent, partial unique number index.
    `ICloudInboxStore.TryApplyInbound` gained an optional `installationId` (from the device claims, via
    `CloudSyncReceiver.Receive`). `PosSaleProjection` validates (complete/in range, branch code, registry
    row for installation+branch+register released rows included, number unused) and uses
    `INSERT .. ON CONFLICT DO NOTHING`; any rejection stores the sale unnumbered plus an audit row
    `sale.number_conflict` (reason in the payload). It runs in a savepoint: a missing `pos_sales` table
    (API deployed before `0023`) or any projection error only logs a warning and the sale is still
    ingested. For that reason `/health/ready` does NOT require `pos_sales`.
  - Decision, UI: `SaleResultMessage` (UI-free) builds "Venta V01-C1-125 registrada por $X (Efectivo)."
    and, while pending, "Venta registrada por $X (Efectivo, número pendiente)." (the tender is kept, a
    deviation from the literal text of the request), "La venta V01-C1-125 ya estaba registrada..." for
    retries; the result `TextBlock` carries the composition tooltip. The sale path still never awaits the
    network (existing markup tests forbid it): when the identity is unknown the sale commits unnumbered
    and `RetryTerminalIdentityIfUnknown` starts one background refresh so the NEXT sale is numbered,
    instead of awaiting `EnsureAsync` before the first sale; the startup refresh already covers the
    normal case.
  - Not applied: the `MainWindow.xaml.cs:171` suggestion (the finding text was not available to the
    writer; that line is the fire-and-forget identity refresh, which was reworked anyway).
  - Residual risks: rotating-IP register exhaustion by an authenticated pairer is limited and audited,
    not prevented; sales received before `0023` is applied have no `pos_sales` row (they stay in
    `sync_inbox`); an unnumbered sale is never numbered later.
- 2026-10-01: T3 review follow-ups, T4 and T5 done (single writer). Slice A review
  (review-8e9d46f0f96f5e2e, c26310b..e32254d) and slice B (review-ce02774ce3c02940, e32254d..94551a3)
  were both approved and acknowledged. Commits: `3ef3973` perf(pos) sale counter bumped by one
  indexed UPDATE and seeded from MAX(sale_sequence) only when its row is missing, new index
  `ix_sale_effects_number (branch_id, register_number, sale_sequence)` (finding A: MAX subquery twice
  per sale under the write gate); `059d027` fix(api) projection savepoint contains ANY
  non-cancellation exception (finding B), audited as `sale.projection_failed` (not for a missing table),
  migration `0024_terminal_registers_assign_result.sql` (`terminal_registers_assign` returns
  `assigned_number, newly_allocated`; finding A "assigned_at = now()" inference removed),
  `/health/ready` checks the new result shape, constants for the exhausted code
  (`RegisterNumbersExhaustedException.ErrorCode`), `RegisterNumber.Prefix`, `AuditActorKinds`; test that
  `GET /device/identity` audits the allocation as actor kind `device` (RLS accepts it: `actor_kind` has
  no constraint and the policy only pins the organization); `8314117` feat(pos) T4; `6d7015b` docs T5.
  - RED: `SaleNumberingCommitTests.TheSeedLookup_IsCoveredByAnIndex` failed on a clean HEAD worktree
    (1 failure); the API tests (`PosSalesProjectionTests`, `DeviceEndpointTests`,
    `TerminalRegistersAssignResultMigrationTests`) and the POS tests (`PosOperatorTextTests`,
    `RunSyncAsyncTests`) failed to compile on HEAD (no `FaultInjection`, `AssignedAction`,
    `AuditActorKinds`, `IdentitySummary`, `PosMessages.Sync*`). GREEN after implementation.
  - Checks: `dotnet build Commerce.sln` (worktree) 0 errors; focused classes 155 and 94 passed; full
    `tests/Commerce.Integration` 1587 passed, 0 failed, 0 skipped; UTF-8 verified with iconv on every
    changed file; web untouched (no `npm test`).
  - Decision, 0024: a new migration instead of editing 0022 (already applied on dev and the test DB).
    The function cannot change its return type in place, so 0024 drops and re-creates it; 0022 now also
    drops the 4-argument signature first so fixtures that replay the whole chain end with the 0024 shape
    (replaying 0022 after 0024 failed with 42P13 otherwise). The fault-injection seam
    (`PosSaleProjection.FaultInjection`, `InternalsVisibleTo("Commerce.Integration")`) exists only so a
    test can raise a non-Postgres exception inside the guarded region.
  - Decision, T4: the main window has no header terminal label; the label lives on the lock screen
    (branch eyebrow) and in settings. The lock screen now shows the full label with the composition
    tooltip (`Sucursal 01 = código de sucursal · Caja 1 = número de esta caja`). "Sync state" stays in the
    existing settings status block and footer. The `SaleResultMessage` tooltip text and the web "Código"
    hint were verified unchanged. Web: no GUID is shown to non-sysadmins (the branch id column is
    sysadmin-only; the web has no sales screen).
  - Not done: `SaleNumber`'s regex still spells `V`/`C` literally (an attribute cannot use the
    constants); actor-kind literals elsewhere in the codebase (`"org-user"`) were not migrated, only the
    new code uses `AuditActorKinds`.
- 2026-10-01: final numbering review follow-ups (single writer). Lineage review-fc64e8d1fa77a316
  (approved and acknowledged, next boundary 7f38033). Advisories and resolutions:
  1. Process-wide `FaultInjection` static replaced by an optional constructor seam
     `PostgresCloudInboxStore(..., projectionFault)` passed to `PosSaleProjection.ProjectAsync`
     (`dd13d3f`). `InternalsVisibleTo("Commerce.Integration")` stays: tests still read
     `PosSaleProjection.FailureAction`/`ConflictAction`.
  2. The "is migration 0023 applied?" hint is logged only for UndefinedTable (42P01); other failures log
     the SqlState or exception type plus message (`492526e`, tests capture the warning).
  3. Readiness negative test: a 0022-style scalar `terminal_registers_assign` reports 503 and logs
     `migration 0021/0022/0024 missing` (`78c9d4f`). Characterization test: the behavior already existed,
     so it had no RED.
  4. 0024 deploy window documented in `deploy/README.md` and `docs/document-numbering.md` (`01f08cb`):
     not backward compatible, apply 0024 and the matching API together. API tolerance not attempted.
  5. `BranchSyncStore.NextSaleSequence` now bumps to `MAX(counter, highest stored sequence) + 1` in the
     single UPDATE (one seek on `ix_sale_effects_number`) (`79ce93e`).
  - RED: seam test did not compile (no `projectionFault`); the log assertion failed on the old message;
    `ACounterBehindTheStoredSales_IsReconciledToTheHighestStoredSequence` failed before the fix. GREEN after.
  - Checks: `dotnet build Commerce.sln` (worktree) 0 errors; `PosSalesProjectionTests` 16 passed,
    `PostgresReadinessHealthCheckTests` 18 passed, `SaleNumbering*` 13 passed; full
    `tests/Commerce.Integration` 1589 passed, 0 failed, 0 skipped; iconv UTF-8 clean on changed files.
