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

- [ ] T1 Migrations: purchase_receptions + lines, stock_movements (append-only, branch RLS), stock minimums, cost history, reception numbering + tests (route: delegated backend writer)
- [ ] T2 API receptions: draft create/edit, confirm (stock + supplier Invoice + cost history, one transaction), void (compensating movements), duplicate-document guard (route: delegated backend writer)
- [ ] T3 API stock: on hand per branch/presentation, movement history, manual adjustment (opening, shrinkage, count correction), minimum levels, low-stock list (route: delegated backend writer)
- [ ] T4 Sync branch -> cloud: project synced POS sale lines into stock movements, idempotent; sale reversals/voids if the payload carries them (route: delegated sync writer)
- [ ] T5 Sync cloud -> branch: stock replica channel to the branch node; POS shows availability and warns on a sale over stock (route: delegated sync writer)
- [ ] T6 Web: receptions list/form/confirm/void; stock screen (on hand, movements, adjustments, minimums, low-stock alerts) (route: delegated web writer)

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

## Next step

T1-T3 backend writer; then T4-T5 sync writer and T6 web writer.
