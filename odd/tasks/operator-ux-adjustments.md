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

- [x] T5 Owner decision 2026-10-04: the organization's quantity decimal
  separator (`organizations.quantity_decimal_separator`, Comma | Dot) drives
  quantity display and entry on BOTH the web and the desktop. The POS
  receives it through the device sync (additive contract), stores it
  locally (offline-safe, last known value) and formats/parses kilos with it
  (`SaleQuantity`, cart lines, product cards, kilos dialog); a change in the
  web settings reaches the terminal on its next sync. Adding the same
  weighted product again keeps summing its kilos into the same line
  (confirmed). (route: delegated backend + POS writer, after T4.) - done:
  additive `GET /device/organization/settings` (device bearer, org from the
  credential) -> `{"quantityDecimalSeparator"}`; stored in `branch.db`
  `organization_settings_replica` (last known value; never synced = terminal
  culture); `QuantityFormat` drives `SaleQuantity`, cart lines, cards and
  the kilos dialog; parsing still accepts ',' and '.'.

- [x] T6 Owner decision 2026-10-04 (RDD advisory on d097a20: kilos had no
  upper bound): entering more than 50 kg on a line (adding or editing) asks
  for a preventive confirmation ("¿Confirmás 550,000 kg de Lengua?"); Yes
  keeps it, No returns to the kilos input. No hard cap. The threshold is a
  named constant in `SaleQuantity`, ready to become a setting. (route: with
  T5, delegated POS writer.) - done: `SaleQuantity.ConfirmAboveKilos = 50m`,
  `NeedsConfirmation` (50 no, 50.001 yes), inline themed confirmation inside
  the kilos dialog (No has focus). Also fixed the T4 review advisories: the
  enable/disable toggle re-reads the customer and sends
  `ExpectedUpdatedAtUtc` (server 409 customer-modified; `GET /customers/{id}`
  now accepts the device operator); a row-action click no longer opens the
  editor. GREEN 830 POS/sync tests; full `dotnet test` 2411 passed / 2
  failed (the known one + an intermittent one, see follow-ups).

- [x] T7 Close the T5/T6 review follow-ups before the owner's test run
  (owner, 2026-10-04): (a) the 50 kg confirmation evaluates the resulting
  line kilos (existing + entered when the product merges into its line, the
  edited value when editing), and the question shows that total; (b) only a
  409 `customer-modified` maps to "customer modified", other conflicts show
  the server's message or a generic one; (c) the desktop customer form's Save
  sends `ExpectedUpdatedAtUtc` of the loaded record (the web already does)
  and handles the 409 by telling the operator and reloading that customer;
  (d) a failed enable/disable does not reload the list when the server is
  unreachable, it shows the offline message instead. (route: delegated POS
  writer.) - done: `SaleQuantity.ResultingQuantity` + merged-total question
  ("60,000 kg de Lengua (30,000 + 30,000)"); 409 mapped by body
  (`Modified` only for customer-modified, new `Unreachable` kind);
  `CustomerFormSave` sends the loaded version and reloads on conflict;
  unreachable toggles do not reload. RED 4 value failures (after fixing 4
  wrong-reason ones), GREEN 194 focused, full `dotnet test` 2436 passed / 1
  known failure.

## Follow-ups

- `CatalogCategoryReplicaTests.OpeningABranchDbCreatedBeforeCategories_...`
  failed once in a full run (6 ms) and passed alone and in three grouped
  runs of the SQLite replica suites; suspected race around the global
  `SqliteConnection.ClearAllPools()` used by ~40 test classes. Watch it.
- Toggle then Save: if someone else saved the customer between the list
  load and the toggle's re-read, the open form adopts the newer version and
  its next Save could overwrite that change (small window).
- Bulk quantities show no unit ("60,000 de X"); the replica has no unit
  name.
- Manual checks pending: row-action click, kilos confirmation Enter/Esc,
  customers grid at 1366x768 in both themes.
- When the reload after a 409 also fails, the form keeps its data and the
  list is not reloaded.
- [x] T4 Desktop reusable entity list component (search, filters, column
  sorting, "Nuevo", row actions, side or wide edit panel) and the customers
  section rebuilt on it, wide layout without vertical scrolling at
  1366x768 and above (route: delegated POS writer, after T3) - done:
  UI-free `EntityListModel` / `EntityListDefinition<T>` (search, filters,
  sort toggle, row actions with confirmation, editor state) + template-only
  `Controls/EntityListView` (a UserControl cannot host named form fields,
  MC3093); Clientes rebuilt on it (list and form side by side, only the
  form scrolls, enable/disable row action through `PUT /customers/{id}`).
  RED 12 of 13 markup tests, GREEN 40 model + 13 markup tests, 244 POS
  suites, full `dotnet test` 2374 passed / 1 known failure. One writer
  crashed (infrastructure) mid-way; a second finished. Not yet checked by
  eye at 1366x768 in both themes.

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
