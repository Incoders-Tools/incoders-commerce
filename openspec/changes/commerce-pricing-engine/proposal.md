# Proposal: Commerce Pricing Engine

## Intent

**There is no price anywhere in this product.** Verified this session: no price field exists on `Product`, `Presentation`, `OrderLineSnapshot`, `SubmitOrderRequest`, or any migration. The only money-shaped value in the system is `SaleEffect.TotalAmount` — an opaque number a cashier **types by hand** into `AmountTextBox` (`MainWindow.xaml.cs`, `CommitSaleButton_Click`), with zero reference to the catalog. POS sales and catalog Orders are today two unrelated models that never meet.

So ADR-010 ("price is resolved server-side, in one place") currently has nothing to resolve, ADR-003's snapshot-on-submit freezes a commercial context with no commercial values in it, and Phase B's `Customer.DiscountPercentage` is dead data carried for a consumer that does not exist.

**Be honest about size.** This is not "add a discount calculator". It is the change that introduces money into the domain: a versioned price aggregate, an import pipeline for untrusted supplier files, the resolution engine itself, an identification key on the catalog, offline price replication to branches, and a rework of the one POS sale path that currently works. The user was offered a three-way split, saw the scope, and explicitly chose one change, one PR. Treat the blast radius as real, not theoretical.

**Why now.** Phase A (ADRs) and Phase B (`Customer`) are done on this branch. Every downstream surface — guest-vs-registered ordering (Phase D), invoicing, reporting — is blocked on a price existing. And operationally, the user re-keys supplier price lists from Excel by hand today; that manual cost is the concrete pain driving the import requirement.

## Scope

### In Scope

- **Catalog persistence** (added after design's own verification, confirmed by the user). `Product`/`Presentation` today are constructed entirely from the request body on every call (`Catalog.cs`) — there is no `products`/`presentations` table anywhere. This was not in the original proposal's scope; design surfaced it as a real prerequisite (nothing to attach a price or a barcode to otherwise) and the user confirmed it belongs in this same change rather than a separate one. This is Work Unit 1 and the primary reason the change grew past the original estimate.
- **Versioned, effective-dated `PriceList` aggregate** (locked decision). Argentine list prices change constantly and history must be auditable, so this is **not** a mutable scalar on `Presentation`. Price entries are append-only/effective-dated; the "current" price is *resolved by date*, never overwritten. Design owns the exact shape (`PriceListEntry` with `EffectiveFrom`/`EffectiveTo` vs. append-only history) but must not collapse it into one overwritable column. Organization-scoped with RLS per `PostgresOrganizationStore` convention.
- **Admin web screen for price lists** — create/view/edit entries, view an item's price history. Required independently of import; import is an accelerator, not the only entry path.
- **Excel supplier-price import pipeline** — upload a supplier's `.xlsx`, parse rows, match them to existing Products/Presentations, stage proposed `PriceListEntry` rows, and surface them for admin review before they take effect. The file is an **untrusted input from outside the trust boundary**: it is parsed, validated, and diffed — never blind-upserted into live prices. Exact supplier format and the row→catalog matching key are open questions (see below), not invented here.
- **`PricingResolutionService`** implementing ADR-010 exactly: resolves `(customer context, product/presentation, quantity)` → effective price. Guest = official list price, no discount. Registered = list price adjusted by `Customer.DiscountPercentage`. **Channel is never an input**; web, POS, and admin console MUST get the same price for the same tuple, proven by test.
- **`OrderLineSnapshot` extended** with resolved unit price, applied discount, and line total, frozen at submit per ADR-003. `SubmitOrderRequest` still sends **no price** — the client never computes one (ADR-010; a client-side calculation is a defect, not a shortcut).
- **Identification code (barcode/SKU) on the catalog domain** — new field + migration + admin editing, so a scanned code resolves to a catalog item. Uniqueness scope is an open question (see below).
- **POS scan-to-sell rework** — a scan input field (keyboard-wedge assumption, below) resolves code → product + presentation + **current locally-cached price**, displays the item's main data and price on screen, and appends it as a real sale **line**. The free-text `AmountTextBox` total flow is replaced by line-item composition with a computed total. This is a rework of the existing working sale path, not an additive feature.
- **BranchNode pricing replication** — extend the existing cloud→local read-replication channel (the pattern `commerce-customer-identity` already established for customers) to carry currently-effective prices + catalog identification codes to each branch. Staleness is governed by **ADR-002's existing freshness policy**; this change MUST NOT invent a second freshness mechanism.

### Out of Scope (non-goals)

- **Promotions, quantity-break pricing, and manual price override** — ADR-010 defers these to their own ADR. Quantity is part of the resolution *signature* so the engine does not need re-plumbing later, but no quantity-tier rule ships.
- **Multi-currency / FX.** Single currency, implicit, as today.
- **Any specific supplier's Excel layout being pre-decided.** Open question (a) — inventing a parser for a format nobody confirmed is the fastest way to build the wrong thing.
- **Barcode scanner hardware driver / vendor SDK integration.** Verified: no barcode SDK, ZXing, or scanner library exists in the repo; the only references are architecture diagrams describing "Scanner HID/keyboard". **Keyboard-wedge (scanner emulates a keyboard, terminates with Enter) is therefore the correct minimal assumption** — a focused text field with fast Enter-triggered lookup. Stated as an assumption, correctable by the user.
- Cost/margin tracking, supplier entity, purchase orders.
- Invoicing/AFIP, taxes on the line, payments/settlement (ADR-003 keeps these out).
- Per-branch price divergence — `Product`/`Presentation` are organization-scoped today (no `BranchId`); one org-wide price stands.
- Guest checkout surface itself (Phase D). This change makes guest pricing *resolvable*, not reachable.

## Decisions (confirmed by the user)

| # | Decision | Answer |
|---|---|---|
| a | Supplier Excel format | **Configurable per supplier** — each supplier gets a saved column-mapping (once configured, later imports from that supplier reuse it). Not a single fixed layout. |
| b | Row → catalog matching | **By barcode/SKU** — a row matches an existing Presentation via the identification code added by this same change. Rows with no matching code are reported as unmatched, not guessed at. |
| c | Barcode/SKU scope | **Per Presentation**, not per Product. A scan resolves to exactly one sellable line with no follow-up "which presentation?" prompt at the POS. |
| d | Import activation | **Review before applying.** An import lands as a reviewable staged batch; an admin confirms it before any price goes live. No import is ever auto-applied. |
| e | `Customer.PaymentTerms` | **Stays deferred / free text.** Confirmed: it does not participate in ADR-010's resolution tuple, purely informational. Revisit only if a future phase (payments/invoicing) needs it structured. |

**Additional decision (not asked, orchestrator default — low-stakes, reversible)**: the existing manual-total `AmountTextBox` sale path is **kept as a fallback** for miscellaneous/unlisted items (not removed), alongside the new scan-to-line flow. This avoids a hard blocker if a cashier needs to ring up something with no barcode yet, without reopening the "does the old path survive" risk as an open question.

Assumptions pending correction: (1) one org-wide currency; (2) keyboard-wedge scanner input; (3) a price is per `Presentation` (the sellable unit), not per `Product`; (4) an item with no effective price for the resolution date is an explicit, visible error at the POS — never a silent zero.

## Capabilities

### New Capabilities
- `price-list-management`: versioned, effective-dated price entries; org-scoped persistence with RLS; admin create/edit/history; append-only semantics (no destructive overwrite).
- `supplier-price-import`: upload/parse/validate a supplier Excel file, match rows to catalog items, stage and review proposed entries, commit or reject; untrusted-input handling.
- `pricing-resolution`: the ADR-010 engine — one server-side resolver, guest vs. registered branching, channel-independence, snapshot-on-submit output.
- `catalog-item-identification`: barcode/SKU on the catalog, uniqueness rules, admin editing, lookup by code.
- `pos-scan-sale`: scan → display item + current price → compose line-item sale with a computed total, offline-capable against the local cache.

### Modified Capabilities
- `private-customer-ordering`: `OrderLineSnapshot` MUST carry resolved unit price, applied discount, and line total; the submission request MUST NOT accept a caller-supplied price; catalogue semantics gain the identification code.
- `branch-offline-sync`: cloud→local replication extends to effective prices and identification codes; the local sale becomes line-item based with resolved prices; price-cache staleness is governed by the **existing** ADR-002 freshness policy.
- `organization-persistence`: new org-scoped price/identification tables follow the established RLS and store conventions.
- `customer-registry`: touched only if open question (e) resolves toward structuring `PaymentTerms`; otherwise unchanged.

## Approach

Build the money model server-side first, then let each client read it — never compute it.

The `PriceList` aggregate lands in `src/Commerce.Domain/Pricing/` with a Postgres store mirroring `PostgresCustomerStore`/`PostgresOrganizationStore` exactly; no new persistence idiom. A single migration adds the price tables and the catalog identification column.

`PricingResolutionService` sits in `src/Commerce.Application/Pricing/` as the **only** place a price is computed. Order submission calls it server-side and writes the result into the extended `OrderLineSnapshot`. The guest-vs-registered divergence test that ADR-010 demands is written against this service, plus a channel-parity test asserting identical output for the same tuple regardless of caller.

The Excel import is a staged pipeline — parse → match → stage → review → commit — so the untrusted-file boundary never touches live prices directly. It reuses the price aggregate's normal write path at commit time rather than bulk-writing around it.

The POS consumes replicated prices from its local cache; resolution logic is shared with the cloud so an offline sale prices identically to an online one (ADR-002 keeps the branch authoritative for its own sale). The scan flow is a lookup against the local cache, not a network call.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Commerce.Domain/Pricing/*` | New | `PriceList` / `PriceListEntry`, effective-dating, resolution value objects |
| `src/Commerce.Application/Pricing/PricingResolutionService.cs` | New | ADR-010 engine, sole price authority |
| `src/Commerce.Application/Pricing/Import/*` | New | Excel parse, match, stage, review, commit |
| `src/Commerce.Domain/Catalog/Presentation.cs` (and/or `Product.cs`) | Modified | Identification code (barcode/SKU) — scope per open question (c) |
| `src/Commerce.Domain/Ordering/OrderLineSnapshot.cs` | Modified | Resolved unit price, applied discount, line total |
| `src/Commerce.Cloud.Api/Endpoints/Ordering.cs` | Modified | Server-side resolution on submit; still no client-supplied price |
| `src/Commerce.Cloud.Api/Endpoints/Pricing.cs` | New | Admin price CRUD, history, import upload/review/commit |
| `src/Commerce.Cloud.Api/Persistence/PostgresPriceListStore.cs` | New | Org-scoped store, RLS, mirrors existing store conventions |
| `src/Commerce.Web/src/screens/PriceListsScreen.tsx` (+ routes) | New | Admin price management and import review |
| `src/Commerce.Web/src/screens/CatalogScreen.tsx` | Modified | Identification code editing; price visibility |
| `src/Commerce.Pos.Windows/MainWindow.xaml(.cs)` | **Modified (rework)** | Scan input, item+price display, line-item sale replacing free-text total |
| `src/Commerce.BranchNode/*` | Modified | Replicate effective prices + identification codes; reuse ADR-002 freshness |
| `deploy/db/migrations/0009_*.sql` | New | Price tables, identification column, order line price columns |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| **Pricing-resolution correctness** — a wrong price is a money defect, silent and customer-visible | High | Resolution exists in exactly one service; guest-vs-registered divergence test and channel-parity test are mandatory success criteria, not optional coverage |
| **Import trust boundary** — an uploaded Excel file is untrusted input (malformed rows, absurd values, wrong file entirely, oversized upload, formula/zip-bomb payloads) | High | Staged pipeline with mandatory review before commit; size/type validation; parse in a non-evaluating reader; every rejected row reported, never silently skipped |
| POS sale-flow rework alongside the kept fallback | Medium | The manual-total flow stays available for miscellaneous/unlisted items (decided above); design must keep the two paths clearly distinguishable in the sale record (a fallback sale is not silently indistinguishable from a priced, catalog-backed one) |
| Offline price staleness sells at a wrong (old) price | Medium | ADR-002's freshness policy applies verbatim; no second mechanism. Stale-cache behavior at the POS must be explicit and visible |
| Effective-dating gaps — item with no valid entry for today's date | Medium | Explicit "no effective price" error at resolution, never a zero/null fallback; success criteria cover it |
| Missing/duplicate barcodes make scans ambiguous | Medium | Uniqueness rule settled by open question (c) and enforced at the DB level, not in UI code |
| Decimal/rounding drift between line totals and sale total | Medium | One rounding policy defined in design, applied server-side; no client-side arithmetic |
| **Change size** — new aggregate + import pipeline + engine + catalog change + two UIs + POS rework + sync | High | Far above the 400-line review budget. `sdd-tasks` must forecast explicitly and recommend chained/stacked slices; the user chose a single PR, so this tension needs an explicit, recorded decision before apply |
| Depends on unmerged Phase B branch | Medium | Built on `feat/customer-identity`; must not be merged to `main` ahead of it |

## Rollback Plan

Revert the commit: pricing domain, service, endpoints, and screens disappear; `OrderLineSnapshot` and `SubmitOrderRequest` return to price-free; the POS returns to the manual-total sale. Migration `0009` ships inverse statements — drop price tables, drop the identification column, drop the order-line price columns. **Caveat**: sales committed as line-item sales and orders carrying snapshotted prices cannot be re-expressed in the old model, so rollback is genuinely lossy once real transactions exist. Prefer forward-fix after first real use. Narrower rollbacks: disable the import endpoint alone; or feature-gate the POS scan flow while keeping the server-side engine.

## Dependencies

- ADR-010 (pricing), ADR-003 (snapshot-on-submit), ADR-002 (offline freshness) — accepted, not re-litigated.
- `commerce-customer-identity` (Phase B, same branch, unmerged) — `Customer.CustomerKind` and `DiscountPercentage` are resolution inputs.
- An Excel reading library must be selected (none present in the repo today) — design owns the choice and its licensing.

## Success Criteria

- [x] A price list entry created with an effective date is retrievable, and superseding it preserves the prior entry as history (no destructive overwrite). — `PriceListEntries_SupersedingPrice_PreservesPriorEntryAsHistory` (Phase 2)
- [x] The same (item, quantity) tuple resolves to an identical price via web, POS, and admin console — channel is provably not an input. — `PricingChannelParityTests` (Phase 3): Postgres- vs SQLite-backed `IEffectivePriceSource`, byte-identical `Resolved` output; no channel/caller-identity parameter exists in `PricingResolutionService.ResolveAsync`'s signature.
- [x] A guest resolution returns the official list price; the same item for a registered customer with a discount returns a strictly lower price (ADR-010's mandated divergence test). — `PricingResolutionTests` (Phase 3)
- [x] No client sends a price; a submission attempting to supply one is rejected. — `SubmitOrderLine` has no price member (structural); `OrderPricingTests` (Phase 4)
- [x] A submitted order freezes unit price, discount, and line total; changing the price list afterwards does not alter that order. — freeze test, task 4.9
- [x] An item with no effective price for the resolution date produces an explicit error, never a zero. — `PriceResolutionOutcome.NoEffectivePrice` (Phase 3) + `no-effective-price` order denial (Phase 4)
- [x] A supplier Excel upload produces a reviewable set of proposed changes; nothing reaches live prices without passing the review step (per open question (d)). — `SupplierImportTests.Upload_StagesTheBatch_AndChangesZeroLivePrices` (Phase 9)
- [x] A malformed/wrong-format upload is rejected with per-row reasons and changes no price. — `ImportGuardTests` (file-level reasons before any row is read) + `ImportMatcherTests`/`price_import_rows.match_status` (per-row reasons: `UnknownCode`/`InvalidPrice`/`DuplicateInFile` — the spec's own wording is "per-row OR per-file reasons"); `Upload_MalformedFile_CreatesNoBatchRow` (Phase 9)
- [x] Scanning a code at the POS displays the item's main data and its current price from the local cache, with no connectivity. — Phase 7 scan flow + manual runbook (no automated WPF UI harness exists in this repo, consistent with the PairingWindow/OperatorLoginWindow precedent)
- [x] A POS sale composed from scanned lines commits with a total computed from resolved line prices, not a typed number. — `ScannedSaleTests` (Phase 7)
- [x] Offline price-cache staleness behaves per ADR-002's freshness policy, with no second freshness mechanism introduced. — reuses `CachedOperator.Ttl` (Phase 7), no new mechanism added
- [x] RLS: a second-organization fixture cannot read or write another org's price lists. — `PriceListEntries_CrossOrganizationRead_ReturnsZeroRows` (Phase 2) + `SupplierMappingsAndImportBatches_CrossOrganizationRead_ReturnsZeroRows` (Phase 9)
- [x] `dotnet test Commerce.sln` and `dotnet build Commerce.sln` pass. — 455/455 tests, 0 build errors (see Phase 10 apply-progress for the disclosed NU1903 transitive-advisory note)
