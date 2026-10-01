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
- [ ] T1 Branch codes: `branches.code smallint` (unique per org, NOT NULL after
  backfill ordered by `created_at, id`, immutable via trigger), allocation in
  `CreateBranchAsync` and bootstrap under a per-org lock, DTOs
  (`BranchOption`, `BranchSummaryDto`, `SelectableBranch`, device branch
  options) carry the code, web Branches screen shows "Código" and hides the
  GUID column unless sysadmin. Route: delegated direct.
- [ ] T2 Register numbers: `terminal_registers(organization_id, branch_id,
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

## Next step
T1.
