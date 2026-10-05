# Price Editing and Desktop Polish

## Objective

Fix what the owner found on 2026-10-04: the desktop Staff section opens with
the new-staff form and wastes the screen, the desktop theme only changes
after a restart, and the web "Gestionar precios" page shows "Por kg" instead
of the product and offers no real way to edit prices one by one or in bulk.

## Why (verified in code)

- `StaffView` shows the create form permanently next to a narrow vertical
  list; email/password boxes are tall.
- `DesktopThemeService.Apply` swaps the palette dictionary at runtime, but
  views read theme brushes with `StaticResource` (e.g. 21 uses in
  `MainWindow.xaml`), which resolve once at load, so nothing repaints until
  the app restarts.
- `PriceListsScreen`'s detail page renders `presentation.name` ("Por kg"),
  not the product, and only offers "Nuevo precio" per presentation (one
  `POST /pricing/price-lists/{id}/entries` per price); opening a row's
  history pushes its buttons around ("Ocultar historial").

## Decisions (owner, 2026-10-04)

- Desktop Staff: list first, using the full width; the create/edit form is
  hidden and opens only on "Nuevo" (or editing a row). Compact fields
  (email, password) and a cleaner look. Reuse the desktop
  `EntityListView` component built for Customers.
- Desktop theme changes apply instantly, without restarting.
- Web price editing moves to its own tab inside `/app/price-lists`,
  isolated from the lists table: pick a list, then
  - edit prices individually (rows show product name, presentation, code,
    current price; type a new price), or
  - remark the whole list (or the filtered/selected rows) by a percentage,
    with a preview of old -> new before publishing.
  Publishing writes all changed prices at once.

## Design defaults (orchestrator; owner can adjust)

- The percentage applies to the list's BASE prices (entries); the published
  composition (IVA, IB, flete, remarcación) keeps applying on top, and the
  grid shows base and final price side by side. Changing the composition's
  remarcación stays in the existing composition page.
- New batch contract (all-or-nothing, same floor rule as single entries):
  `POST /pricing/price-lists/{priceListId}/entries/batch`
  body `{ "effectiveFrom": "YYYY-MM-DD" | null, "entries": [{ "presentationId": "...", "unitPrice": 123.45 }] }`
  (`effectiveFrom` null = today's business day in Argentina; 1-2000 entries;
  unitPrice > 0, 2 decimals; a presentation at most once).
  `200 { "published": n, "entries": [PriceListEntryRecord...] }`;
  `400` validation problem; `409 { "error": "price-below-floor", "violations": [...] }`
  (same shape as today); nothing is written on any error. Same permission
  and branch rules as `POST .../entries`; one audit record per batch.
- History of a row opens in a side panel or dialog so the grid layout never
  shifts.

## Tasks

- [x] T1 Desktop Staff on `EntityListView`: list (Email, Rol, Sucursal,
  Estado) with search, filters (Rol, Estado), sortable columns, row actions
  (Editar rol, Revocar/Restaurar, Resetear contraseña), "Nuevo" opens the
  create form; form hidden otherwise; full width; compact fields (route:
  delegated POS writer) - done: UI-free `StaffList`, `EntityListView`
  opt-in `HidesClosedEditor`, own row cannot change its own role, Estado
  starts on Activos. GREEN 171 focused + 27 StaffList tests.
- [x] T2 Desktop live theme: every theme brush/color is consumed with
  `DynamicResource` (views, controls, templates, windows); switching in
  Settings repaints all open windows immediately; guard with a markup test
  that theme keys are not used through `StaticResource` (route: same POS
  writer, after T1) - done. The real cause differed from the first
  diagnosis: no palette key was consumed through StaticResource; the
  semantic brushes in DesktopTheme.xaml froze their palette color, so a
  palette swap repainted nothing. Brushes moved to Themes/ThemeBrushes.xaml,
  reloaded together with the palette (`DesktopThemeService.PlanApply`);
  two code-behind brush reads use `SetResourceReference`. RED 9 of 12, GREEN
  12 live-theme tests; full suite (fallback build, the owner's running POS
  and Cloud API locked bin/Debug) 2467 passed / 1 known failure.
- [x] T3 Web price editing tab - done 6bd4d59 (RED 18, GREEN `npm test` 664
  passed; batch publish mocked until T4). The unused PriceDateFilter was
  removed; the contacts-editor tests got a 20 s timeout (they exceeded 5 s
  on a loaded machine).
- [x] T3 (spec) Web "Editar precios" tab: list picker, grid (product, presentation,
  code, category, current base, current final, new base), search/filter by
  category, row selection, individual edits, bulk % with preview, effective
  date, publish through the batch endpoint, floor violations shown per row,
  history in a side panel; the old nested "Gestionar precios" page goes away
  or opens the new tab (route: delegated web writer, mocks the batch
  contract above).
- [x] T4 Server batch endpoint as specified (route: delegated backend writer,
  after T1/T2 finish to avoid concurrent .NET builds) - done: one
  transaction, floor validator both directions, same-day replace through
  migration 0043 (column-scoped GRANT UPDATE (unit_price, created_at_utc,
  created_by_user_id) to app_runtime; the 0017 tenant policy already covers
  UPDATE; the batch path is the only UPDATE on entries), one
  `price-list.entries-published` audit row with replaced old/new prices;
  replaced prices reach the POS sync. RED 19 of 40, GREEN 41 and 62 related;
  full suite green except the known failure (the 21-24 h UTC-date window
  failures reran clean after midnight).

## Acceptance criteria

- Desktop Staff opens on the list at full width; "Nuevo" opens the form;
  closing it returns to the list; fields are compact.
- Switching Oscuro/Claro/Vaca Verde in Settings repaints the sale, customers
  and staff screens at once.
- In the web, the "Editar precios" tab lists "Lengua" (not "Por kg") with its
  code and current prices; changing two prices and publishing writes both;
  "+10 %" on all rows previews and publishes every price x 1,10 (rounded to
  cents); a change that breaks the floor list publishes nothing and marks the
  offending rows.

- [x] T5 Fixes before the owner's next test run (owner, 2026-10-04): (a)
  desktop Staff wording follows the existing messages: row action "Dar de
  baja" / "Reactivar", status "Activo" / "Dado de baja", Estado filter
  "Activos" / "Dados de baja" / "Todos", and "Restablecer contraseña"
  everywhere; (b) the web price editor sends `effectiveFrom: null` (server
  business day) unless the user picked another date; (c) switching tab or
  list with unpublished price edits asks for confirmation (route: (a) with
  T4 in the .NET writer, (b)(c) web writer) - done: (a) with T4; (b)(c)
  c3df7da (RED 7, GREEN `npm test` 672). In-app route changes cannot be
  blocked (the app uses `<BrowserRouter>`); tab, list and browser unload
  are guarded.

- [x] T6 RDD review fixes on the web price editor (6bd4d59 + c3df7da) and the
  batch endpoint (5518713): a batch effective before today's business day is
  refused (c793eb2, so price history is never rewritten); the editor locks
  while publishing and clears only the published edits; hidden invalid rows
  are reported with a "show" action; a picked future date loads its own
  baseline, the date input starts at today and a server `effectiveFrom`
  error lands on the field; the "today" hint is visible; the clear-date test
  now proves the date was in effect first. GREEN `npm test` 679.

- [x] T7 Close the remaining follow-ups (owner, 2026-10-05): an app-wide
  unsaved-changes guard (`components/layout/UnsavedChanges.tsx`, mounted in
  AppLayout; works with `<BrowserRouter>`) asks before sidebar, mobile nav,
  account-menu links and sign-out discard pending price edits; while a picked
  date's prices load (or failed to load) the remark and publish are disabled,
  base/final cells show "…" and a failed load offers Reintentar. RED 7,
  GREEN `npm test` 689. Not covered: the organization/branch switchers still
  remount the screen without asking.

## Decisions for T4 (orchestrator default, owner informed 2026-10-04)

- A batch entry for a presentation that already has an entry effective the
  same day replaces it as a correction and the audit row keeps the old
  price (the single-entry endpoint keeps refusing that case).
- `effectiveFrom` null resolves to Argentina's business day on the server;
  validation errors use keys `entries[N].unitPrice` (N = index in the sent
  batch), which the web already maps to rows.

## Follow-ups

- Staff wording is mixed: "Revocar/Restaurar" and "Resetear contraseña" vs
  the existing messages "Usuario dado de baja/reactivado" and the
  "Restablecer contraseña" button.
- The web editor sends the browser's date by default; send null (server
  business day) unless the user picked a date.
- Pending price edits are lost when switching tabs (no warning).
- Manual checks: theme switch with Sale, Customers and Staff open; Staff
  Nuevo -> create -> list, Editar rol, Resetear contraseña.

## Out of scope (noted)

- Employees and their current accounts (PRD 9.19: file, contact, status,
  branches, user and role; advances, purchases, discounts and other
  movements; balance and history). Not implemented yet. The PRD excludes a
  full payroll liquidation; deducting merchandise from the salary fits the
  "discounts / movements" of the employee account. To be specified as its own
  task later (owner, 2026-10-04).

## Constraints

- TDD: Strict (RED -> GREEN -> REFACTOR). Runners: `dotnet test`; `npm test`,
  `npm run lint`, `npm run build`.
- One .NET writer at a time (shared build outputs and `commerce_test`).
- Money rounding: the existing `Money.Round2` rule.
