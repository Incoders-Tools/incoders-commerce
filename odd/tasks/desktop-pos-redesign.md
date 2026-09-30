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

### Phase 2a — Discounts (authorized 2026-09-29: "arrancá con los descuentos")
Design defaults (owner decisions + parent defaults, revisit if the owner
objects): percentage 0 < p <= 100 with up to 2 decimals; a line discount
applies to that line's total; a sale discount applies to the subtotal after
line discounts; amounts rounded to 2 decimals half away from zero per line
and on the sale discount; every discount (add or change) prompts for the
branch PIN; 5 failed attempts lock the prompt for 5 minutes on that
terminal; the admin can only set/rotate the PIN, never read it back.
- [x] D1. Spec: discount requirement and scenarios in `pos-scan-sale`, and a
      branch discount PIN requirement (set/rotate by admins, hash only,
      offline verification on terminals, lockout, audit).
- [x] D2. Cloud: branch discount PIN (slow salted hash + version) with a
      migration, admin set/rotate endpoint (branch settings permission,
      sysadmin on a selected org), audit on rotate, exposed to paired
      devices of that branch through the device sync.
- [x] D3. Web: "PIN de descuentos" set/rotate in the branch settings.
- [x] D4. POS: replicate the PIN hash; `SaleCart` line and sale discounts;
      PIN prompt with lockout; discount actions in `SaleLinesTable` and a
      "Descuento" row in `TotalsPanel`.
- [x] D5. Sale sync: payload carries line/sale discounts and "authorized by
      branch PIN" with the operator; cloud ingestion persists and audits it.
      Route for D1..D5: delegated direct (one writer, sequential units).

### Phase 2b — Tender (authorized 2026-09-29: record the method, no integration)
Owner decision: the POS only records how the customer paid; card and QR are
charged on the merchant's own terminal/app. Mercado Pago QR integration is
deferred. Parent defaults: one tender per sale in this cut (split tender is a
follow-up); Efectivo asks for the amount received (must be >= total), shows
the change, offers "Exacto" quick fill; Tarjeta and QR need one confirmation;
the tender buttons replace "Cobrar venta" as the way to complete a sale; works
offline like any sale.
- [x] P1. Spec: tender requirement and scenarios in `pos-scan-sale`
      (methods, cash change, offline, recorded and synced).
- [x] P2. Domain/payload: a QR method (house-consistent with
      `PaymentMethod`, without breaking existing order payments), tender
      method + amount received + change in the sale record and payload; cloud
      ingestion keeps accepting older payloads.
- [x] P3. POS: enable Efectivo/Tarjeta/QR in `TotalsPanel`, cash dialog with
      change, confirmation for card/QR, commit with the tender.
- [x] P4. Cleanups: the manual sale records the selected customer and a
      tender; the customer picker walk-in text in Spanish.
      Route for P1..P4: delegated direct (one writer, sequential units).

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

- 2026-09-29: D1..D5 done (route: delegated direct, one writer, strict TDD,
  throwaway worktree branch fast-forwarded into `dev`).
  - `29fa951` docs(spec): `pos-scan-sale` gains "Percentage Discounts on Lines
    and on the Whole Sale" and "Discounts Recorded and Synchronized With Their
    Authorization"; new `openspec/specs/branch-discount-pin` (set/rotate, hash
    only, replication, prompt with lockout).
  - `4f5a615` feat(cloud) D2: migration `0019_branch_discount_pin.sql`
    (`branch_discount_pins`, PBKDF2-SHA256 210k iterations, 128-bit salt, 256-bit
    hash, version, rotated_at/by; org AND branch RLS, fail-closed; tenant
    composite FK to branches; mirrored into `init-rls.sql`).
    `Commerce.Domain.Discounts.BranchDiscountPin` derives/verifies (same
    primitive as `OperatorPinCredential`). `PUT|GET
    /account/branches/{id}/discount-pin` gated like `/account/branches`
    (ManageBranchSettings, sysadmin acting on a selected org), 404 for a foreign
    branch, PIN 4 to 12 digits, audit `branch.discount-pin.set|rotated` with
    versions only. `GET /device/branch/discount-pin` (device bearer, branch
    from the stored credential) returns hash+salt+params+version. RED: tests did
    not compile (`BranchDiscountPin` / DTOs missing); GREEN 27 pin tests plus 78
    migration/categories tests.
  - `8c639a7` feat(web) D3: "PIN de descuentos" panel per branch in
    `BranchesScreen` (opens from a row action, shows only set/unset and the last
    change, write-only field). RED: 5 new vitest cases failed; GREEN 298/298;
    `npm run build` ok. No DOM moved (a row action was added), `e2e/` selectors
    (`Nombre de la sucursal`, branch name text) untouched, so no e2e edit.
  - `07d7d25` feat(pos) D4 logic + D5 sync: `DiscountMath`, `PinLockout`,
    `SaleCart` line/sale discounts (`SetLineDiscount`, `SetSaleDiscount`,
    `Remove*`, `BuildSaleLines`), `BranchPinDiscountAuthorizer` behind
    `IDiscountAuthorizer`, `DiscountPinReplicaClient` + `SyncRunner` pull,
    `branch.db` tables `discount_pin_replica` / `discount_pin_lockout` and
    additive nullable columns on `sale_effects` / `sale_lines` (idempotent, an
    older db opens), `SaleLine`/`SaleEffect`/`SalePayloadV1` extended additively,
    `BranchNodeService.CompleteScannedSale(saleDiscount, discountAuthorization)`,
    cloud `SaleDiscountAudit` writes one `sale.discount.authorized` audit row in
    the inbox transaction (duplicates return before it). RED: compile failures
    for every new type, then the ingestion cases failed (no audit row) before
    the hook; GREEN.
  - `4313bc1` feat(pos) D4 UI: `DiscountWindow` (percentage + PIN in one themed
    prompt, remove without PIN, unavailable message when no PIN / locked),
    per-line "%" action and discount text in `SaleLinesTable`, "Descuento" row
    and sale-discount button in `TotalsPanel`, `MainWindow` wiring and commit
    with the cart discounts. RED: 4 structural markup tests failed, then GREEN.
  - Checks: `dotnet build Commerce.sln` 0 errors; full integration suite in the
    throwaway worktree 917/917, 0 skipped (Postgres and pgbouncer up); `npm run
    test` 298/298, `npm run build` ok. Re-rendered `MainWindow` at 1120x700 in
    Dark, Light, Vaca Verde with a discounted line and a sale discount, plus the
    PIN prompt in each theme (throwaway harness, PNGs not committed; the real
    app was not launched).
  - Decisions/deviations: (1) the cloud has no sales table, so "persists" means
    the full payload stays in `sync_inbox.payload` (jsonb) plus the audit row.
    (2) `SaleLine.LineTotal` stays the UNDISCOUNTED amount; the payload total is
    the FINAL total; discount fields are null when unused. (3) A discount with no
    authorization marker still ingests and is audited as
    `sale.discount.unauthorized`. (4) The line/sale discount and the PIN share one
    prompt (percentage + PIN), asked on every add or change; a quantity change
    keeps the percentage and recomputes the amount without a new PIN. (5) The
    spec scenario for the sale discount used 1100/1000 arithmetic that did not
    add up (10% of 1100 is 110); corrected to 1000 -> 900 -> 855 in the same
    unit. (6) Web shows status per branch on demand (row action) instead of
    listing status for every branch, to avoid one request per branch. (7)
    `commerce_dev` and the shared `commerce_test` were migrated to 0019.
  - Follow-ups: the operator who authorized is the signed-in operator
    (`ResolveActorId`, the installation id when nobody is signed in); card
    reading is a future `IDiscountAuthorizer`; no cloud read/report of the
    discount audit exists yet (audit_log has no SELECT grant); the sale table
    shows about one row fewer at 1120x700 because the totals panel gained the
    Descuento row; running app processes keep the old binaries until restarted.

- 2026-09-29 (parent): discounts range `8f2a3fe..205edb2` assessed HIGH
  (3828 lines, 51 files); owner DECLINED the review for this candidate.
  Parent spot check in a throwaway worktree: `dotnet test --filter Discount`
  110/110. Renders checked: line -10% and sale discount totals consistent
  (10000 - 95 - 5% of 9905 = 9409.75); PIN prompt shows remaining attempts.

- 2026-09-29: P1..P4 done (route: delegated direct, one writer, strict TDD,
  throwaway worktree branch fast-forwarded into `dev`; not pushed).
  - `9e344e0` docs(spec): `pos-scan-sale` gains "Tender Recorded at the Moment
    of Sale", "Tender Recorded and Synchronized With the Sale" and "Walk-in
    Customer Label and Customer on Manual Sales".
  - `e3b9862` feat(sales) P2: `Commerce.Domain.Sales.SaleTender` (method
    `cash|card|qr`, amount received and change for cash only) and
    `SaleTenderRules.TryCash/Card/Qr`; additive `Tender` on `SaleEffect` and
    `SalePayloadV1`; `sale_effects.tender_method|tender_amount_received|
    tender_change` via idempotent `EnsureColumns` (`BranchSyncStore.Tender.cs`);
    `CompleteScannedSale(tender)` and `CompleteOfflineSale(customerId, tender)`.
    RED: the new tests did not compile (`Commerce.Domain.Sales` missing); GREEN
    28/28 (rules, commit incl. older-db upgrade and replay, cloud ingestion).
    The cloud ingestion tests are characterization tests: the cloud already keeps
    the sale payload as received in `sync_inbox.payload`, so no cloud code changed.
  - `f649b03` fix(pos) P4: walk-in entry reads "Consumidor final" (test first).
  - `4299722` feat(pos) P3+P4: `TenderInput` (parse cash with decimal comma,
    change, "Exacto", Spanish labels), `TenderWindow` (cash prompt with live
    change, card/QR single confirmation, DynamicResource only), `TotalsPanel`
    Efectivo/Tarjeta/QR enabled and raising `TenderRequested` (the "Cobrar venta"
    button and the "Próximamente" state are gone), `MainWindow` commits both
    paths with the tender; the manual popup now has Efectivo/Tarjeta/QR
    (`CommitSaleButton` is the Efectivo one), records the selected customer and
    rejects amounts <= 0. Neither commit path reads `DeviceToken` (guarded by a
    markup test). RED: `TenderInput` tests did not compile, markup/label tests
    failed; GREEN 118/118 for `TenderInput|SaleCustomerPicker|PosComponentMarkup|
    SaleTender|PosStaff|PosComposition|ScannedSale|SaleCart`.
  - Checks: `dotnet build Commerce.sln` 0 errors; full integration suite in the
    throwaway worktree 973/973, 0 skipped (Postgres and pgbouncer up); web not
    touched, so no npm run. Re-rendered `MainWindow` at 1120x700 in Dark, Light,
    Vaca Verde with enabled tender buttons, plus the cash prompt (with change and
    with insufficient amount), card and QR confirmations in all three themes
    (throwaway harness, PNGs not committed; the real app was not launched).
  - Decisions: (1) QR is NOT added to `PaymentMethod`: that enum types order
    payment attempts (persisted with a DB CHECK in `0011_payments.sql`, served by
    the payment endpoints and taxonomy spec), a sale tender is a POS record in the
    sale payload, so a separate `SaleTender` avoids a migration and touching order
    payments. If the tender is later reconciled with `PaymentLedger`, map
    `cash->Cash`, `card->Card`, and add QR to the taxonomy then. (2) The
    unused `payment_effects` / `CommitPaymentAtomically` path is not used: it has
    its own outbox and a `PaymentEffect` aggregate with approval semantics, which
    would make one sale two outbox rows and two sync kinds with no consumer; the
    tender rides the single atomic sale commit and payload. (3) No cloud audit row
    per sale tender (it would write one row per sale); the payload in `sync_inbox`
    is the record. (4) Old/new payloads both ingest; a tender with an unknown
    method is not rejected by the cloud (it only stores). (5) The manual sale now
    refuses amounts <= 0.
  - Follow-ups: split tender; no cloud read/report of tenders yet; cash change is
    shown in the prompt and the result line only (no printed ticket); if the
    tender should feed the payment ledger or cash-session totals, that needs its
    own decision; running app processes keep the old binaries until restarted.

- 2026-09-30 (parent): tender range `512cfa1..dc555a1` assessed medium
  (1206 lines, 24 files); owner DECLINED the review for this candidate.
  Parent spot check in a throwaway worktree: `dotnet test --filter Tender`
  56/56. Cash render checked: 9409.75 total, 10000 received, 590.25 change.

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
Parent: assess the tender range (`512cfa1..HEAD`) for review due, spot check
`dotnet test --filter Tender`, then decide on the next Phase 2 item (cash
session, reports, product images).
