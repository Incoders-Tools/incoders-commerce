# Desktop POS Redesign

## Objective
Bring the Windows POS sale surface (`src/Commerce.Pos.Windows`, WPF) up to the
Vaca Verde reference (`D:\Incoders\Customers\Vaca Verde\UI POS.jpeg`): top
navigation, search/scan box, product cards, sale table, category rail, totals
and tender buttons — built from reusable components instead of one monolithic
window.

## Problem
The previous pass (`b488bf7`, `odd/tasks/desktop-modern-ui.md`) was visual
only. The POS still has no reusable controls (six windows, no UserControls,
one 539-line `MainWindow.xaml.cs`), no product browsing, no name search, no
line editing, no categories, no tender, and no tax breakdown.

## Why
The owner considers the desktop POS the most behind part of the product and
wants it prioritized (2026-09-29).

## Scope (authorized)
Phase 1 — UI and local data, no new product decisions required:
- Reusable WPF component layer and icon resources.
- New sale screen layout matching the reference.
- Local name search, add by presentation (cards), edit/delete lines.
- Category replication to the POS and the category rail.
- Record the selected customer on scanned sales (bug found while mapping).

Phase 2 — needs owner decisions and spec work before implementation
(NOT authorized yet): tender at sale (Efectivo / Tarjeta / QR; `QR` is not in
`PaymentMethod`), IVA 21% breakdown (tax is today a rate component composed
into the list price, not applied locally), cash session / "Cerrar Caja",
reports, product images (no image field in the domain), a POS "Productos"
window, weighted-item quantity entry.

## Constraints
- Stay on WPF; no third-party UI package unless a blocker is proven. Icons
  from Segoe Fluent Icons / Segoe MDL2 Assets glyphs or in-repo `Geometry`
  resources.
- Keep `DynamicResource` brushes and the palette keys so Dark, Light and
  Vaca Verde themes keep working.
- Keep the control names and handlers that code-behind and tests rely on
  (see mapping below) or update those tests in the same commit.
- Sale commit paths must never read `DeviceToken` (sync-blocked is not
  sales-blocked). Manual and scanned paths stay mutually exclusive.
- Spanish operator UI; code, identifiers, commits and docs in English.
- Commit straight to `dev` (owner workflow); no PR per slice.

## Mapping evidence (2026-09-29)
- Sale state: `ObservableCollection<ScannedSaleLineViewModel>`; commit via
  `BranchNodeService.CompleteScannedSale` (atomic sale + outbox write).
- Local data: SQLite `branch.db` `catalog_replica` (no category, no image;
  identification code doubles as barcode/SKU), `price_replica`.
  `ListCatalogPriceReplica()` returns everything; lookup is exact-code only.
- Scanned commit resets the customer picker but never passes the customer.
- Names to preserve: `ScanCodeTextBox`, `ScanMessageText`,
  `ScannedLinesListView`, `ScannedTotalText`, `CommitScannedSaleButton`,
  `CommitSaleButton`, `AmountTextBox`, `CustomerPickerComboBox`,
  `SaleResultText`, `StaleCatalogBanner`, `StaleCatalogBannerText`,
  `OperatorDisplayText`, `SwitchOperatorButton`, `ManageCustomersButton`,
  `ManageStaffButton` (+ `_Click`, asserted by `PosStaffManagementTests`),
  `SyncButton`, `RepairButton`, `BottomSyncStatusText`,
  `BottomVersionStatusText`; `MinWidth=1120`, `MinHeight=700`.
- POS tests live in `tests/Commerce.Integration` (ScannedSale*, SaleRegression,
  SaleCustomerPicker, LocalEffectivePriceSource, CatalogPriceReplica/Sync,
  PosStaffManagement, PosCompositionRoot).

## TDD
Strict TDD enabled (global config). Runner: `dotnet test
tests/Commerce.Integration` (needs the Postgres and pgbouncer containers up;
with Postgres down the tests skip green). XAML layout is verified by build and
structural tests; cart/search/category logic gets RED→GREEN tests.

## Tasks
- [x] T1. Component foundation: extract sale state into a testable cart model
      (add by presentation, change quantity, remove line, totals); add icon
      resources and shared control styles; create UserControls `PosNavBar`,
      `ProductCard`, `SaleLinesTable`, `CategoryRail`, `TotalsPanel`.
      Route: delegated direct (writer).
- [x] T2. Sale screen layout: rebuild `MainWindow` as the reference layout
      (nav, search/scan, cards grid + sale table, rail, totals) using T1;
      tender buttons shown disabled with "Próximamente" until Phase 2.
      Route: delegated direct (same writer as T1).
- [x] T3. Local name search (`BranchSyncStore` LIKE query) feeding the cards
      grid; scan keeps exact-code behavior. Route: delegated direct.
- [x] T4. Categories, organization-scoped (owner decision 2026-09-29).
      Split after verifying no categories table exists:
  - [x] T4a. Cloud: spec requirement, `categories` table (organization-owned,
        name unique per org, icon key from a fixed set), RLS, backfill every
        existing `products.category_id` into a "Sin categoría" row per org,
        FK from products, admin CRUD endpoints, integration tests.
  - [x] T4b. Web: categories management screen (admins, Spanish i18n, icon
        picker from the fixed set) and a category select in the product form.
  - [x] T4c. Sync + POS: category id/name/icon in the device catalog sync
        and `catalog_replica` (idempotent local migration), re-send on product
        or category edits (the cursor today follows only
        `presentations.updated_at_utc`), `CategoryRail` driven by real data
        filtering the cards.
      Route: delegated direct (one writer, sequential work units).
- [x] T5. Pass the selected customer on scanned sales, with a regression
      test. Route: delegated direct.
- [x] T6. Theme the default WPF ScrollBar and ComboBox (and the popup) with
      the palette keys so Dark and Vaca Verde have no light defaults; tighten
      the vertical gap between the search box and the section titles.
      Route: delegated direct.

## Acceptance criteria
- Sale screen matches the reference structure in all three themes, min size
  1120x700, no clipping.
- A sale can be built from scan, name search or cards; lines can be edited and
  removed; totals update; commit still writes sale + outbox offline.
- Existing POS tests stay green; new logic has tests.

## Checks per task
`dotnet build src/Commerce.Pos.Windows`, focused `dotnet test
tests/Commerce.Integration --filter` for touched areas, full integration suite
in a throwaway worktree at task close.

## Progress
- 2026-09-29: document created after mapping (Explore worker). Phase 1
  authorized by the owner ("arranco con el POS" → "Correcto").

- 2026-09-29: T1 + T2 done (route: delegated direct, one writer, strict TDD).
  - `dd389d3` refactor(pos): extract the sale cart into a testable model.
    RED: `SaleCartTests` did not compile (`SaleCart` missing); GREEN: 10/10.
  - `3c3efdf` feat(pos): add reusable sale components and icons
    (`Controls/` PosNavBar, ProductCard, SaleLinesTable, CategoryRail,
    TotalsPanel; icon glyphs + shared styles in `DesktopTheme.xaml`;
    `ProductCardViewModel`). RED: `ProductCardViewModelTests` did not compile;
    GREEN: 7/7. `PosComponentMarkupTests` is a structural guard (no RED).
  - `500bfe9` feat(pos): rebuild the sale screen around products, sale table
    and totals.
  - Checks: `dotnet build src/Commerce.Pos.Windows` 0 errors; focused
    `dotnet test --filter Scanned|Sale|PosStaff|PosComposition|Cart|ProductCard|PosComponent`
    60/60; full integration suite in a throwaway worktree 769/769, 0 skipped
    (Postgres and pgbouncer up). Layout checked by rendering the real XAML at
    1120x700 in Dark, Light and Vaca Verde (throwaway harness, not committed);
    the real app was not launched (needs pairing).
  - Decisions: nav-bar and totals controls keep the old x:Names inside the
    components (`ManageStaffButton`, `SyncButton`, `RepairButton`,
    `SwitchOperatorButton`, `OperatorDisplayText`, `ScannedTotalText`,
    `CommitScannedSaleButton`) and expose routed events; handlers keep their
    names in `MainWindow`. Manual sale is a "Venta manual" popup off the search
    box; the customer picker moved to the sale panel header (T5 wires it).
    Decrementing a line at quantity 1 removes it.
  - Known gaps: cards render the whole catalog without virtualization (T3 adds
    search filtering); default WPF scrollbars and ComboBox are not themed;
    search placeholder says "Escanear código" until T3 adds name search.
  - Review: RDD assess `afbb8b5..87c1fcf` medium, 1792 lines, review due
    (`slice_budget_reached`); owner DECLINED the review for this candidate.
    Parent spot check: `dotnet test --filter SaleCart` 10/10.

- 2026-09-29: T3, T5, T6 done; T4 BLOCKED (route: delegated direct, strict TDD).
  - `8a6ca81` feat(pos): search the local catalog by name as the operator
    types. RED: `CatalogSearchTests` did not compile (`SearchCatalog` missing);
    GREEN 9/9. `BranchSyncStore.SearchCatalog` folds case/accents through a
    registered SQLite function, all tokens must match name or code prefix,
    org scoped, capped at 120 with a `Truncated` flag (UI shows a hint);
    200 ms debounce; the cards grid is capped instead of virtualized. Enter
    with an exact code still scans.
  - `00d7cd0` feat(pos): record the selected customer on scanned sales.
    RED: `ScannedSaleCustomerTests` did not compile; GREEN 4/4. Additive
    `SaleEffect.CustomerId` / `SalePayloadV1.CustomerId`, nullable
    `sale_effects.customer_id` with an idempotent migration; spec requirement
    added to `pos-scan-sale`. Finding: the MANUAL path does not record the
    customer either (the brief assumed it did); left as is, follow-up.
  - `e7e9897` feat(pos): theme scrollbars and combo boxes with the palette
    keys (ScrollBar, ComboBox + popup + items, DynamicResource only); vertical
    gap under the search box reduced. Verified by rendering.
  - T4 blocker: Postgres has no categories table. `products.category_id` is a
    bare uuid (no FK, no name anywhere, no endpoint or Web screen manages
    categories), so a category name cannot be replicated. Needs an owner
    decision: create categories (table, CRUD, admin UI, backfill of existing
    products) as a cloud feature first, then replicate id+name. Note also that
    the catalog sync cursor follows `presentations.updated_at_utc`, so a
    product-only edit (name/category) is not re-sent today.
  - Checks: `dotnet build src/Commerce.Pos.Windows` 0 errors; focused tests
    green (293 for Scanned|Sale|Customer|Outbox|Sync|Payload; 39 for markup and
    composition); full integration suite in a throwaway worktree 782/782, 0
    skipped (Postgres and pgbouncer up). Re-rendered 1120x700 in Dark, Light,
    Vaca Verde with sample categories and an open customer dropdown.

- 2026-09-29 (parent): RDD assess `5cf1b8b..42bc1a9` medium, 721 lines,
  review due (`slice_budget_reached`); owner DECLINED the review for this
  candidate. T4 blocker verified by the parent: no categories table exists in
  any migration; `products.category_id` is a bare uuid.
  Follow-ups found: the manual sale path does not record the customer either;
  the customer picker shows "Walk-in (no customer)" in English; product-only
  edits (name, category) are not re-sent by the catalog sync cursor
  (`presentations.updated_at_utc`).

- 2026-09-29: T4a, T4b, T4c done (route: delegated direct, one writer, strict
  TDD, three sequential work units). Owner decision: categories are
  organization-scoped, managed by admins, name + icon key from a fixed set.
  - `94e41ff` feat(catalog): add organization-scoped product categories.
    Spec `openspec/specs/catalog-categories` (new). Migration
    `0018_catalog_categories.sql` (mirrored in `init-rls.sql`): `categories`
    with org-only fail-closed RLS, unique lower(btrim(name)) per org, icon CHECK
    over 12 keys, backfill of one "Sin categoría" per org that owns products,
    same-org composite FK from `products` (RESTRICT), all in one re-runnable
    transaction that also bumps `products.updated_at_utc`. Endpoints
    `/catalog/categories` (list for any member with a permission; create,
    rename/icon, delete for ManageCatalog incl. acting sysadmin; delete refused
    with 409 `category-in-use`), product create defaults to / validates the
    category, new `PUT /catalog/products/{id}/category`. RED: the migration
    tests failed for the missing 0018 file; the endpoint tests failed 10/10
    with the routes unmapped, then 10/10 green. Existing tests that persisted
    products with random category ids were moved to a real category
    (`CategoryFixture`).
  - `7623f09` feat(web): manage categories and assign them to products.
    `CategoriesScreen` (admin route `/app/categories`, sidebar link, icon
    picker, delete confirmation, Spanish explanations for 409s), category
    select on the catalog edit page (there is no product-creation form on the
    web today, so the select lives in the presentation edit page). RED: vitest
    failed on the missing screen/route/select; GREEN 293/293. `e2e/`
    product seeding no longer sends a random category id; e2e typechecked with a
    standalone `npx tsc --noEmit --ignoreConfig ...` (clean); e2e not run (CI only).
  - `55852e8` feat(pos): sync product categories and filter the sale grid by
    them. Sync projection carries category id/name/icon (nullable), the cursor
    now re-sends a presentation when its presentation, product OR category
    changed. RED: `CatalogPriceSyncTests` product-only and category-rename
    cases failed on the old cursor; `CatalogCategoryReplicaTests` did not
    compile before the store/rail changes; GREEN. POS: nullable category
    columns with idempotent `ALTER` for existing `branch.db`,
    `ListCatalogCategories`, `SearchCatalog(categoryId)`,
    `CategoryRailItem.Build`, `CategoryGlyphs` (Segoe UI Emoji glyphs, chosen
    over Segoe MDL2 because MDL2 has no food/drink symbols; WPF renders them
    monochrome so they follow the palette), rail filters the cards combined with
    the name search and falls back to Todos when its category disappears.
  - Checks: `dotnet build` (solution) 0 errors; focused `dotnet test` for
    Categor|Catalog|Migration|Pricing|Import|GuestOrdering|PublicRateLimit|
    AccountEndpoint 261/262 (the one failure is the known launcher wwwroot
    test, the dev stack was running); full integration suite in a throwaway
    worktree 808/808, 0 skipped; `npm run test` 293/293, `npm run build` ok.
    Re-rendered `MainWindow` at 1120x700 in Dark, Light and Vaca Verde with real
    icon keys (throwaway harness, PNGs not committed; the real app was not
    launched).
  - Decisions/deviations: default category is created on demand when a product
    is created without a category; delete is refused (no reassignment); the
    sync row's `updated_at_utc` stays the presentation's own; the dev database
    `commerce_dev` and the shared `commerce_test` were migrated to 0018 by hand.
  - Follow-ups: no web product create/edit form exists (products come from the
    API/import); the manual sale path still does not record the customer; the
    customer picker shows "Walk-in (no customer)" in English; running app
    processes started before the change keep the old binaries until restarted.

- 2026-09-29 (parent): T4 range `4491987..2259fe7` assessed HIGH (3163
  lines, 58 files; evidence: process-starting code in
  `CategoriesMigrationTests.cs`); owner DECLINED the review for this
  candidate. Parent spot check in a throwaway worktree: `dotnet test --filter
  Categor` 25/25 (main tree build blocked by the user's running POS locking
  `Commerce.Updater.dll`).

## Owner decisions (2026-09-29)
- Tax: the POS shows only the final-consumer total (tax included), no IVA
  line. Price composition is shown per product in the web products and price
  list forms; the IVA breakdown required by Argentine rules belongs on the
  fiscal ticket, not on the POS screen.
- Discounts (new Phase 2 scope): the cashier can apply a percentage discount
  to the whole sale or to specific lines. Every discount requires an
  authorization prompt (PIN now, card reading later) from an administrator,
  who may choose to share the ability with cashiers. DECIDED 2026-09-29:
  one shared discount PIN per branch (option A, chosen for simplicity over a
  personal-PIN-plus-permission model; the owner accepted that the audit
  cannot tell which person entered it). Admins set and rotate it. Design
  guards: store only a slow salted hash, replicate the hash to the branch's
  terminals so authorization works offline, lock the prompt after repeated
  failures, audit each discount with the current operator and "authorized by
  branch PIN". Card reading later is another way to present the same
  authorization.

## Next step
Phase 2 spec work: discounts with the shared branch PIN in `pos-scan-sale`,
then tender (Efectivo / Tarjeta / QR). Follow-ups from T4 are listed in the
last Progress entry.
