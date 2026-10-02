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

- [ ] T1 Data: `customers.price_list_id`, organization default customer list, `price_lists.floor_price_list_id`; seed update (drop Clientes, Mostrador base + 35 % set, floor = Reparto, customers -> Reparto); migration + seed tests (route: delegated backend writer)
- [ ] T2 Pricing engine: resolve with the buyer's list (customer's list, else organization default), then composition, then customer discount; web orders and staff orders use it (route: delegated backend writer)
- [ ] T3 API: list composition breakdown per product, copy list with new markup, publish new composition, floor validation with violations (route: delegated backend writer)
- [ ] T4 POS: replica carries every list's entries and rate components plus customers' list; POS prices walk-in with Mostrador and a selected customer with the customer's list (route: delegated POS writer)
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

## Next step

T1-T3 backend writer, then T4 POS writer, then T5 web writer.
