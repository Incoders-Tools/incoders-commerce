# Purchases, Goods Reception and Stock

## Objective

Record goods received from suppliers in the web console, per branch; on
confirmation move stock, add the invoice to the supplier current account and
keep cost history. Keep stock synchronized with the branch POS in both
directions so the POS does not sell blind and low-stock alerts can drive
restocking.

## Why

No stock concept exists today (no table, no movement, the POS decrements
nothing; `OrderPendingReason.StockUnconfirmed` is a stub). PRD 9.6, 9.8 and
12.4 require receptions that move inventory, update the supplier account and
keep costs and audit; stock must derive from movements (PRD 62, 129, 805,
1045).

## Decisions

- Owner, 2026-10-02: **stock is cloud-authoritative for now** (deviation from
  PRD 724 "Stock de sucursal | Sucursal local"), with synchronization
  guaranteed both ways:
  - branch -> cloud: every synced POS sale decrements cloud stock
    (idempotent per sale line), so cloud stock and alerts stay true;
  - cloud -> branch: a stock replica reaches the branch node / POS, so the
    POS knows availability offline (as of the last sync).
- POS policy v1: a sale over the known stock **warns, it does not block**
  (the replica can be stale offline; PRD 324 asks for a configurable
  negative-stock policy later). Stock may go negative in the cloud and is
  flagged.
- Stock is branch-scoped (RLS on `app.current_branch_id`, pattern of 0016 /
  0017). Products and presentations are already branch-owned (0016).
- Reception lines reference a `presentation`; quantity is units or kg per
  `quantity_behavior` (FixedQuantity / Weighted / Bulk). Lot code and
  expiry are optional free text / date in v1.
- A media res is received as a Weighted line with a lot (compatible with the
  deferred romaneo, PRD 9.24).
- Numbering: `R{branch}-W-{seq}` per docs/document-numbering.md (new
  letter `R`), server-side per-branch counter.
- The ledger is reused as is: confirming a reception posts an `Invoice`
  (Credit) with `due_on = occurred_on + payment_terms_days`; voiding posts
  the compensating `Reversal` in both ledgers.

## Out of scope (later)

Purchase orders, OCR/AI document capture, romaneo/desposte, transfers
between branches, reservations by order, physical count workflow beyond a
simple adjustment, configurable negative-stock policy, linking supplier
price-list mappings to suppliers, dashboard real data (still mock until the
owner says).

## Tasks

- [x] T1 Migrations: purchase_receptions + lines, stock_movements (append-only, branch RLS), stock minimums, cost history, reception numbering + tests (route: delegated backend writer) - `340e180`: `0032_purchase_receptions.sql`, `0033_stock.sql` (mirrored in `init-rls.sql`), `ReceptionRules`/`ReceptionNumber`/`StockRules` pure rules. RED: 16 migration tests failed before the migrations (domain tests: compile error), GREEN: 43 domain + 17 migration tests.
- [x] T2 API receptions: draft create/edit, confirm (stock + supplier Invoice + cost history, one transaction), void (compensating movements), duplicate-document guard (route: delegated backend writer) - `b4f88ca`: `/purchases/receptions` (list, get, draft, put, confirm, void). RED: 17 endpoint tests failed (404) before the code, GREEN: 19 (acceptance 120 kg x 4.000 = 480.000 due by terms, void brings stock and balance back, duplicate document 409, race of three confirms applies once).
- [x] T3 API stock: on hand per branch/presentation, movement history, manual adjustment (opening, shrinkage, count correction), minimum levels, low-stock list (route: delegated backend writer) - `f3d7b55`: `/stock`, `/stock/low`, `/stock/{presentationId}/movements`, `/stock/adjustments`, `/stock/minimums/{presentationId}`. RED: 20 of 40 endpoint tests failed before the code, GREEN: all 40 (incl. minimum -> low-stock list, branch isolation and append-only proofs in the migration tests).
- [x] T4 Sync branch -> cloud: project synced POS sale lines into stock movements, idempotent; sale reversals/voids if the payload carries them (route: delegated sync writer) - `2a9c9d8`: `PosSaleStockProjection` (inbox tx, own savepoint). RED: 4 of 5 new tests failed (stock stayed 0) before the code, GREEN: 5 (2,5 kg weighted line = -2.5, redelivery and re-sent sale under another operation id decrement once, multi-line sale with the sale time, unknown presentation / non-positive quantity skipped with a warning). No sale void/return payload kind exists: documented gap, `PosSaleVoid` reserved.
- [x] T5 Sync cloud -> branch: stock replica channel to the branch node; POS shows availability and warns on a sale over stock (route: delegated sync writer) - `eabd4eb` (cloud: `GET /device/stock/sync`, migration `0034_stock_replica_index.sql`, applied to `commerce_dev`) and `a51e12d` (branch node `stock_replica` + cursor, `StockReplicaClient`, `SyncRunner` pull, POS card stock + as-of + non-blocking warning). RED: 4 of 5 endpoint tests failed (404) and the branch/POS tests did not compile before the code; GREEN: 5 endpoint + 6 store + 4 client/sweep + 14 presentation/markup tests.
- [x] Concept fix - `950ba51`: reception ledger concepts in Spanish ("Recepción de mercadería R01-W-1", "Anulación de recepción R01-W-1: motivo"), asserted in the acceptance test (RED showed the English text).
- [ ] T6 Web: receptions list/form/confirm/void; stock screen (on hand, movements, adjustments, minimums, low-stock alerts) (route: delegated web writer)
- [x] T7 Per-organization number format: quantity input/display follows the org's `quantityDecimalSeparator` (`Comma` | `Dot`) (route: inline, single writer by owner request 2026-10-02)
  - Owner decision: quantity input must adapt to different formats per business; Vaca Verde writes kilos "1.5" (dot decimal). Today `lib/quantity.ts` assumes es-AR and also accepts "2.5", so "1.500" reads as 1500 kg (data-entry hazard).
  - Scope: column on `organizations` (migration 0035, default `Comma`), exposed on `GET /account/organization/settings`, updated by `PUT` (business-admin / sysadmin acting on the org, audited); Vaca Verde seed sets `Dot` only while still default; web `useNumberFormat()` used by every quantity input/display (Dot: digits and one "." only, "," rejected; Comma: "." is thousands only, "2.5" rejected); "Formato de números" select in the organization edit screen. Money stays es-AR.
  - Out of scope / follow-up: the POS (WPF) still uses its own culture for quantities.
  - Done: backend `fb245b2` (migration `0035_organization_settings.sql` mirrored in `init-rls.sql`, `GET/PUT /account/organization/settings`, audit `organization.settings_updated`, Vaca Verde seed sets `Dot` while untouched; applied to `commerce_dev`: Vaca Verde = Dot, other org = Comma); web `f3c316e` (`useNumberFormat()`, `lib/quantity.ts` strict per-format parsing, reception lines/stock adjust/stock list/minimums/movements, `/app/settings` screen + nav). RED: integration project did not compile (missing DTOs), 9 of 11 `quantity.test.ts` failed, provider test unresolved import; GREEN: 112 integration (settings, seed, MigrationRls, AdminConsole, Purchasing migration, StockReplica; none skipped), 528 web tests.
  - Decisions: columns on `organizations` (not a 1:1 table): it already holds branding, RLS and the grant cover new columns, no join; each new setting is a column with a CHECK. Write permission `ManageBranchSettings` (business-admin; sysadmin acting on an organization). The settings screen is new (`/app/settings`, tenant admin nav) because the existing branding form is sysadmin-only and by id. Seed rule: Dot only while the value is still Comma and no `organization.settings_updated` audit row exists. Unit cost stays es-AR money (`parseAmount`). Dot display has no thousands grouping so it reads back as typed.
  - Follow-up: POS (WPF) quantities still use the device culture; the sysadmin has no per-organization-by-id settings endpoint (acts via the organization switcher).

## Acceptance criteria

- Confirming a reception of 120 kg of "Media res" at 4.000/kg for supplier X
  at branch Ruta 51 raises its stock by 120 kg, adds an Invoice of 480.000 to
  X's account due by X's terms, and records the cost.
- Voiding it brings stock and the supplier balance back, both history lines
  visible.
- The same supplier document cannot be confirmed twice.
- A synced POS sale of 2,5 kg lowers cloud stock by 2,5 kg exactly once even
  if the envelope is delivered twice.
- After sync, the POS shows the presentation's stock and warns (not blocks)
  when a sale exceeds it.
- A presentation below its minimum appears in the low-stock list.

## Constraints

- Clean domain (pure rules in `src/Commerce.Domain`), Npgsql stores, minimal
  APIs; forward-only idempotent migrations appended to
  `deploy/dev/db/init-rls.sql`; sync contract v1 is additive-only.
- TDD: Strict (RED -> GREEN -> REFACTOR), source: global config. Runners:
  `dotnet test`; `npm test`, `npm run lint`, `npm run build` in
  `src/Commerce.Web`.
- Commit straight to `dev`, Conventional Commits, no AI attribution.

## Progress

- Feature document created 2026-10-02.
- T1-T3 done by the backend writer (see the task lines). Decisions taken while implementing:
  - Permission: reused `ManageUsers` (the supplier guard); the roles model has no purchasing permission and adding a flag needs a roles migration. Revisit when stock/purchasing roles are split from user management.
  - Reception number is assigned at CONFIRM (drafts have none), `R{branch}-W-{seq}` under a per-branch advisory lock (seed 4); a voided reception keeps its number.
  - Stock idempotency key for T4: `stock_movements` UNIQUE (organization_id, branch_id, source_type, source_line_id); `StockMovementWriter.InsertAsync(..., ignoreDuplicateSourceLine: true)` makes redelivery a no-op. Receptions use source_type `PurchaseReception` (void: `PurchaseReceptionVoid`); sales should use `PosSale` (void/return: `PosSaleVoid`), source_id = sale id, source_line_id = sale line id.
  - A confirmation with total 0 moves stock and records cost but posts no Invoice (the ledger needs amount > 0); void then skips the ledger reversal. If the Invoice was reversed by hand from the supplier account, void still reverses the stock and leaves `ledgerReversalMovementId` null.
  - A minimum can be cleared (`minimumQuantity: null`): `stock_minimums` also grants DELETE. Minimum 0 flags negative stock.
  - Ledger concepts are English ("Goods reception R01-W-1", "Void of goods reception ..."): the web shows them raw.
- T4-T5 done by the sync writer. Decisions:
  - Sale line key: payload lines carry a per-sale `LineNumber`, not a global id, so `source_line_id` = first 16 bytes of SHA-256("pos-sale-line:{saleId}:{lineNumber}"); unique key makes redelivery (even under a new operation id) a no-op. Movement `occurred_at_utc` = sale time (`NewStockMovement.OccurredAtUtc`, default now()).
  - Unknown presentation or non-positive quantity: that line is skipped with a warning; other lines and the sale are still ingested. The whole projection runs in its own savepoint after the `pos_sales` projection and never blocks ingestion.
  - Stock replica: cursor column `stock_movements.created_at_utc` (index added by 0034), server re-reads 5 minutes before the cursor (created_at is the writing tx start) because items are absolute snapshots, so overlap is idempotent; nothing is stored per presentation. A presentation that never moved has no row: unknown, never warned about. The "as of" label is the stock cursor (last successful pull), true for unchanged rows too.
  - POS: card shows "Stock: X [kg]" + "al dd/MM HH:mm"; a line over the known stock shows a red warning on the card and in the scan message ("... la venta no se bloquea"); the sale is never blocked. Cards re-read the replica after every sweep.
  - Not covered: no sale void/return sync payload (no compensating stock movement yet); no local decrement between pulls (two terminals of a branch each see stock as of their last pull); the scan-message warning omits the "kg" label when the card is not on screen.
  - Full `dotnet test`: Integration 1986 passed, 2 failed (the known `PublicRateLimitTests...IsUnreachable_AndAppStillStarts`, plus `ScannedSaleTests.CommitScannedSaleAtomically...` which failed once with a SQLite "database is locked" in pool clearing and passes in isolation: flaky), Upgrade 123, Bootstrap 1.

## Next step

T6 web (in progress by the web writer); then verify the acceptance criteria end to end.
