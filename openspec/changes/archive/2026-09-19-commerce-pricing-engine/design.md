# Design: Commerce Pricing Engine

## Technical Approach

The proposal's locked decisions stand. This design records **one verified
codebase fact the proposal assumed and that is false**, because it changes what
"implement the proposal" means and it is the honest reason this change is even
larger than the proposal forecast.

**Verified deviation — there is no persisted catalog.** `rg -i
'products|presentations' deploy/` returns hits only in `0008`'s prose comments
and the staging runbook: **no `products` table, no `presentations` table, no
`PostgresProductStore`, no catalog store of any kind.** `Product` and
`Presentation` (`src/Commerce.Domain/Catalog/`) are transient objects
*constructed from the request body* inside `CatalogEndpoints` (`Catalog.cs:57-62`
builds `new Product(productId, scope.OrganizationId, request.CurrentName, …)`),
and `CatalogScreen.tsx` is a six-textbox form where the operator types the
product id, name, category id and unit id by hand. `OrderScreen.tsx` does the
same for order lines.

Every one of the proposal's catalog-dependent requirements is unbuildable
against that: "identification code on the catalog domain + migration" has no
table to add a column to; "match rows to existing Products/Presentations" has
nothing to match against; a price entry cannot reference a presentation that is
not stored; "scanning a code displays the item's main data" has no item to
display. **Catalog persistence is therefore in scope as Work Unit 1**, mirroring
`PostgresCustomerStore` exactly. This is not gold-plating; it is the floor the
proposal's own scope stands on, and it is recorded in Open Questions for
acknowledgement rather than silently absorbed.

Confirmed as stated in the proposal: `Product`/`Presentation` carry **no
`BranchId`** (org-scoped, one org-wide price stands), `Presentation` has **no
identification field**, `OrderLineSnapshot` is price-free, `sale_effects` stores
one opaque `total_amount`, and **no Excel package reference exists anywhere** in
the solution (all `PackageReference` lines audited; only Npgsql, Sqlite, Hosting,
Http, and test packages).

Everything else follows one rule: **the money model is server-side, and the POS
runs the same code, not a copy of it.** `Commerce.Pos.Windows` and
`Commerce.BranchNode` already `ProjectReference` `Commerce.Application`
(verified in both `.csproj` files), so `PricingResolutionService` is shared
literally — channel-independence becomes a structural property, not a test-only
promise.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Effective-dating shape** | **Append-only history, resolved by "latest `effective_from` on or before the date".** `price_list_entries` carries `effective_from date` and **no `effective_to`**. Publishing a new price is a pure `INSERT`; no prior row is ever rewritten, so "superseding preserves the prior entry as history" is true by construction rather than by care. Resolution is one query: `WHERE presentation_id = $1 AND effective_from <= $2 ORDER BY effective_from DESC LIMIT 1`. "No effective price for this date" is exactly *zero rows* — a single, unambiguous condition that cannot be confused with a gap between two ranges. `UNIQUE (price_list_id, presentation_id, effective_from)` makes a same-day double-publish a 409 instead of a silent coin flip. `app_runtime` gets **no `DELETE`** on the entries table (the `customers`/`platform_admins` precedent), so history is not merely append-only by convention — destruction is unavailable. | **`EffectiveFrom`/`EffectiveTo` ranges.** Every new price would have to `UPDATE` the previous row's `effective_to` — a destructive write to the audit history this change exists to protect, racy under concurrent publishes, and it needs a `btree_gist` `EXCLUDE` constraint (a new extension) to stop overlaps. It also makes *two* distinct failure modes ("no row" and "row exists but the date falls in a gap") where append-only has one. The only thing ranges buy is expressing a deliberate expiry-with-no-successor, which nothing in this change needs. |
| **Which price list resolves (and what `CustomerKind` does)** | **One `is_default` price list per organization, and `CustomerKind` selects nothing.** `price_lists (id, organization_id, name, is_default)` with a partial unique index `WHERE is_default`. Resolution always reads the org's default list; the guest↔registered divergence comes **only** from `Customer.DiscountPercentage`, exactly as ADR-010 and the proposal state it ("Guest = official list price, no discount. Registered = list price adjusted by `DiscountPercentage`"). Named non-default lists are creatable and viewable by the admin screen but are not yet selected by any rule. | **Make `CustomerKind` (Retail/Wholesale) pick a list.** Intuitive, and the customer-identity design even anticipated it — but no decision locked a kind→list mapping, and inventing one silently changes the commercial meaning of every existing Wholesale customer the moment it ships. Deferred to the same future ADR that owns promotions and quantity breaks. |
| **`PricingResolutionService` contract and location** | **`Commerce.Application/Pricing/PricingResolutionService.cs`, taking a pure value tuple and an injected `IEffectivePriceSource`.** There is **no channel parameter and no caller identity parameter** — channel-independence is enforced because there is nothing to differ on, not because a test says so. Postgres supplies `PostgresEffectivePriceSource`; the POS supplies `LocalEffectivePriceSource` over `BranchSyncStore`. The *same* compiled service prices an online order and an offline scan. Failure is a typed outcome (`PriceResolutionOutcome.NoEffectivePrice`), never a nullable decimal and never `0m`. | **A cloud-only service with the POS reimplementing arithmetic in `MainWindow`.** Two copies of the money rule is exactly the ADR-010 defect. **Returning `decimal?`** — a `null` that a careless `?? 0` turns into a free sale, which the proposal explicitly forbids. |
| **Rounding policy (one, server-side)** | **`MidpointRounding.AwayFromZero` to 2 decimals, applied twice and only twice:** `unitNet = Round(unitList * (1 - discount/100), 2)`, then `lineTotal = Round(unitNet * quantity, 2)`. The sale/order total is the plain sum of already-rounded line totals — never re-rounded, never recomputed from unit prices. Money is `decimal` end to end (`numeric(12,2)` at rest); `double` appears nowhere. Because the rule lives in `PricingResolutionService`, the POS cannot drift from it. | **Round only at the total** — line totals shown to the customer then fail to add up to the printed total. **Banker's rounding** (.NET's default) — correct for statistics, surprising for retail money and not what a hand-written Argentine price list does. |
| **Identification code placement and uniqueness** | **`presentations.identification_code text NULL`** (decision (c): per Presentation, so a scan resolves to exactly one sellable line), with `CREATE UNIQUE INDEX presentations_org_code_uk ON presentations (organization_id, identification_code) WHERE identification_code IS NOT NULL`. Org-scoped because `Presentation` is org-scoped through its product and two organizations legitimately share an EAN-13. Partial, so unlabelled presentations stay unconstrained. Enforced by the **index, not by UI code** (the proposal's own risk-row mitigation). Lookup is `WHERE organization_id = … AND identification_code = $1`, and the SQLite replica carries the identical partial unique index so an ambiguous scan is impossible offline too. | **Globally unique** — rejects a second org's legitimate use of the same manufacturer barcode. **On `Product`** — contradicts decision (c) and forces a "which presentation?" prompt at the counter. **Uniqueness validated in the endpoint** — a second write path or a direct SQL insert defeats it. |
| **Excel library** | **ClosedXML.** Selected on two grounds: (1) licensing — ClosedXML is MIT; EPPlus moved to a commercial/Polyform-Noncommercial licence from v5, which is a real cost for a commercial product. *This licensing claim is carried from the exploration phase and was not re-verified against an online source in this offline session; `sdd-apply` MUST confirm the package's `LICENSE` before the `PackageReference` lands.* (2) Security — ClosedXML reads OpenXML `.xlsx` only, so the legacy OLE/BIFF `.xls` parser surface (historically the richer exploit target) simply is not present, and it never evaluates formulas unless asked. | **EPPlus** — licence risk on a commercial product. **`DocumentFormat.OpenXml` raw** — no licence problem, but hand-rolling shared-string-table and cell-type handling for untrusted input is more attack surface authored by us, not less. **CSV-only import** — sidesteps the library entirely but contradicts the proposal's locked `.xlsx` scope and the user's actual pain. |
| **Import state machine and untrusted-file handling** | **Four states, persisted: `Staged → Committed \| Rejected` (and `Failed` for a file that never parsed).** No `Uploaded` state — a file that fails validation never creates a batch row, so an un-reviewable batch cannot exist. Guards, all before any row is read: `.xlsx` extension **and** OpenXML magic bytes; **≤ 5 MB**; **≤ 5 000 data rows** (checked against the sheet's used range *before* materializing cells, so a zip-bomb-shaped sheet is refused rather than expanded); exactly one named sheet, taken from the supplier mapping, never guessed; cell values read as cached values with **no recalculation**; price must parse as `decimal`, be `> 0` and `< 100 000 000`. Every row lands in `price_import_rows` with a `match_status` — nothing is ever silently skipped. Commit writes through `PostgresPriceListStore.AppendEntryAsync` (the **normal** write path, one transaction, one audit row) — there is no bulk-insert bypass. | **Auto-apply matched rows and only surface the failures** — violates locked decision (d). **Parse in memory with no batch persistence** — the review screen would then depend on a server-side session, and a rejected import would leave no evidence of what was proposed. **Trusting the file extension alone** — a renamed `.zip` reaches the parser. |
| **Per-supplier column mapping** | **A small `supplier_price_mappings` table** (`supplier_name`, `sheet_name`, `header_row`, `code_column`, `price_column` as Excel column letters), selected by the admin at upload time and reused for that supplier's later imports (decision (a)). Columns are stored as letters, not indices, because that is what the admin reads off the supplier's own file. | **A JSON blob column** — unqueryable and untyped for five scalar fields. **Auto-detecting columns by header text** — a heuristic on untrusted input; decision (a) says *configured*, not *guessed*. |
| **`OrderLineSnapshot` extension and where resolution runs** | **Four new fields** — `UnitListPrice`, `AppliedDiscountPercentage`, `UnitNetPrice`, `LineTotal` — and `SubmitOrderRequest.Lines` changes type from `IReadOnlyList<OrderLineSnapshot>` to a **new price-free `SubmitOrderLine`** record. A client supplying a price is not *rejected*; it has **nowhere to put one**, the exact `AccessEnabled`-removal idiom this repo already established. Resolution runs in `CloudOrderSubmissionService.SubmitAsync` **after all four existing denial checks** (access → credential/customer binding → customer exists under RLS → customer enabled) and **before** `_orderStore.Submit`: the customer's `DiscountPercentage` is only known after the `FindAsync` read, and an unauthorized caller must not be able to probe catalog/price existence through a timing or reason-code difference. A `NoEffectivePrice` outcome denies with reason `no-effective-price`. | **Resolve first, or interleaved with the access checks** — leaks price/catalog existence to an unauthenticated probe and does pricing work for callers that will be denied anyway. **Keep `OrderLineSnapshot` in the request DTO and null out prices server-side** — the field exists, so a future edit can wire it back up. |
| **BranchNode replication: one channel, not two** | **A single `catalog-prices` channel carrying catalog rows and their currently-effective price together.** New SQLite tables `catalog_replica` and `price_replica` (+ the same partial unique index on the code), the **existing** `sync_cursors` table with a second channel constant, and `ApplyCatalogPriceSync(...)` applying upserts, removals, and the cursor advance in **one** transaction — a byte-for-byte mirror of the `customers_replica` / `ApplyCustomerSync` / `SimulateInterruptedCustomerSync` shape, including the test-only interrupted twin. Server side: `GET /device/catalog/sync?since=` on the existing device-bearer group, org derived from the stored `device_credentials` row. Client side: `CatalogPriceReplicaClient` and `PullCatalogPricesAsync()` in `SyncButton_Click`, **immediately after `PullCustomersAsync()`** and still before the `pending.Count == 0` early return. Failure is non-fatal and leaves replica and cursor untouched. | **Two cursors (catalog and prices separately)** — a partial sync could pair a new presentation with no price, or an old name with a new price; one cursor makes that unrepresentable. **A new sync idiom** — the brief forbids it and the existing one is proven. **Replicating price *history*** — the terminal only ever sells at today's price; shipping every historical entry to every notebook widens the offline blast radius for nothing. |
| **Freshness / staleness** | **ADR-002's existing policy verbatim, read off the existing `sync_cursors` row.** No TTL constant, no timer, no second mechanism is introduced. The POS shows the cache age in its status panel and a persistent banner once the age exceeds ADR-002's threshold; a stale cache **never blocks a sale** (ADR-002 keeps the branch authoritative for its own sale) — it is made *visible*, which is what the proposal's risk row asks for. | Any new freshness knob — explicitly forbidden by the proposal. **Blocking the sale when stale** — turns a connectivity blip into a closed counter. |
| **POS: two explicit buttons, not a mode toggle** | **One window, two visually separated groups, two commit buttons.** Primary: a focused scan `TextBox` (keyboard-wedge, `Enter` → lookup against `catalog_replica`/`price_replica`) appending to a `ListView` of lines with a computed `TotalText`, committed by **"Commit scanned sale"**. Secondary, below a separator and labelled "Unlisted item — manual total": the existing `AmountTextBox` + **"Commit manual sale"**. Pressing the manual button with a non-empty scan list is blocked with an explicit message — a mixed sale would need per-line provenance, which is out of scope. Rationale: a toggle carries a *hidden* mode, and the failure it produces is a sale committed through the wrong path with no visible cause; two buttons make the choice explicit at the instant of commit. **Distinguishable in the record**: `sale_effects` gains `sale_kind TEXT NOT NULL DEFAULT 'Manual'` (`'Scanned' \| 'Manual'`), a new `sale_lines` table holds the scanned lines, and `sale_kind` is carried in the outbox payload JSON. The `outbox` table shape is **not** touched. | **A single toggle/radio mode** — hidden state on the one screen that must never be ambiguous under counter pressure. **Removing the manual path** — the proposal's additional decision keeps it. **Leaving `sale_effects` unchanged** — a fallback sale would be byte-identical to a priced one, which is precisely what the risk row says must not happen. |
| **Unknown code / no price at the POS** | **Explicit, per-scan, and non-appending.** An unknown code → "Code {x} is not in this terminal's catalog." A known code with no locally effective price → "No effective price for {item} on {date} — use the manual path or sync." In both cases **no line is appended and no zero is ever substituted**, matching the cloud's `NoEffectivePrice` outcome. The scan box clears and re-focuses so the next scan is not swallowed. | A `0`-priced line with a warning — a zero-priced line is a money defect that looks like data. |
| **Web: `PriceListsScreen` under the existing `RequireAdmin`** | **`/app/price-lists` nested inside the existing `<Route element={<RequireAdmin />}>` block in `App.tsx`** (the same block that already guards `customers`), with the nav tab hidden without the bit — the `CustomersScreen` pattern reused verbatim, no new guard. Three sections on one screen: **Prices** (presentations with current price, a per-row History expander listing every `effective_from` descending, and a "New price" form), **Suppliers** (the column-mapping CRUD), and **Import** (upload → review table → Commit/Reject). The review table is one row per `price_import_rows` row: row #, raw code, matched item, current → proposed, and a status badge (`Matched`/`NoChange`/`UnknownCode`/`InvalidPrice`/`DuplicateInFile`). Commit is disabled when no row is `Matched`. Identification-code editing lands on `CatalogScreen`, which Work Unit 1 turns from a hand-typed rename form into a real presentation list. | **A separate `ImportScreen`** — the reviewer would have to hold two screens in mind for one workflow. **A new admin guard** — `RequireAdmin` already exists and is tested. |
| **Price tables RLS** | **Mirrors `customers` exactly**: own denormalized `organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE`, symmetric `USING`/`WITH CHECK` tenant policy with the `NULLIF(current_setting('app.current_org_id', true), '')::uuid` pooler hardening from `0001`, `ENABLE` + `FORCE ROW LEVEL SECURITY`, `REVOKE ALL FROM PUBLIC`, `GRANT SELECT, INSERT, UPDATE` to `app_runtime` — **no `DELETE` on any new table**. `PostgresPriceListStore` mirrors `PostgresCustomerStore` byte-for-byte: raw `NpgsqlDataSource`, one transaction per scoped method, `set_config('app.current_org_id', $1, true)` always the first statement, audit row in the same transaction as the mutation. | Any new persistence idiom, or a `DELETE` grant "for later" — both forbidden by the proposal's Approach. |

## Data Flow

```text
Resolve a price (ONE service, both hosts, no channel parameter)
  PricingResolutionService.ResolveAsync(
      presentationId, quantity, discountPercentage /* null = guest */, effectiveOn)
    -> IEffectivePriceSource.GetUnitPriceAsync(presentationId, effectiveOn)
         cloud: PostgresEffectivePriceSource  -> price_list_entries, default list
         POS  : LocalEffectivePriceSource     -> price_replica (SQLite)
    -> null  -> PriceResolutionOutcome.NoEffectivePrice   <- NEVER 0m, never null
    -> value -> unitNet   = Round(unitList * (1 - d/100), 2)
                lineTotal = Round(unitNet * quantity, 2)
  Same tuple, same answer, in both hosts: the same compiled method runs.

Submit an order (resolution AFTER every existing denial check)
  POST /orders {orderId, customerId, accessCredential, lines[] }   <- NO price field
    -> [unchanged] access -> binding -> customer exists (RLS) -> customer enabled
    -> for each line: ResolveAsync(presentationId, qty,
                                   customer.DiscountPercentage, today)
         NoEffectivePrice -> Denied "no-effective-price"   <- nothing submitted
    -> OrderSnapshotFactory.Snapshot(product, presentation, qty, resolvedPrice)
    -> orderStore.Submit(...)          <- frozen: list, discount, net, line total
  A caller has nowhere to put a price: SubmitOrderLine has no price member.

Supplier Excel import (untrusted input never touches live prices)
  POST /pricing/imports  (multipart: file + supplierMappingId)   [admin]
    -> guards: .xlsx magic bytes | <=5 MB | <=5000 rows | 1 named sheet
               -> any failure: 400, NO batch row created
    -> ClosedXML, cached values, no recalculation
    -> per row: code -> presentations.identification_code (org-scoped)
         Matched | NoChange | UnknownCode | InvalidPrice | DuplicateInFile
    -> INSERT price_import_batches(status='Staged') + price_import_rows  [1 tx]
    -> 201 {batchId}                    <- ZERO live prices changed so far
  GET  /pricing/imports/{id}    -> the review table
  POST /pricing/imports/{id}/commit  [admin]
    -> status must be 'Staged' (else 409)
    -> ONE tx: for each Matched row -> AppendEntryAsync (the NORMAL write path)
               -> batch status='Committed' -> audit row
  POST /pricing/imports/{id}/reject -> status='Rejected', no price written

Offline catalog+price replication (the customers_replica pattern, reused)
  MainWindow.SyncButton_Click
    -> await ReconcileOperatorsAsync()        [existing]
    -> await PullCustomersAsync()             [existing]
    -> await PullCatalogPricesAsync()         [new, same position, same shape]
         GET /device/catalog/sync?since={cursor}   Bearer <DeviceToken>
           org from the STORED device_credentials row, never the body
         -> ONE SQLite tx: upsert catalog_replica + price_replica,
                           delete removed ids, advance sync_cursors
         -> unreachable/401 -> replica and cursor byte-identical, sale unblocked
    -> existing pending-outbox push, unchanged

POS scan-to-sell (zero HTTP, zero token read - the structural property holds)
  ScanTextBox + Enter
    -> catalog_replica lookup by (organization_id, identification_code)
         miss -> "Code X is not in this terminal's catalog."   [no line]
    -> ResolveAsync via LocalEffectivePriceSource
         NoEffectivePrice -> explicit message                  [no line, no zero]
    -> append line {name, presentation, qty, unitNet, lineTotal}; Total recomputed
  "Commit scanned sale"  -> sale_effects(sale_kind='Scanned') + sale_lines [1 tx]
  "Commit manual sale"   -> sale_effects(sale_kind='Manual'), no lines
                            blocked while the scan list is non-empty
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `deploy/db/migrations/0009_catalog_and_pricing.sql` | Create | `products`, `presentations` (+ `identification_code` and its partial unique index), `price_lists`, `price_list_entries`, `supplier_price_mappings`, `price_import_batches`, `price_import_rows`; RLS/grants/policies for all seven, with the inverse block shipped as comments. |
| `deploy/dev/db/init-rls.sql` | Modify | Same DDL appended verbatim (the hand-kept parity convention `MigrationRlsTests` asserts). |
| `deploy/README.md`, `deploy/staging-runbook.md` | Modify | `0009` apply section and its inverse block. |
| `src/Commerce.Domain/Catalog/Presentation.cs` | Modify | `string? IdentificationCode`. |
| `src/Commerce.Domain/Pricing/PriceList.cs`, `PriceListEntry.cs`, `Money.cs` | Create | Aggregate + append-only entry (`EffectiveFrom`, no `EffectiveTo`) + the rounding policy in one place. |
| `src/Commerce.Domain/Ordering/OrderLineSnapshot.cs` | Modify | `UnitListPrice`, `AppliedDiscountPercentage`, `UnitNetPrice`, `LineTotal`. |
| `src/Commerce.Application/Pricing/PricingResolutionService.cs` | Create | The ADR-010 engine. No channel parameter, no caller identity. |
| `src/Commerce.Application/Pricing/IEffectivePriceSource.cs`, `PriceResolutionOutcome.cs` | Create | The one port both hosts implement; typed `NoEffectivePrice`. |
| `src/Commerce.Application/Pricing/Import/SupplierPriceImportParser.cs`, `ImportRowMatcher.cs`, `ImportGuards.cs` | Create | Parse → match → stage; all untrusted-input guards in one auditable place. |
| `src/Commerce.Application/Ordering/OrderSnapshotFactory.cs` | Modify | Snapshot overload taking the resolved price. |
| `src/Commerce.Cloud.Api/Persistence/PostgresCatalogStore.cs`, `CatalogRecords.cs` | Create | Products/presentations CRUD + `FindByIdentificationCodeAsync` + `ListChangedSinceAsync`; mirrors `PostgresCustomerStore`. |
| `src/Commerce.Cloud.Api/Persistence/PostgresPriceListStore.cs`, `PricingRecords.cs` | Create | `AppendEntryAsync`, `GetEffectiveAsync`, `ListHistoryAsync`, list CRUD, batch/row persistence; same store idiom. |
| `src/Commerce.Cloud.Api/Pricing/PostgresEffectivePriceSource.cs` | Create | `IEffectivePriceSource` over the default price list. |
| `src/Commerce.Cloud.Api/Endpoints/Pricing.cs` | Create | Price list CRUD/history, supplier mappings, import upload/review/commit/reject — `adminGroup`'s exact authorization shape. |
| `src/Commerce.Cloud.Api/Endpoints/Catalog.cs` | **Modify (rework)** | Real persisted product/presentation CRUD replacing the body-built `new Product(...)`; identification-code editing. |
| `src/Commerce.Cloud.Api/Endpoints/Ordering.cs` | Modify | `SubmitOrderLine` (price-free) replaces `OrderLineSnapshot` in `SubmitOrderRequest`. |
| `src/Commerce.Cloud.Api/Ordering/CloudOrderSubmissionService.cs` | Modify | Resolve after the four existing denial checks; `no-effective-price` denial. |
| `src/Commerce.Cloud.Api/Endpoints/Device.cs` | Modify | `GET /device/catalog/sync`. |
| `src/Commerce.Cloud.Api/Program.cs` | Modify | Register the stores, the price source, `PricingResolutionService`, `MapPricingEndpoints`. |
| `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` | Modify | All seven new tables (+ `relforcerowsecurity` + policy names). |
| `src/Commerce.Cloud.Api/Commerce.Cloud.Api.csproj` | Modify | `ClosedXML` package reference (licence confirmed at apply time). |
| `src/Commerce.BranchNode/BranchSyncStore.cs` | Modify | `catalog_replica`, `price_replica`, `sale_lines`, `sale_effects.sale_kind`; `ApplyCatalogPriceSync` + `SimulateInterruptedCatalogPriceSync`; `FindByCode`, `GetEffectivePrice`, `CommitScannedSale`. |
| `src/Commerce.BranchNode/BranchNodeService.cs` | Modify | `CompleteScannedSale(...)` alongside the existing `CompleteOfflineSale`. |
| `src/Commerce.Pos.Windows/LocalEffectivePriceSource.cs`, `CatalogPriceReplicaClient.cs` | Create | The POS half of the shared port; the device-bearer pull client. |
| `src/Commerce.Pos.Windows/MainWindow.xaml(.cs)` | **Modify (rework)** | Scan box + line `ListView` + computed total + "Commit scanned sale"; the existing `AmountTextBox`/`CommitSaleButton` demoted to a labelled fallback group; `PullCatalogPricesAsync()` after `PullCustomersAsync()`; stale-cache banner. |
| `src/Commerce.Pos.Windows/PosHostBuilder.cs` | Modify | Register the new client, the local price source, and the shared service. |
| `src/Commerce.Web/src/api/pricing.ts`, `catalog.ts`, `types.ts` | Create/Modify | Price list, history, mapping, and import clients; price-free order line DTO. |
| `src/Commerce.Web/src/screens/PriceListsScreen.tsx` (+ `PriceHistory.tsx`, `ImportReviewTable.tsx`) | Create | Prices / Suppliers / Import, admin-gated. |
| `src/Commerce.Web/src/screens/CatalogScreen.tsx` | **Modify (rework)** | Real presentation list + identification-code editing, replacing the hand-typed rename form. |
| `src/Commerce.Web/src/screens/OrderScreen.tsx`, `e2e/ordering.spec.ts` | Modify | Stop sending line shapes that no longer exist; show the server-resolved total. |
| `src/Commerce.Web/src/App.tsx`, `routes/AppLayout.tsx` | Modify | `/app/price-lists` inside the existing `RequireAdmin` block; nav tab. |
| `tests/Commerce.Cloud/PricingResolutionTests.cs`, `PriceListTests.cs`, `ImportGuardTests.cs`, `ImportMatcherTests.cs` | Create | Unit coverage. |
| `tests/Commerce.Integration/PricingEndpointTests.cs`, `SupplierImportTests.cs`, `OrderPricingTests.cs`, `MigrationRlsTests.cs` | Create/Modify | Endpoint, import, order-freeze, RLS and migration coverage. |
| `tests/Commerce.BranchNode/CatalogPriceReplicaTests.cs`, `ScannedSaleTests.cs` | Create | Replica atomicity, code lookup, offline resolution, sale-kind distinction. |

## Interfaces / Contracts

```sql
-- 0009_catalog_and_pricing.sql (excerpt: the non-obvious constraints only;
-- RLS/grants/policies mirror `customers` in 0008 exactly for all seven tables,
-- including the NULLIF(..., '')::uuid pooler hardening. NO DELETE is granted.)

CREATE TABLE IF NOT EXISTS presentations (
    id                  uuid PRIMARY KEY,
    organization_id     uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    product_id          uuid NOT NULL REFERENCES products (id) ON DELETE CASCADE,
    name                text NOT NULL,
    quantity_behavior   text NOT NULL CHECK (quantity_behavior IN ('Integral','Fractional')),
    unit_id             uuid NOT NULL,
    identification_code text NULL,
    updated_at_utc      timestamptz NOT NULL DEFAULT now()
);
-- Per-organization, partial: two orgs may legitimately share an EAN-13, and an
-- unlabelled presentation stays unconstrained. Enforced HERE, not in UI code.
CREATE UNIQUE INDEX IF NOT EXISTS presentations_org_code_uk
    ON presentations (organization_id, identification_code)
    WHERE identification_code IS NOT NULL;

CREATE TABLE IF NOT EXISTS price_lists (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL,
    is_default      boolean NOT NULL DEFAULT false,
    created_at_utc  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS price_lists_one_default
    ON price_lists (organization_id) WHERE is_default;

-- APPEND-ONLY. No effective_to: a supersede is an INSERT, never an UPDATE of
-- history. "No effective price" is exactly zero rows, not a gap between ranges.
CREATE TABLE IF NOT EXISTS price_list_entries (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    price_list_id      uuid NOT NULL REFERENCES price_lists (id) ON DELETE CASCADE,
    presentation_id    uuid NOT NULL REFERENCES presentations (id) ON DELETE CASCADE,
    unit_price         numeric(12,2) NOT NULL CHECK (unit_price > 0),
    effective_from     date NOT NULL,
    source             text NOT NULL DEFAULT 'Manual'
                            CHECK (source IN ('Manual','Import')),
    import_batch_id    uuid NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid NOT NULL,
    CONSTRAINT price_list_entries_one_per_day
        UNIQUE (price_list_id, presentation_id, effective_from)  -- 409, not a coin flip
);
CREATE INDEX IF NOT EXISTS price_list_entries_resolution_idx
    ON price_list_entries (price_list_id, presentation_id, effective_from DESC);

CREATE TABLE IF NOT EXISTS price_import_batches (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    supplier_mapping_id uuid NOT NULL REFERENCES supplier_price_mappings (id),
    file_name          text NOT NULL,
    row_count          integer NOT NULL,
    status             text NOT NULL DEFAULT 'Staged'
                            CHECK (status IN ('Staged','Committed','Rejected','Failed')),
    uploaded_at_utc    timestamptz NOT NULL DEFAULT now(),
    uploaded_by_user_id uuid NOT NULL,
    resolved_at_utc    timestamptz NULL
);
CREATE TABLE IF NOT EXISTS price_import_rows (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    batch_id        uuid NOT NULL REFERENCES price_import_batches (id) ON DELETE CASCADE,
    row_number      integer NOT NULL,
    raw_code        text NULL,
    raw_price       text NULL,
    presentation_id uuid NULL REFERENCES presentations (id) ON DELETE SET NULL,
    current_price   numeric(12,2) NULL,
    proposed_price  numeric(12,2) NULL,
    match_status    text NOT NULL CHECK (match_status IN
                        ('Matched','NoChange','UnknownCode','InvalidPrice','DuplicateInFile')),
    reject_reason   text NULL      -- every rejected row carries its reason; never silent
);
```

```csharp
// Commerce.Application/Pricing — the ADR-010 engine.
// NOTE the absence: no channel, no caller identity, no HttpContext. Channel
// independence is structural — there is no parameter that could differ.
public interface IEffectivePriceSource
{
    Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct);
}

public abstract record PriceResolutionOutcome
{
    public sealed record Resolved(
        decimal UnitListPrice, decimal AppliedDiscountPercentage,
        decimal UnitNetPrice, decimal LineTotal) : PriceResolutionOutcome;
    // Explicit and typed. There is no `decimal?` and no 0m fallback anywhere.
    public sealed record NoEffectivePrice(Guid PresentationId, DateOnly On) : PriceResolutionOutcome;
}

public sealed class PricingResolutionService
{
    // discountPercentage == null  => GUEST: official list price, no discount.
    // discountPercentage != null  => REGISTERED: the customer's own rate.
    public Task<PriceResolutionOutcome> ResolveAsync(
        Guid presentationId, decimal quantity, decimal? discountPercentage,
        DateOnly effectiveOn, CancellationToken ct);

    // The ONE rounding policy (Domain/Pricing/Money.cs). Applied exactly twice.
    // AwayFromZero, not .NET's default banker's rounding: retail money.
    internal static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

// Commerce.Domain/Ordering/OrderLineSnapshot.cs — frozen at submit (ADR-003)
public sealed record OrderLineSnapshot(
    Guid ProductId, string ProductName, Guid PresentationId, string PresentationName,
    QuantityBehavior QuantityBehavior, Guid UnitId, decimal Quantity,
    decimal UnitListPrice, decimal AppliedDiscountPercentage,
    decimal UnitNetPrice, decimal LineTotal);

// Commerce.Cloud.Api/Endpoints/Ordering.cs — the client cannot send a price:
// there is no member to put one in (the AccessEnabled-removal idiom).
public sealed record SubmitOrderLine(
    Guid ProductId, Guid PresentationId, decimal Quantity);
public sealed record SubmitOrderRequest(
    Guid OrderId, Guid CustomerId, Guid AccessCredential, Guid DestinationBranchId,
    Guid ActorId, IReadOnlyList<SubmitOrderLine> Lines, Guid CorrelationId);

// Commerce.Cloud.Api/Endpoints/Device.cs — ONE channel, ONE cursor: a catalog
// row and its price can never be replicated out of step with each other.
public sealed record CatalogSyncResponse(
    IReadOnlyList<CatalogReplicaRow> Items,
    IReadOnlyList<Guid> RemovedPresentationIds,
    DateTimeOffset ServerTimeUtc);
public sealed record CatalogReplicaRow(
    Guid PresentationId, Guid ProductId, string ProductName, string PresentationName,
    string? IdentificationCode, string QuantityBehavior, Guid UnitId,
    decimal? UnitPrice, DateOnly? EffectiveFrom, DateTimeOffset UpdatedAtUtc);
```

```sql
-- BranchSyncStore additions (the customers_replica shape, reused verbatim)
CREATE TABLE IF NOT EXISTS catalog_replica (
    presentation_id TEXT PRIMARY KEY, organization_id TEXT NOT NULL,
    product_id TEXT NOT NULL, product_name TEXT NOT NULL, presentation_name TEXT NOT NULL,
    identification_code TEXT NULL, quantity_behavior TEXT NOT NULL,
    unit_id TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS catalog_replica_code_uk
    ON catalog_replica (organization_id, identification_code)
    WHERE identification_code IS NOT NULL;   -- ambiguous scan impossible offline too
CREATE TABLE IF NOT EXISTS price_replica (       -- CURRENT price only, never history
    presentation_id TEXT PRIMARY KEY, organization_id TEXT NOT NULL,
    unit_price TEXT NOT NULL, effective_from TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS sale_lines (
    sale_id TEXT NOT NULL, line_number INTEGER NOT NULL, presentation_id TEXT NOT NULL,
    identification_code TEXT NULL, product_name TEXT NOT NULL, presentation_name TEXT NOT NULL,
    quantity TEXT NOT NULL, unit_price TEXT NOT NULL, line_total TEXT NOT NULL,
    PRIMARY KEY (sale_id, line_number));
-- A fallback sale is NOT silently indistinguishable from a priced one:
ALTER TABLE sale_effects ADD COLUMN sale_kind TEXT NOT NULL DEFAULT 'Manual';
-- `sync_cursors` is REUSED: BranchSyncStore.CatalogPricesChannel = "catalog-prices".
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | **ADR-010 divergence**: guest (`null` discount) returns the list price; the same tuple with a 10% customer returns a strictly lower price | xUnit, stub `IEffectivePriceSource` |
| Unit | **Channel parity**: the same tuple resolved through the Postgres-backed and the SQLite-backed source yields byte-identical `Resolved` values | xUnit + temp SQLite + live Postgres |
| Unit | `NoEffectivePrice` for a date before the earliest entry, and for a presentation with zero entries — asserted as the typed outcome, never `0m` | xUnit |
| Unit | Rounding: `Round2` is AwayFromZero; a 3-line sale's total equals the sum of rounded line totals | xUnit |
| Unit | Append-only: publishing a second price leaves the first retrievable at its own date | xUnit + integration |
| Unit | Import guards: 6 MB file, 6 000-row sheet, `.zip` renamed to `.xlsx`, missing sheet, non-numeric price, negative price, absurd price — each rejected with its own reason, no batch row created | xUnit with fixture files |
| Unit | Import matcher: unknown code ⇒ `UnknownCode`; identical price ⇒ `NoChange`; the same code twice ⇒ `DuplicateInFile` | xUnit |
| Integration | `POST /orders` freezes list/discount/net/total; publishing a new price afterwards leaves the submitted order unchanged | `WebApplicationFactory` + live Postgres |
| Integration | An order line for a presentation with no effective price is denied `no-effective-price`, and **no order is stored** | same |
| Integration | Import lifecycle: upload ⇒ `Staged` and **zero** live price changes; commit ⇒ entries appear via the normal write path + audit row; reject ⇒ no price written; committing twice ⇒ 409 | same |
| Integration (RLS) | Org B cannot read/insert/update org A's `price_lists`, `price_list_entries`, `presentations`, `products`, or import tables; `app_runtime` holds **no `DELETE`** on any of the seven; `presentations_org_code_uk` rejects a duplicate code within an org and **permits** the same code in another org | `MigrationRlsTests`, direct connections |
| Integration (migration) | `0009` applies twice cleanly; `/health/ready` fails before it and passes after | same |
| Integration | `GET /device/catalog/sync`: device bearer required; org from the stored credential row, not the query; `since` filtering; a removed presentation appears in `removedPresentationIds` | same |
| Unit (BranchNode) | `ApplyCatalogPriceSync` is atomic (the `SimulateInterrupted…` twin leaves replica **and** cursor byte-identical); code lookup hits; a scanned sale writes `sale_kind='Scanned'` + lines; a manual sale writes `sale_kind='Manual'` + zero lines | xUnit + temp SQLite |
| Web (Vitest) | `PriceListsScreen` lists/creates/history-expands; the review table renders one row per status with the right badge; Commit is disabled with zero `Matched` rows; a `seller` is redirected by `RequireAdmin`; `CatalogScreen` edits the identification code | Vitest + Testing Library |
| Web (E2E) | `ordering.spec.ts` updated to the price-free line shape and still passing | Playwright |
| Manual | Desktop runbook: scan a known code offline ⇒ item + price + line; scan an unknown code ⇒ explicit message, no line; commit a scanned sale and a manual sale and confirm the two are distinguishable in `branch.db` | Runbook step — no WPF UI harness exists |

## Threat Matrix

| Native row | Applicability |
|---|---|
| Documentation-like paths | N/A — no file-classification boundary. |
| Git repository selection / Commit state / Push state / PR commands | N/A — no product code runs Git or PR automation. |
| **Routing** | **Applicable** — new admin routes under `/pricing` (price lists, history, supplier mappings, import upload/review/commit/reject), a reworked `/catalog` group, and one new device-bearer route `GET /device/catalog/sync`. Safe behavior: `/pricing` and `/catalog` reuse `Account.cs`'s `adminGroup` shape verbatim (`RequireAuthorization` default cookie + `TenantScopeEndpointFilter` + store-loaded caller + permission check), so a `seller` cookie, a platform cookie (`Path=/platform`, not sent), and a device bearer token authenticate nothing there; `/device/catalog/sync` requires the `DeviceBearer` policy and derives org from the stored `device_credentials` row, never the query. `POST /orders` gains a denial path that reveals nothing about catalog/price existence to an unauthorized caller because resolution runs strictly **after** every access check. RED tests: `seller` ⇒ 403 on every `/pricing` verb; cookie-less and device-token calls on `/pricing` ⇒ 401; cross-org price list ⇒ 404; `no-effective-price` denial stores no order. |
| **Executable-file classification** | **Applicable** — the import endpoint accepts an operator-supplied file. It is **not** executed and **not** classified by extension alone: OpenXML magic bytes are checked, size (≤5 MB) and used-range row count (≤5 000) are bounded *before* cell materialization, exactly one mapping-named sheet is read, cached values are read with **no formula recalculation**, and ClosedXML's OpenXML-only reader means no legacy OLE/BIFF parser is reachable. A failed file creates no batch row. RED tests: `.zip` renamed `.xlsx` ⇒ rejected; oversized file ⇒ rejected; oversized used range ⇒ rejected before expansion; a formula cell ⇒ its cached value is read, never evaluated. |
| **Process integration** | **Applicable** — one new outbound HTTP dependency from `Commerce.Pos.Windows` (`CatalogPriceReplicaClient` → `/device/catalog/sync`, device bearer). Safe behavior: the device token is read in exactly one new expression, inside `PullCatalogPricesAsync`, preserving `MainWindow`'s documented "sync-blocked, not sales-blocked" structural property — **both** commit paths still make zero HTTP calls and zero token reads. Every network failure leaves the replica and cursor byte-identical. RED tests: an unreachable host leaves `catalog_replica`, `price_replica` and the cursor unchanged; a 401 does not advance the cursor; a scanned sale commits with the network down. |
| Shell / subprocess | N/A — none introduced. |

## Migration / Rollout

Forward-only. Per environment, **before** deploying the new image: apply
`0009_catalog_and_pricing.sql` via `psql` against the direct (non-pooled)
connection, following `0008`'s exact convention. `/health/ready` fails closed
until all seven tables exist with `FORCE ROW LEVEL SECURITY` and their policies,
so ordering is enforced by the gate rather than by discipline.

**No data migration**: all seven tables are new and `presentations` is new, so
`identification_code` has no existing rows to backfill. There is no catalog data
to migrate **because there is no catalog storage today** — the first products
and presentations are created through the reworked `/catalog` endpoints after
deploy.

**Order-of-deploy hazard, and it is real.** The moment the new image is live,
`POST /orders` denies every line whose presentation has no effective price. Since
no `price_list_entries` row can exist before an admin publishes one, **every
order submission is denied until a default price list exists and carries a price
for each ordered presentation.** That is the correct fail-closed behavior (the
proposal forbids a zero fallback), but it must be in the deploy note: create the
default price list and publish prices immediately after deploy, before
announcing ordering.

**POS terminals** keep working unchanged through the manual-total path from the
first launch; the scan path stays empty until the first successful
`PullCatalogPricesAsync`, which is a safe default. `branch.db` gains its new
tables via the existing `CREATE TABLE IF NOT EXISTS` block and `sale_effects`
gains `sale_kind` with a `'Manual'` default, so pre-change sales read as manual
— which is exactly what they were.

Rollback: revert the commit, then run `0009`'s inverse block (shipped as
comments in the file). **Lossy after first real use**, as the proposal states:
line-item sales and price-carrying order snapshots cannot be re-expressed in the
old model. Narrower rollbacks: disable `MapPricingEndpoints`' import routes
alone; or hide the POS scan group while keeping the server-side engine.

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | **Catalog persistence** (the verified deviation): `0009` part A — `products`, `presentations` (+ code column and partial unique index) with RLS/grants/policies + `init-rls.sql` parity + readiness + `MigrationRlsTests`; `PostgresCatalogStore` + `CatalogRecords`; `Presentation.IdentificationCode`; reworked `Catalog.cs` CRUD | ~620 | Tables exist with forced RLS; duplicate code within an org rejected, permitted across orgs; catalog CRUD round-trips | Revert; run the inverse block |
| 2 | **Pricing schema + aggregate**: `0009` part B — `price_lists`, `price_list_entries` + RLS/grants/`MigrationRlsTests`; `PriceList`/`PriceListEntry`/`Money`; `PostgresPriceListStore` (append, effective, history) | ~480 | Append-only proven: superseding preserves history; one default per org; no `DELETE` grant | Revert; tables become unused, not broken |
| 3 | **The engine**: `PricingResolutionService`, `IEffectivePriceSource`, `PriceResolutionOutcome`, `PostgresEffectivePriceSource`, rounding policy + the ADR-010 divergence, channel-parity, no-effective-price and rounding unit tests | ~360 | Guest vs. registered divergence green; `NoEffectivePrice` typed, never `0m`; no channel parameter exists | Revert; nothing consumes it yet |
| 4 | **Order integration**: `OrderLineSnapshot` fields, `SubmitOrderLine`, `Ordering.cs`, `CloudOrderSubmissionService` resolution placement, `OrderSnapshotFactory`, `OrderScreen.tsx` + `ordering.spec.ts`, integration tests | ~430 | Snapshot freeze proven against a later price change; `no-effective-price` stores no order; no client can send a price | Revert; orders return to price-free |
| 5 | **Pricing admin API**: `Endpoints/Pricing.cs` (price CRUD, history, supplier mappings) + audit wiring + `Program.cs` + integration/RLS tests | ~420 | `ManageUsers` gate, cross-org 404, audit row per publish | Revert; aggregate stays, unreachable over HTTP |
| 6 | **BranchNode replication**: `GET /device/catalog/sync`, `catalog_replica`/`price_replica`/`sync_cursors` channel, `ApplyCatalogPriceSync` + interrupted twin, `CatalogPriceReplicaClient`, `PullCatalogPricesAsync` in `SyncButton_Click`, BranchNode tests | ~560 | Atomic apply; unreachable/401 leaves replica+cursor byte-identical; `since` filtering | Revert POS+device slice; cloud unaffected |
| 7 | **POS scan rework**: scan box, line `ListView`, computed total, `LocalEffectivePriceSource`, `sale_lines` + `sale_kind`, `CommitScannedSale`, the demoted manual fallback group, stale banner, `PosHostBuilder`, BranchNode sale tests | ~640 | Offline scan prices identically to cloud; unknown code/no price append nothing; the two sale kinds are distinguishable in `branch.db` | Hide the scan group; manual path survives |
| 8 | **Web pricing UI**: `api/pricing.ts`, `PriceListsScreen` + `PriceHistory` + `ImportReviewTable`, `CatalogScreen` rework, `App.tsx`/`AppLayout` routing, Vitest specs | ~700 | Admin sees and uses the screen; a `seller` is redirected; history expands; review badges correct | Revert the web slice only |
| 9 | **Excel import pipeline**: `ClosedXML` reference, `0009` part C — `supplier_price_mappings`, `price_import_batches`, `price_import_rows` + RLS; `ImportGuards`/`Parser`/`Matcher`; upload/review/commit/reject endpoints; guard + matcher + lifecycle tests | ~760 | Every guard rejects with its own reason and creates no batch; staged batch changes zero live prices; commit uses the normal write path | Disable the import routes; manual pricing survives |

**Ordering rationale (verified, adjusted from the brief's suggestion).** Catalog
persistence moves to Unit 1 — it did not appear in the brief's suggested order
because the proposal assumed it existed; nothing downstream (price entries, code
uniqueness, import matching, POS scan) can compile or be tested without it.
Units 2–3 then split what the brief bundled as one: the store is inert without
the engine, and the engine is the single highest-value reviewable in the change,
so it must not be buried inside a schema diff. Unit 5 (admin API) lands **after**
Unit 4 so the first thing that can publish a price already has a correct
resolver behind it. Units 6→7 keep their dependency direction (nothing to scan
before there is a replica). Unit 8 (web) and Unit 9 (import) move to the end
because import *depends on* the review UI being reviewable and on the matcher
having a persisted catalog and a working write path; the import pipeline is also
the single most self-contained slice and the safest to defer or drop if the
change has to be narrowed under pressure.

## Review Workload Forecast

Decision needed before apply: No
Chained PRs recommended: Yes
400-line budget risk: High

**~4 970 authored lines, roughly 12.4× the 400-line review budget.** This is the
largest change in the repository's history to date and is ~1.7× the
`commerce-customer-identity` change that was already delivered as a chain. The
honest forecast: nine dependency-ordered slices, none of which is itself under
budget, and three of which (1, 7, 9) are over 600 lines on their own.

The user has **already chosen a single PR with an accepted size exception** for
this change; this forecast does not reopen that decision and no further input is
required before apply. What an honest forecast still owes:

- A single PR of ~5 000 lines will not receive meaningful line-by-line review.
  The mitigation that remains available inside a single PR is **nine separately
  labelled, dependency-ordered commit ranges matching the Work Units above**, so
  a reviewer can read Unit 3 (the money engine) and Unit 9 (the untrusted-input
  boundary) as isolated diffs. Units 3 and 9 carry essentially all of the
  change's risk and should be reviewed as if they were their own PRs.
- Units 1, 2, 5, 6 are additive and inert-until-wired; Units 4, 7, 8 rework
  existing working paths and are where regression risk concentrates.
- If the size exception is revisited before apply, the natural cut is
  1→2→3→4→5 (the server-side money model, ~2 310 lines, fully valuable on its
  own and shippable) followed by 6→7→8→9 (the clients and the import
  accelerator). Unit 9 is droppable entirely without breaking any other unit.

## Open Questions

- [ ] **Needs acknowledgement, not a new decision**: the proposal's scope
      assumes a persisted catalog to add an identification code to and to match
      import rows against. Verified: **no `products`/`presentations` table and
      no catalog store exists** — `Catalog.cs` builds `Product` objects from the
      request body and `CatalogScreen.tsx` is a hand-typed form. Work Unit 1
      builds catalog persistence, which is the honest reason this change is
      ~1 200 lines larger than the proposal forecast. Confirm before apply.
- [ ] **Licence check, must be closed by `sdd-apply`**: ClosedXML is selected as
      MIT-licensed against EPPlus's post-v5 commercial licence. That comparison
      is carried from the exploration phase and was **not** re-verified online in
      this session. Confirm the package's actual `LICENSE` before the
      `PackageReference` lands; if it has changed, `DocumentFormat.OpenXml` is
      the fallback and Unit 9's budget grows.
- [ ] Non-blocking: append-only effective-dating cannot express "this price
      expires on date X and nothing replaces it". Nothing in this change needs
      it; a withdrawal sentinel row is the additive follow-up if it is ever
      needed.
- [ ] Non-blocking: `CustomerKind` selects no price list in this change
      (resolution uses the org's default list and `DiscountPercentage` only). A
      kind→list mapping belongs to the same future ADR as promotions and
      quantity breaks.
- [ ] Non-blocking: a scanned sale and a manual sale cannot be mixed in one
      transaction. Per-line provenance would be needed; deliberately out of
      scope, and the UI blocks the combination explicitly rather than silently.
