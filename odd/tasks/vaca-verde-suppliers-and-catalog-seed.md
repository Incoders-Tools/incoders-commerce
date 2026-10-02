# Vaca Verde Suppliers and Catalog Seed

## Objective

Load Vaca Verde's real suppliers, product catalog and price lists into the
system (local first, versioned so it travels to dev and prod), so the owner
can evaluate receptions, stock, POS and pricing with real data. Add soft
deletion for products so seeded examples can be retired later.

## Sources (owner's Downloads, outside the repo)

- `Proveedores Lista.xlsx`: 10 rows (Frigorífico, Dirección, Teléfono,
  Email, Ciudad, Contacto).
- `Precios Vaca Verde Reparto con Porcentajes.xlsx`:
  - "Lista Reparto con porcentajes": 26 wholesale cuts (Colgados + Cortes al
    vacío) with base price and IVA 10,5 %, IB 2,5 %, Flete 7 %, Remarcación
    25 %, all applied on the base (e.g. Asado completo 10.600 -> 15.370).
  - "Lista Clientes": final prices of the same cuts.
  - "MEDIA": 25 retail beef cuts per kg (same as "Vacuna").
- `Vacuno Cerdo Pollo.xlsx`: Vacuna (25), Cerdo + embutidos (25), Pollo (13,
  includes milanesas and medallones).
- `Achuras.xlsx`: 12 offal products; "Precio con aumento" is the final price
  (the % columns are inconsistent; "Centro" has "NO").

## Decisions (owner, 2026-10-02)

- Ruta 51 hosts both the butcher shop (POS, final consumer) and the
  distribution (wholesale + delivery). One branch, one catalog, several price
  lists: **Mostrador** (default; final consumer), **Reparto** (base + rate
  components), **Clientes** (final prices).
- A cut present in several lists is ONE product with a price per list.
- Product codes start as 001, 002, ... (presentation identification code),
  to be replaced later for scale integration.
- Add "Milanesa de ternera de bola de lomo" as an example product, flagged as
  provisional (may be edited or removed).
- Product categories: Vacuno, Cerdo, Embutidos, Pollo, Achuras, Milanesas,
  plus empty Almacén and Bebidas for the butcher shop.
- Products need soft deletion (owner's expectation; it does not exist today).
- Next feature (separate): a price list per customer; walk-in POS sale uses
  Mostrador; a selected customer (POS or web) uses that customer's list.

## Tasks

- [ ] T1 Product soft deletion: `is_active` on products, catalog endpoints to deactivate/reactivate, lists and POS replica exclude inactive by default, web catalog toggle (route: delegated writer A)
- [ ] T2 Supplier seed: normalize the 10 suppliers (cities on core geography, contacts, supplier category "Carne"), versioned idempotent seed + tests (route: delegated writer B)
- [ ] T3 Catalog seed: categories, products + Weighted "Por kg" presentations with codes 001..., price lists Mostrador (default) / Reparto (+ list-specific rate set) / Clientes with entries, provisional milanesa; review report for the owner (route: delegated writer B)

## Acceptance criteria

- Running the seed twice changes nothing.
- Reparto resolves Asado completo to 15.370 from base 10.600; Clientes to
  14.500; Mostrador prices match the retail sheets.
- Each product appears once, with a unique code, in the right category.
- A deactivated product disappears from the POS and default catalog lists but
  keeps its history.

## Constraints

- TDD: Strict (RED -> GREEN -> REFACTOR), source: global config. Runners:
  `dotnet test`; `npm test`, `npm run lint`, `npm run build`.
- Migrations forward-only, idempotent, appended to `deploy/dev/db/init-rls.sql`.
- Commit straight to `dev`, Conventional Commits, no AI attribution.

## Progress

- Feature document created 2026-10-02.

## Next step

T1 (writer A) and T2-T3 (writer B) in parallel.
