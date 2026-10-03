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
- [ ] T5 Web: customer form price-list select; price lists screen with composition breakdown, copy with new markup, composition edit, floor list and violations (route: delegated web writer)

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

## Next step

T5 web writer.
