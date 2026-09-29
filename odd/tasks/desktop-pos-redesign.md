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
- [ ] T3. Local name search (`BranchSyncStore` LIKE query) feeding the cards
      grid; scan keeps exact-code behavior. Route: delegated direct.
- [ ] T4. Replicate category to the POS (sync DTO + `catalog_replica` column
      + migration of the local schema) and drive the rail/filter.
      Route: delegated direct.
- [ ] T5. Pass the selected customer on scanned sales, with a regression
      test. Route: delegated direct.

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

## Next step
T3: local name search (`BranchSyncStore` LIKE query) feeding the cards grid.
