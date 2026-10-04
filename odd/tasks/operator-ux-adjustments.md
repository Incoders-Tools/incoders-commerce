# Operator UX Adjustments

## Objective

Fix what the owner found while testing the local environment on 2026-10-04:
the web catalog shows the presentation instead of the product, the web menu
mixes daily work with rarely used tables, the desktop sale adds whole units
for products sold by weight, and the desktop customers section is a tall
form that wastes the screen and has no search.

## Why (verified in code)

- `CatalogScreen` renders `presentation.name` ("Por kg") in the name column;
  the screen already loads the products (it marks inactive ones), so the
  product name is available.
- The sidebar puts customers, suppliers, price lists, satellite tables,
  users, branches and settings in one "Administración" section.
- `SaleCart.AddAsync` and `IncrementAsync` always add `1`, whatever the
  presentation's quantity behavior; Vaca Verde sells almost everything by
  the kilo ("Por kg", `Weighted`).
- The sale search box has no initial focus and its text padding (46) and
  placeholder margin (47) differ.
- `CustomersView` is a single vertical form with a plain list and no search,
  filters, sorting or row actions.

## Decisions (owner, 2026-10-04)

- Catalog shows the product name (presentation as secondary text when it
  adds information).
- Menu: rarely used satellite tables (categories, supplier categories,
  business types) go in their own section near the end; system
  administration (branches, settings, users) in another section at the end.
  Proposed layout (names can be adjusted by the owner):
  1. Dashboard (no section)
  2. Operación: Catálogo, Pedidos
  3. Gestión: Clientes, Proveedores, Listas de precios
  4. Compras: Recepciones, Stock
  5. Tablas auxiliares: Categorías, Rubros de proveedores, Tipos de negocio
  6. Sistema: Usuarios, Sucursales, Configuración
  7. Plataforma (system administrator only): Organizaciones, Ciudades
- Desktop sale:
  - The cursor sits at the start of the search field (focused, caret aligned
    with the placeholder text).
  - A product sold by weight is added and edited in kilos with decimals
    (e.g. 0,550 kg of lengua); the line price is the price per kilo of the
    buyer's list times the kilos. Same for every weighted product. Products
    sold by unit keep whole quantities.
  - More breathing room between the sale's action buttons and the fields.
- Desktop customers: use the full width to avoid scrolling; a web-like
  list: search, filters, sortable columns, a "Nuevo" button, row actions
  (edit, enable/disable, ...), and an edit form that does not push the list
  away. Build it as a REUSABLE desktop component (list + new + edit + row
  actions + search/filter/sort) so future entities reuse it.

## Tasks

- [x] T1 Web catalog shows the product name (route: delegated web writer) -
  done 1e64384.
- [x] T2 Web menu sections as above, Tablas auxiliares and Sistema at the
  end (route: delegated web writer) - done 1e64384; "Rubros de proveedor"
  stays singular (owner, 2026-10-04). RED 6, GREEN `npm test` 652 passed.
- [x] T3 Desktop sale: caret/focus in the search field; weighted lines in
  kilos with decimals priced per kilo (shared pricing rule, organization
  quantity format); spacing of the action area (route: delegated POS
  writer) - done: `SaleQuantity` rules (measured lines > 0, max 3
  decimals), one kilos entry point (`RequestMeasuredQuantity`, ready for a
  scale), line total via `PricingResolutionService` / `Money.Round2`,
  `FocusSearchBox` everywhere, spacing tokens. RED 30 compile errors, GREEN
  763 POS/sale tests, full `dotnet test` (Debug) 2321 passed / 1 known
  failure.

## Follow-ups

- The POS has no copy of the organization's decimal separator; quantities
  display with the terminal culture ("0,550 kg" on es-AR) while Vaca Verde's
  setting is Dot. Replicate the setting to the POS if the owner wants them
  to match.
- [ ] T4 Desktop reusable entity list component (search, filters, column
  sorting, "Nuevo", row actions, side or wide edit panel) and the customers
  section rebuilt on it, wide layout without vertical scrolling at
  1366x768 and above (route: delegated POS writer, after T3).

## Acceptance criteria

- The web catalog lists "Lengua" (not "Por kg").
- The web menu shows Tablas auxiliares and Sistema after the daily
  sections, with the items above.
- On the desktop sale the caret blinks at the start of the empty search
  field when the sale opens and after a sale is cleared.
- Adding "Lengua" (Weighted) lets the operator enter 0,550 kg and the line
  shows 0,550 kg x price per kg = rounded line total; adding a unit product
  still adds 1.
- Desktop customers at 1366x768: list and form visible without vertical
  scrolling of the whole section; search filters as you type; clicking a
  column header sorts; "Nuevo" opens an empty form; each row offers its
  actions.

## Constraints

- TDD: Strict (RED -> GREEN -> REFACTOR). Runners: `dotnet test`; `npm test`,
  `npm run lint`, `npm run build`.
- Run one test-running .NET writer at a time (shared `commerce_test`).
- Money keeps the existing rounding rule; quantities follow the
  organization's decimal separator.
