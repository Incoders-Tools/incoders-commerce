# Customer Price Lists

## Objective

Price every sale from the list that applies to the buyer: the butcher-shop
counter (walk-in, final consumer) from **Mostrador**, a customer (web order,
seller-entered order, or a customer selected at the POS) from the customer's
own list (today **Reparto** for every customer). Lists are independent, show
how each price is composed, can be copied with a new markup, and the counter
never sells below the delivery price.

## Why

Today only the organization's default list resolves, plus the customer's
discount percentage; there is no list per customer, and the POS replica
carries one list without rate components
(`PricingResolutionService` remarks). Vaca Verde sells to final consumers at
the counter and wholesale + delivery to customers at different prices.

## Decisions (owner, 2026-10-02)

- Two lists for now: **Mostrador** and **Reparto**. The seeded "Clientes"
  list is dropped (nothing uses it). Note given to the owner: Reparto's
  composed price (e.g. Asado completo 15.370) replaces the sheet's "Lista
  Clientes" figure (14.500).
- Lists are always independent: each has its own entries (base prices) and its
  own list-specific rate component set. Prices are never shared or mixed.
- Composition is broken down and visible:
  - Reparto: IVA 10,5 % + IB 2,5 % + Flete 7 % + Remarcación 25 %, all on base.
  - Mostrador: IVA 10,5 % + IB 2,5 % + Remarcación **35 %**, on base, no flete.
    Its base prices start as an independent copy of Reparto's base where the
    product exists there; for products only sold at the counter the base is
    derived from today's final price (final / 1,48). Easy to change later.
- Every customer is assigned a price list; new customers default to Reparto
  (organization setting "lista por defecto para clientes"); all seeded
  customers get Reparto. A walk-in POS sale uses the organization default list
  (Mostrador).
- Copy a list with a new markup (copies base entries and the composition with
  the changed remarcación) to create new lists (special discounts, etc.).
  Editing a composition publishes a new effective-dated set (history kept;
  editing in place is discouraged).
- Floor rule: Mostrador must never price below Reparto for the same product.
  A list can declare a "floor list"; publishing entries or a composition that
  would put any product below its floor is refused with the list of
  violations. The only way to sell lower at the counter is an explicit POS
  discount, which already requires the admin authorization configured for
  large discounts.

## Tasks

- [x] T1 Data: `customers.price_list_id`, organization default customer list, `price_lists.floor_price_list_id`; seed update (drop Clientes, Mostrador base + 35 % set, floor = Reparto, customers -> Reparto); migration + seed tests (route: delegated backend writer) - done 5a10db3: migration 0037, seed 004, generator/003/report/README updated; RED 5+7 failing, GREEN 16+5 seed/migration tests, 228 migration/seed tests green.
- [x] T2 Pricing engine: resolve with the buyer's list (customer's list, else organization default), then composition, then customer discount; web orders and staff orders use it (route: delegated backend writer) - done bafac94: BuyerPriceListSelector, orders priced from the buyer's list, customers priceListId/priceListName, org defaultCustomerPriceListId; RED compile failures, GREEN 6+7+3 tests, subset 625 green (1 known failure).
- [x] T3 API: list composition breakdown per product, copy list with new markup, publish new composition, floor validation with violations (route: delegated backend writer) - done 2923500: breakdown, composition, copy, floor endpoints and the 409 price-below-floor rule; RED compile/404, GREEN 15 endpoint + 4 domain tests.
- [x] T4 POS: replica carries every list's entries and rate components plus customers' list; POS prices walk-in with Mostrador and a selected customer with the customer's list (route: delegated POS writer) - done 44ab064 (replica) + fc9fda4 (POS pricing): new additive channel `price-lists` (`GET /device/pricelists/sync`, snapshot replaced in one SQLite tx with the cursor), `BuyerPricingFactory`/`SaleCart.SetCustomerAsync`, list name on the sale screen; RED compile failures (new types/members absent) then GREEN 6 cloud + 7 store + 4 client/sweep + 13 POS pricing tests, full `dotnet test` 2090 passed / 1 known failure (PublicRateLimitTests...IsUnreachable_AndAppStillStarts) / 0 skipped in Integration.
- [x] T5 Web: customer form price-list select; price lists screen with composition breakdown, copy with new markup, composition edit, floor list and violations (route: delegated web writer) - done a734d38 (composition UI) + b857742 (customer/settings selects); RED 7 + 5 + 2 failing, GREEN 7 composition + 3 customer form + 2 customers screen + 2 settings tests; `npm test` 547 passed (74 files), `npm run lint` 0 errors (warnings pre-existing pattern), `npm run build` ok.

- [x] T6 Price fallback: when the buyer's list has no effective price for a presentation, price it from the organization default list (Mostrador) with that list's composition, then the customer discount; same rule in the cloud and the POS (shared Application code); record which list priced each line; POS customer change no longer refused for missing prices (route: delegated writer) - done ac65d5c (shared rule + cloud + migration 0038) + 885f094 (POS) + b91e659 (web note); RED compile failures (PriceListPorts, PricedFromListId/FellBack, FallbackListName/PriceNote absent) and the web test failing, GREEN 4 fallback unit + 3 cloud order + 1 store round-trip + 2 migration + 3 POS + 1 cloud/POS parity + 2 web tests; `npm test` 549 passed, `npm run lint` 0 errors, `npm run build` ok; full `dotnet test` 2103 passed / 1 known failure (PublicRateLimitTests...IsUnreachable_AndAppStillStarts) / 0 skipped in Integration (+123 Upgrade, +1 Bootstrap).

- [ ] T7 Fixes authorized by the owner 2026-10-03: L1 POS customer picker and cart stay in sync (clearing a sale or a vanished customer resets both to walk-in); L3 effective dates (price entries, rate sets, replica snapshot, breakdown default) use the business day in America/Argentina/Buenos_Aires, not UTC (route: delegated writer)

## Acceptance criteria

- Bola de lomo: Reparto 16.530 (11.400 x 1,45); Mostrador 16.872 (11.400 x 1,48).
- A web order of a Reparto customer prices at Reparto; a walk-in POS sale at
  Mostrador; the same POS sale with that customer selected at Reparto.
- Copying Mostrador with remarcación 40 % yields a new list priced at base x
  1,53, leaving Mostrador untouched.
- Publishing a Mostrador composition or price that puts any product below
  Reparto is refused and lists the products.
- The breakdown of a price shows base, each component and the final price.

## Constraints

- Channel independence of `PricingResolutionService` is preserved: the price
  depends on the buyer, never on the channel.
- TDD: Strict (RED -> GREEN -> REFACTOR), source: global config. Runners:
  `dotnet test`; `npm test`, `npm run lint`, `npm run build`.
- Run one test-running writer at a time (shared `commerce_test` deadlocks).
- Migrations forward-only, idempotent, appended to `deploy/dev/db/init-rls.sql`;
  sync contract additive-only.
- Commit straight to `dev`, Conventional Commits, no AI attribution.

## Progress

- Feature document created 2026-10-02.
- T1-T3 backend done (5a10db3, bafac94, 2923500). Decisions: customers/org default point at one list of the organization; a list not visible in the selling branch is skipped (falls to the branch default). Mostrador base entries and set effective 2026-10-02. Floor validated on the change's effective date only, both directions. Guest web orders use the default list (Mostrador).
- T4 done (44ab064, fc9fda4). Decisions: the `price-lists` channel is a full snapshot (REPLACE, so removals need no tombstones and redelivery is idempotent) and carries the customers' list assignments, instead of extending the customers cursor channel (a migration-assigned list would never be re-sent); no cloud migration needed, SQLite tables are `CREATE TABLE IF NOT EXISTS`; entries are the base price effective on the server date, rate sets are the one effective today plus later-dated ones; a branch that never synced price lists keeps pricing from `price_replica` (no list label). Customer change on an open sale re-prices every line at its quantity from the new list, keeps discount percentages, sale discount and authorization (amounts recomputed), and is refused (nothing changes, picker reverts, products named) when the new list has no price for a line. Catalog cards show the current buyer's list price. Partials: the snapshot is re-sent on every sweep (add a version token + 304 if the catalog grows); no WPF/E2E run of the sale screen (markup and cart logic are covered by tests, the window itself was only built); Engram mirror `odd/customer-price-lists/tasks` not refreshed by this writer.
- T5 done (a734d38, b857742). Decisions: no new routes or nav entries (the breakdown, composition and copy pages are full-screen states of the existing `/app/price-lists` screen, like "Gestionar precios"), so `App.test.tsx`, `AppLayout.test.tsx` and `e2e/` needed no change; `ApiError` now keeps the parsed body so a 409 `price-below-floor` can list its violations; the breakdown reads `/breakdown?on=` (always sent, default today) and the history from `/composition`; the composition form publishes from tomorrow by default, either the full component set or "solo remarcación"; the customer form takes lists and the org default from `CustomersScreen` (container-presentational) and sends the empty-id sentinel to clear on edit; the settings PUT sends only the fields that changed. Partials: copy form only takes a new remarcación % (the API also accepts a full component set, no UI for it); customers list filters by list client-side; no E2E or browser run of the new pages (jsdom tests only); Engram mirror `odd/customer-price-lists/tasks` not refreshed by this writer.

## Reviews (owner granted each slice, 2026-10-02/03)

All six slices approved and acknowledged: A data + seed `29e0b30..5a10db3` (`review-6106b88fb13f1ea7`), B engine `5a10db3..bafac94` (`review-50aca56698a037a4`), C composition API `bafac94..5b4202d` (`review-dd9fd618a4934077`), D replica `5b4202d..44ab064` (`review-8e8ce25ea8574b21`), E POS pricing `44ab064..821acad` (`review-35d0dd5a1d59c68b`), F web `821acad..583a70b` (`review-d884f1f4b79749f3`).

## Follow-ups (non-blocking review findings, most relevant)

- [ ] L1 (-> T7) POS: clearing the sale resets the cart to walk-in but the customer picker keeps the old customer; a vanished customer can also desync picker and cart (`SaleCart.cs:216-217`, `MainWindow.xaml.cs:662-679`).
- [x] L2 (-> T6, done ac65d5c/885f094) A customer whose list has no price for a product cannot buy it (`PostgresPriceListStore.cs:179-192`). Owner decision 2026-10-03: fall back to the organization default list (Mostrador).
- [ ] L3 (-> T7) Replica and composition use the UTC date: between 21:00 and 24:00 Argentina time tomorrow's prices/sets apply early (`Device.cs:443-444`, `PriceListCompositionEndpoints.cs:393`).
- [ ] L4 Floor check runs outside the write transaction (TOCTOU) and only at the change's effective date; import floor check untested (`PriceListCompositionEndpoints.cs:176-181`, `Pricing.cs:270-278`, `558-568`).
- [ ] L5 Organization settings PUT can lose a concurrent update (`Account.cs:485-486`).
- [ ] L6 Customer price-list name join is not organization-scoped in SQL (relies on RLS) (`PostgresCustomerStore.cs:83`).
- [ ] L7 Seed 004 deletes "Clientes" by name; Mostrador conversion can be partial if re-run after manual edits (`004_vaca_verde_customer_price_lists.sql:121-180`).
- [ ] L8 Web composition form can submit an empty set if loading the current one fails (server rejects it) (`CompositionForm.tsx:91-94`); POS UI flow tested only by markup grep.

- T6 done (ac65d5c, 885f094, b91e659). Rule: `PricingResolutionService(PriceListPorts primary, PriceListPorts? fallback)`; the buyer's list first, then the default list (the fallback list's own composition, then the customer discount); both missing keeps `NoEffectivePrice`; no channel parameter (ADR-010). `Resolved` and `OrderLineSnapshot` carry `PricedFromListId` + `FellBack`; `order_lines.priced_from_price_list_id` (nullable, no FK) and `price_fell_back` (default false) via migration 0038, exposed in the order JSON as `pricedFromListId`/`fellBack`. Cloud: `CloudOrderSubmissionService` passes the branch default list as fallback; POS: `BuyerPricingFactory` passes the branch default list, `SaleCart` re-prices instead of refusing and the line shows "(precio de Mostrador)" (list name taken from the replica); the catalog card quote also falls back. Web: the staff order screen notes "Precio de Mostrador: N líneas". Floor rule untouched. Partials: the POS sale screen was only built, not run (the note is a XAML binding checked by a markup test); a fallback line keeps no list id in the POS line (only the name; the POS sale persistence has no provenance column); the public/guest order screen does not show the note (guests price from the default list, so they never fall back); Engram mirror `odd/customer-price-lists/tasks` not refreshed by this writer.

- T6 review (`1275322..19a9dd0`, owner granted): approved, 4 lenses, acknowledged (`review-93e3bb17d0937ad3`); non-blocking: the self-fallback guard (buyer list == default list) is untested (`PriceListFallbackTests.cs:74-80`).

## Next step

Feature complete pending parent review; refresh the Engram mirror.
