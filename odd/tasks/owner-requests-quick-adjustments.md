# Owner Requests: Quick Adjustments

## Objective

Six small changes the owner asked for on 2026-10-11 (items 1, 2, 4, 5, 8 and 10
of the request): a location reference for customers, less visual noise in the
lookup tables, a discount helper, a price-list fix, the known staff positions,
and a sortable stock table.

## Owner decisions (2026-10-11)

- Lookup tables: "Clave" and "Orden" are hidden in the table AND the form; the
  key is generated from the name and the order is alphabetical.
- Staff positions: Carnicero, Chofer, Vendedor, Cajero, plus "Titular" for the
  owner and "Encargado" for his children who work in the business.

## Scope

1. Customers: a free-text location reference ("Referencias", e.g. "Casa gris
   con 2 árboles al frente"), edited and shown in the web and the POS.
2. Lookup tables (business types, supplier categories, treasury account types,
   staff positions, product categories where it applies): no "Clave" or
   "Orden" columns or fields; the server derives the key from the name and
   lists alphabetically.
3. Customers' discount percentage: a placeholder and a help text saying how to
   type it (a number from 0 to 100, decimals allowed, without "%"), in the web
   and the POS.
4. Price lists (`/app/price-lists`): the "Presentación" column shows nothing;
   it must show the presentation the way the catalog does (e.g. "kg" for
   beef, chicken, pork).
5. Staff positions seed: the six positions above, idempotent, for Vaca Verde.
6. Stock table: sortable by clicking a column header, in particular by the
   quantity, to spot low stock.

## Constraints

- Business rules on the server; the web and the POS only render
  (AGENTS.md). Anything present in both layers follows
  `skills/cross-layer-parity` and updates `docs/architecture/cross-layer-parity.md`.
- Migrations: forward-only, idempotent, appended verbatim to
  `deploy/dev/db/init-rls.sql`.
- TDD: Strict (RED -> GREEN -> REFACTOR). Postgres-backed suites only count
  with `incoders-commerce-postgres-1` healthy before and after the run.
- Commit straight to `dev`, Conventional Commits, no AI attribution.

## Tasks

- [x] Q1 Customer location reference (server + web + POS + parity map). The field already existed end to end as "Notas de entrega" (`customers.delivery_notes`, the text the deliveries screen shows the driver as "Indicaciones"); no new column. Renamed "Referencias de ubicación" in the web and the POS, with the example.
- [x] Q2 Lookup tables without "Clave"/"Orden" (server derives the key, alphabetical order; web forms and tables). The four `MasterDataScreen` tables (business types, supplier categories, treasury account types, staff positions). The server already derived the key from the name when omitted and kept it on edit; it now lists by `lower(name)` instead of `sort_order` (the column stays, unused for ordering). The web no longer shows or sends key/order (deactivate included); a key collision reads as "same name". Product categories keep their own "Orden": it is the POS rail order, a real feature. RED: the list test expected the stored order, 3 web tests failed; GREEN: 126 server tests (lookups, suppliers, treasury, employees, payroll, seed), 10 web.
- [x] Q3 Discount percentage helper (web + POS, same wording). Found a real bug behind the confusion: the POS read the discount with the invariant culture, so "7,5" was stored as 75 (and "1,000" as 1000), and nothing on the server limited it, while the discount is applied to the customer's prices at the POS. Now `CustomerDiscountRules` (0-100, two decimals) on create and update (400 `discountPercentage`), the POS reads a decimal comma as a decimal and accepts "10%", and both layers show "De 0 a 100, sin el signo %. Admite decimales: 7,5." RED: domain/POS did not compile, the endpoint accepted 120/-1/7.555 (control run without the rule: 3 failures); web tests failed against the previous form (control). GREEN: 131 server/POS tests, 41 `CustomerForm` tests. No stored discount violated the range in `commerce_dev`.
- [x] Q4 Price lists "Presentación" column, same criterion as the catalog. Not missing data: the editor showed the presentation only when the product had more than one (a test locked it in), and all 90 presentations in `commerce_dev` are the only one of their product ("Por kg"). The column now shows it unless it repeats the product name, like the catalog. Price lists are web-only ("One layer" in the parity map). RED: the updated expectation failed; GREEN: 56 price tests.
- [ ] Q5 Staff positions seed (Titular, Encargado, Carnicero, Chofer, Vendedor, Cajero).
- [ ] Q6 Sortable stock table.

## Follow-ups

- [ ] F1 Q2 review (`review-7d851e84e4e82239`, approved): the treasury
  accounts list still groups by its account type's `sort_order`
  (`PostgresTreasuryStore.cs` ORDER BY), and a type created from the web now
  always gets 0, so its accounts would sort before "Efectivo". Order those
  groups alphabetically too, or give new types the next order.

## Progress

- Feature document created 2026-10-11.
- 2026-10-11: Q1 + Q3 committed (`114f626`, `review-be841c3d19dfee29`,
  approved). Its review found two more: the web field was a number input,
  which turned "7,5" into 75 too (RED: "expected 75 to be 7.5"), and a
  long digit run overflowed the POS parse (`OverflowException` in a UI
  handler). The web now reads the discount as text with `lib/percentage.ts`
  `parsePercentage` (same reading as the POS; unreadable text stops before
  the API with the help text) and the POS caps the whole part at three
  digits.
- Builds and tests run with `--artifacts-path artifacts` (git-ignored)
  while the owner's local stack is running: its API and POS lock their
  `bin` folders.
