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
- [ ] Q2 Lookup tables without "Clave"/"Orden" (server derives the key, alphabetical order; web forms and tables).
- [x] Q3 Discount percentage helper (web + POS, same wording). Found a real bug behind the confusion: the POS read the discount with the invariant culture, so "7,5" was stored as 75 (and "1,000" as 1000), and nothing on the server limited it, while the discount is applied to the customer's prices at the POS. Now `CustomerDiscountRules` (0-100, two decimals) on create and update (400 `discountPercentage`), the POS reads a decimal comma as a decimal and accepts "10%", and both layers show "De 0 a 100, sin el signo %. Admite decimales: 7,5." RED: domain/POS did not compile, the endpoint accepted 120/-1/7.555 (control run without the rule: 3 failures); web tests failed against the previous form (control). GREEN: 131 server/POS tests, 41 `CustomerForm` tests. No stored discount violated the range in `commerce_dev`.
- [ ] Q4 Price lists "Presentación" column, same criterion as the catalog.
- [ ] Q5 Staff positions seed (Titular, Encargado, Carnicero, Chofer, Vendedor, Cajero).
- [ ] Q6 Sortable stock table.

## Progress

- Feature document created 2026-10-11.
