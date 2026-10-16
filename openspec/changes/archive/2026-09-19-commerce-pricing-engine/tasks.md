# Tasks: Commerce Pricing Engine

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~4,970 (design forecast) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | Single PR, size-exception, 9 labelled commit ranges |
| Delivery strategy | single-pr |
| Chain strategy | size-exception |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: size-exception
400-line budget risk: High

User already accepted single-PR + size-exception this session. Mitigation: 9 dependency-ordered, separately labelled commit ranges (below) so Units 3 and 9 (highest risk) are reviewable in isolation.

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|---|---|---|---|---|---|
| 1 | Catalog persistence | commit range 1 | `dotnet test --filter MigrationRlsTests` | psql `0009` apply + `/health/ready` | Revert range; run 0009 inverse |
| 2 | Price schema/aggregate | commit range 2 | `dotnet test --filter PriceListTests` | psql apply | Revert range; tables unused |
| 3 | PricingResolutionService | commit range 3 | `dotnet test --filter PricingResolutionTests` | N/A (pure logic) | Revert range; nothing consumes it |
| 4 | Order integration | commit range 4 | `dotnet test --filter OrderPricingTests` | `WebApplicationFactory` + Postgres | Revert range; orders price-free again |
| 5 | Admin pricing API | commit range 5 | `dotnet test --filter PricingEndpointTests` | Postman/curl against `adminGroup` | Revert range; aggregate unreachable over HTTP |
| 6 | BranchNode replication | commit range 6 | `dotnet test --filter CatalogPriceReplicaTests` | manual: run BranchNode sync against staging | Revert range; cloud unaffected |
| 7 | POS scan rework | commit range 7 | `dotnet test --filter ScannedSaleTests` | manual runbook (no WPF harness) | Hide scan group; manual path survives |
| 8 | Web pricing UI | commit range 8 | `npm run test -- PriceListsScreen` | `npm run build` | Revert web slice only |
| 9 | Excel import pipeline | commit range 9 | `dotnet test --filter ImportGuardTests\|ImportMatcherTests` | manual: upload fixture `.xlsx` | Disable import routes |

## Phase 1: Catalog Persistence (Unit 1)
- [x] 1.1 RED+GREEN: `0009_catalog_and_pricing.sql` part A — `products`/`presentations`+code+partial unique index+RLS; `MigrationRlsTests`
- [x] 1.2 Mirror DDL into `deploy/dev/db/init-rls.sql`; update `deploy/README.md`/`staging-runbook.md`
- [x] 1.3 `Presentation.IdentificationCode` field
- [x] 1.4 RED+GREEN: `PostgresCatalogStore`/`CatalogRecords` (CRUD, `FindByIdentificationCodeAsync`, `ListChangedSinceAsync`)
- [x] 1.5 Rework `Catalog.cs` to persisted CRUD, remove request-body construction
- [x] 1.6 Add 7 new tables to `PostgresReadinessHealthCheck`
- [x] 1.7 Regression guard: existing `CatalogScreen.tsx` still renders against new CRUD shape (deferred to Unit 8 web rework per design.md; backend regression `CatalogEndpointTests`/`AccountEndpointTests` updated and green — see apply-progress notes)

## Phase 2: Price Schema + Aggregate (Unit 2)
- [x] 2.1 `0009` part B — `price_lists`, `price_list_entries` + RLS + `MigrationRlsTests` additions
- [x] 2.2 RED+GREEN: `PriceList`/`PriceListEntry`/`Money.Round2`
- [x] 2.3 RED: append-only test — superseding preserves prior entry
- [x] 2.4 GREEN: `PostgresPriceListStore` (`AppendEntryAsync`, `GetEffectiveAsync`, `ListHistoryAsync`)
- [x] 2.5 RED+GREEN: one-default-per-org partial unique index test

## Phase 3: PricingResolutionService — highest risk, granular (Unit 3)
- [x] 3.1 RED: guest resolution returns list price, no discount
- [x] 3.2 RED: registered customer resolution returns strictly lower price
- [x] 3.3 GREEN: `IEffectivePriceSource`, `PriceResolutionOutcome`, `PricingResolutionService.ResolveAsync`
- [x] 3.4 RED: `NoEffectivePrice` for zero entries and pre-earliest date
- [x] 3.5 GREEN: typed outcome, assert never `decimal?`/`0m`
- [x] 3.6 RED: rounding — `Round2` AwayFromZero; sum-of-lines equals total
- [x] 3.7 GREEN: apply rounding exactly twice (unit-net, then line-total)
- [x] 3.8 RED: channel-parity — Postgres vs SQLite source, same tuple, byte-identical result
- [x] 3.9 GREEN: `PostgresEffectivePriceSource`
- [x] 3.10 REFACTOR: confirm no channel/caller-identity parameter exists anywhere in the signature

## Phase 4: Order Integration (Unit 4)
- [x] 4.1 `OrderLineSnapshot`: add `UnitListPrice`, `AppliedDiscountPercentage`, `UnitNetPrice`, `LineTotal`
- [x] 4.2 `SubmitOrderLine` (price-free) replaces snapshot in `SubmitOrderRequest.Lines`
- [x] 4.3 RED: resolution runs after all 4 existing denial checks, before `_orderStore.Submit`
- [x] 4.4 GREEN: wire `ResolveAsync` into `CloudOrderSubmissionService.SubmitAsync`
- [x] 4.5 RED: `NoEffectivePrice` denies `no-effective-price`, no order stored
- [x] 4.6 GREEN: `OrderSnapshotFactory` resolved-price overload
- [x] 4.7 Regression guard: re-run existing access/binding/customer-enabled denial tests unchanged
- [x] 4.8 Update `OrderScreen.tsx` + `ordering.spec.ts` to price-free line shape, server-resolved total
- [x] 4.9 RED+GREEN: freeze test — price change after submit does not alter stored order

## Phase 5: Admin Pricing API (Unit 5)
- [x] 5.1 `Endpoints/Pricing.cs`: price list CRUD (create/find/list), append entry, history, using `adminGroup` shape gated on `Permission.ManageCatalog` (corrected from design.md Work Unit 5's original `Permission.ManageUsers` row — see task 5.3). Supplier-mapping CRUD deferred to Phase 9 — `supplier_price_mappings` does not exist yet; out of scope for this batch per orchestrator instruction.
- [x] 5.2 Register `MapPricingEndpoints()` in `Program.cs` (`PostgresPriceListStore`/`PostgresCatalogStore` already registered in Phases 1–2)
- [x] 5.3 RED+GREEN routing: `seller` (ManageCatalog, lacking ManageUsers) → 403; cookie-less → 401; device-token → 401; cross-org price list → 404, identical to nonexistent (threat-matrix Routing row) — `tests/Commerce.Integration/PricingEndpointTests.cs`
- [x] 5.4 RED+GREEN: audit row written per publish — `AppendEntryAsync` already wrote `price-list-entry.published` in Phase 2; this batch adds the endpoint-level integration assertion (`AppendEntry_ThenGetHistory_ReturnsPublishedEntry_AndWritesAuditRow`)

## Phase 6: BranchNode Replication (Unit 6)
- [x] 6.1 SQLite: `catalog_replica`, `price_replica` (+ partial unique index), reuse `sync_cursors` with `catalog-prices` channel
- [x] 6.2 RED+GREEN: `ApplyCatalogPriceSync` atomic; `SimulateInterruptedCatalogPriceSync` twin (byte-identical on failure)
- [x] 6.3 `GET /device/catalog/sync` on `DeviceBearer` policy, org from stored credential row
- [x] 6.4 RED+GREEN process-integration: unreachable host/401 leaves replica+cursor unchanged (threat-matrix row)
- [x] 6.5 `CatalogPriceReplicaClient` + `PullCatalogPricesAsync()` in `SyncButton_Click`, immediately after `PullCustomersAsync()`
- [x] 6.6 Regression guard: existing `PullCustomersAsync`/outbox-push sequence unchanged

## Phase 7: POS Scan Rework (Unit 7)
- [x] 7.1 SQLite: `sale_lines`, `sale_effects.sale_kind` column (default `'Manual'`) — `BranchSyncStore` (fresh-DB `CREATE TABLE` + `EnsureSaleKindColumnExists()` ALTER-TABLE compatibility path for pre-existing `branch.db` files)
- [x] 7.2 `LocalEffectivePriceSource` (POS half of shared port) — `Commerce.Pos.Windows/LocalEffectivePriceSource.cs` over `BranchSyncStore.GetEffectivePrice`; registered in `PosHostBuilder`
- [x] 7.3 RED+GREEN (BranchNode): scanned sale writes `sale_kind='Scanned'` + lines; manual sale writes `'Manual'` + zero lines — `tests/Commerce.Integration/ScannedSaleTests.cs`
- [x] 7.4 Scan `TextBox` + `ListView` + computed total in `MainWindow.xaml(.cs)`; "Commit scanned sale" button
- [x] 7.5 Demote existing `AmountTextBox`/`CommitSaleButton` to labelled fallback group; block manual commit while scan list non-empty
- [x] 7.6 Unknown-code / no-price: explicit message, no line appended, no zero substituted
- [x] 7.7 Stale-cache banner per ADR-002 threshold (no new freshness mechanism) — reuses `CachedOperator.Ttl` (14 days)
- [x] 7.8 Regression guard: manual-total sale path still commits unchanged when scan list is empty — `CompleteOfflineSale` call site untouched (structural read) + `SyncTests` (28/28) still green
- [x] 7.9 Manual runbook: scan known code offline, scan unknown code, commit both sale kinds, verify distinguishable in `branch.db` — checklist below

### Phase 7 Manual Verification Runbook (no WPF UI test harness — human checklist)

Consistent with the PairingWindow/OperatorLoginWindow/CustomersWindow precedent (no automated WPF coverage exists or is fabricated here):

1. Launch the POS terminal already paired to a branch whose `catalog-prices` replica has been synced at least once (Sync button) with a known presentation carrying an `identification_code` and a published price.
2. With network disconnected, type/scan the known code into "Code" and press Enter — confirm the item name, quantity 1, unit price, and line total appear in the scanned-lines list with no network call, and the "Scanned Total" updates.
3. Scan the same code again — confirm the SAME line updates to quantity 2 with a recomputed line total (not a duplicate row).
4. Type/scan a code that does not exist in the local catalog — confirm the on-screen message "Code {x} is not in this terminal's catalog." appears, no line is added, and the total is unchanged.
5. If a presentation exists locally with no effective price (or force this by testing before any price has synced) — confirm the "No effective price for {item} on {date}..." message appears, no line is added, and no zero-priced line appears anywhere.
6. Click "Commit scanned sale" — confirm a success message naming the total, the scanned list clears, and the total resets to $0.00.
7. Attempt to type an amount and click "Commit manual sale" while the scanned list is non-empty (repeat steps 2–3 without committing) — confirm the manual commit is blocked with the explicit "Cannot commit a manual sale while scanned lines are pending..." message and no sale is written.
8. With the scanned list empty, enter an amount and click "Commit manual sale" — confirm it commits exactly as before this change (unchanged manual path).
9. Open `branch.db` with a SQLite browser (or `sqlite3`) and confirm: the scanned sale's `sale_effects` row has `sale_kind = 'Scanned'` and has ≥1 matching `sale_lines` rows; the manual sale's `sale_effects` row has `sale_kind = 'Manual'` and has zero matching `sale_lines` rows.
10. Let the `catalog-prices` sync cursor age past 14 days (or manually backdate the `sync_cursors` row for this channel in `branch.db`) and relaunch — confirm the stale-cache banner appears above the scan section, its text names the cache age, and a sale can STILL be committed (staleness is visible, never blocking, per ADR-002).

## Phase 8: Web Pricing UI (Unit 8)
- [x] 8.1 `api/pricing.ts`, `catalog.ts`, `types.ts` clients
- [x] 8.2 `PriceListsScreen.tsx` + `PriceHistory.tsx` + `ImportReviewTable.tsx`; nest under existing `RequireAdmin` block in `App.tsx`
- [x] 8.3 RED+GREEN (Vitest): `seller` redirected by `RequireAdmin`; history expands; Commit disabled with zero `Matched` rows — fixed `PriceListsScreen.tsx`'s "create default"/"publish" handlers, which were calling a full `refresh()` (2 extra unmocked fetches) after the mutation instead of updating state from the mutation response, causing `PriceListsScreen.test.tsx`'s `toHaveBeenCalledTimes(3)` assertions to never settle
- [x] 8.4 Rework `CatalogScreen.tsx` to real presentation list + identification-code editing — RED test written first (`CatalogScreen.test.tsx` fully replaced: list rendering, PUT-based code edit, unreachable-API error state) against catalog-item-identification spec's "Admin Editing of Identification Codes" scenario, then GREEN implementation replacing the old hand-typed rename form
- [x] 8.5 `npm run build` passes; `npm run test` green — confirmed: 18/18 test files, 51/51 tests passing; `tsc -b && vite build` clean

## Phase 9: Excel Import Pipeline — highest risk, granular (Unit 9)
- [x] 9.1 Confirm ClosedXML `LICENSE` is MIT (flag + fallback to `DocumentFormat.OpenXml` if not); add `PackageReference` — verified live against nuget.org's published nuspec (`<license type="expression">MIT</license>`); no fallback needed
- [x] 9.2 `0009` part C: `supplier_price_mappings`, `price_import_batches`, `price_import_rows` + RLS
- [x] 9.3 RED: `.zip` renamed `.xlsx` rejected; oversized (>5MB) rejected; >5000-row sheet rejected before expansion
- [x] 9.4 GREEN: `ImportGuards` — magic bytes, size, row-count, single named sheet checks
- [x] 9.5 RED: formula cell — cached value read, never evaluated
- [x] 9.6 GREEN: `SupplierPriceImportParser` (ClosedXML, cached values, no recalculation)
- [x] 9.7 RED: unknown code → `UnknownCode`; identical price → `NoChange`; duplicate code in file → `DuplicateInFile`
- [x] 9.8 GREEN: `ImportRowMatcher`
- [x] 9.9 RED+GREEN: upload → `Staged`, zero live price changes; failed file creates no batch row
- [x] 9.10 RED+GREEN: commit writes through `AppendEntryAsync` normal path, one tx, audit row; committing twice → 409
- [x] 9.11 RED+GREEN: reject → `Rejected`, no price written
- [x] 9.12 Upload/review/commit/reject endpoints in `Endpoints/Pricing.cs` (+ supplier-mapping CRUD, needed to exercise the pipeline end to end)

## Phase 10: Full-Suite Verification
- [x] 10.1 `dotnet test Commerce.sln` full pass — 455/455 (Bootstrap 1, Upgrade 19, Integration 435); `dotnet build Commerce.sln` 0 errors (24 NU1903 transitive-advisory warnings on `System.IO.Packaging` 8.0.0 via ClosedXML — pre-existing dependency-graph risk, flagged, not blocking)
- [x] 10.2 `npm run test` and `npm run build` full pass in `src/Commerce.Web` — 18/18 files, 52/52 tests; `tsc -b && vite build` clean
- [x] 10.3 Confirm all Success Criteria in proposal.md checked — all 13 checked; see apply-progress for the per-criterion pass rationale
