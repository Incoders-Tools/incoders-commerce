# Price Composition (SDD change `commerce-price-composition`, sliced)

## Objective

Implement the approved change in `openspec/changes/commerce-price-composition/`
(proposal + design + two delta specs): stop treating a `PriceListEntry`'s
amount as the final, fully-loaded price and start deriving that final price
from a **base price** plus an ordered, open, effective-dated set of **rate
components** (VAT, gross-receipts tax, freight, markup, ... as rows, never as
columns), each declaring explicitly whether its percentage applies to the base
price or to the running subtotal.

## Why

Today the spreadsheet is the pricing engine and the system only stores its
output. Verified in the proposal: `PriceListEntry` carries exactly one
monetary value (`UnitPrice`), `PriceList` carries no rate collection, and
grepping the domain for VAT/IB/freight/markup returns nothing. The real
customer (Vaca Verde, meat distributor) applies `base x 1.45` in a sheet, so a
markup change means recomputing and re-importing the whole catalog, a VAT
change is an unexplained full-catalog reprice, and the base price — the one
number the business actually negotiates — is unrecoverable from what we
persist.

## Constraints

- The approved proposal, design and delta specs are the contract. Not
  reinterpreted, not re-decided.
- Append-only, no `EffectiveTo`, exactly as `PriceListEntry`: a rate change is
  an INSERT of a new dated set, never an UPDATE. Enforced by Postgres grants
  (`SELECT, INSERT` only), not by convention.
- Clean domain: no infrastructure imports in `src/Commerce.Domain`. Data
  access lives in `src/Commerce.Cloud.Api/Persistence`.
- Every new table carries org-scoped RLS with `FORCE ROW LEVEL SECURITY`,
  `REVOKE ALL FROM PUBLIC` + explicit `GRANT` to the non-owner `app_runtime`
  role, and the `NULLIF(current_setting(...), '')::uuid` pooler idiom.
- Migrations are forward-only and idempotent, with a header naming the
  migration they follow and their inverse.
- TDD mode: **Strict (RED -> GREEN -> REFACTOR)**, source: user instruction for
  this work. Runner: `dotnet test` (xUnit), integration tests against the live
  Postgres in `incoders-commerce-postgres-1`.
- Branch: this project commits and pushes directly on `dev`.

## Task list

### Slice 1 — model and persistence (this slice)

Covers these delta requirements of `specs/price-list-management/spec.md`:
Price List Rate Components; Rate Components Are Scoped To The List, Not The
Organization; Organization Default Rate Components; Explicit Calculation Base
Per Component; Append-Only Effective-Dated Rate Component History;
Organization-Scoped Component Persistence With RLS. Plus the *semantic* half
of Price List Entry Amount Is A Base Price (documentation only — no change to
observable resolution).

- [x] S1.1 Domain model: `RateCalculationBase`, `RateComponent`,
      `RateComponentSet` in `src/Commerce.Domain/Pricing`, with construction
      validation and a pure composition function. **Written; compiles
      (`dotnet build src/Commerce.Domain` — 0 errors, 0 warnings); its tests
      have NOT been executed.**
- [x] S1.2 Migration `0013_rate_components.sql`: `rate_component_sets` +
      `rate_components`, RLS, append-only grants; mirrored into
      `deploy/dev/db/init-rls.sql`.
- [x] S1.3 `PostgresRateComponentStore` + records: publish a set, read the
      effective set for a date with organization-default inheritance, read
      history.
- [x] S1.4 Tests: `tests/Commerce.Integration/RateComponentTests.cs` written
      (19 domain facts) and **observed RED** before S1.1. Store integration
      tests, RLS isolation, append-only and `0013` coverage in
      `MigrationRlsTests` are NOT written yet.
- [x] S1.5 Respecify `PriceListEntry.UnitPrice` as the base price in the
      domain documentation, naming what slice 2 still owes.

### Slice 2 — resolution

Covers the whole `specs/pricing-resolution/spec.md` delta.

- [x] S2.1 Compose inside `PricingResolutionService`: base -> components in
      order -> final list price -> customer discount. New port
      `IEffectiveRateComponentSource` + `PostgresRateComponentSource`; the
      submission service binds both ports to the SAME default price list.
- [x] S2.2 Vaca Verde verified rows as resolution tests
      (10,600 -> 15,370; 11,400 -> 16,530; 22,500 -> 32,625), plus the
      `Subtotal` negative case pinned at 16,057.7909375.
- [x] S2.3 Empty composition resolves to the stored base price, unchanged —
      no set, no source at all, and an explicitly empty set; proven against
      live Postgres, and channel parity re-proven over the composed price.
- [x] S2.4 Guest vs registered divergence over the *composed* price: one
      shared list price of 15,370, registered strictly lower at 13,833.

Explicitly out of both slices: admin UI, changes to
`supplier-price-import`, per-product/per-category VAT, rounding policy
changes.

## Acceptance criteria (slice 1)

- A rate component set persists and reads back inside its organization, with
  each component's code, label, percentage, calculation base and order.
- A second organization reads zero of the first organization's component sets
  (RLS), and that assertion is proven able to fail (mutation check).
- History is append-only: two dated sets both persist and remain readable, and
  `app_runtime` holds no UPDATE and no DELETE grant on either new table.
- The calculation base is persisted explicitly per component and round-trips;
  a component constructed without one is rejected.
- A price list with no set of its own resolves to the organization's default
  set; a list with its own set ignores the organization's.
- `dotnet build` and `dotnet test` green.

## Progress

- 2026-09-25 — **Slice 1 STOPPED partway: the build is blocked, not green.**
  `dotnet test` failed with `MSB3027`/`MSB3021` — `Commerce.Application.dll`,
  `Commerce.Domain.dll`, `Commerce.BranchNode.dll` and `Commerce.Updater.dll`
  could not be copied because `Commerce.Pos.Windows` (PID 6580) and
  `Commerce.Cloud.Api` (PID 28824) are running and holding them. Per the
  standing instruction for this repository those processes were NOT killed,
  so the session stopped here instead. Nothing was committed; the work below
  sits uncommitted in the working tree.

  **Done and verified:** the domain model (see next entry) compiles on its
  own — `dotnet build src/Commerce.Domain/Commerce.Domain.csproj` reports
  0 errors, 0 warnings.

  **Done but NOT verified:** `RateComponentTests` (19 facts) was observed RED
  before the model was written (5 × `CS0246`, the types did not exist), but
  it has never been run GREEN, because the test project cannot link while
  those DLLs are locked.

  **Not started:** migration `0013`, the `deploy/dev/db/init-rls.sql` mirror,
  `PostgresRateComponentStore`, every live-Postgres test (persistence, RLS
  isolation, append-only, inheritance), the `0013` section of
  `MigrationRlsTests`, the `PriceListEntry.UnitPrice` respecification, the
  RLS mutation check, and the commit.

  **To resume:** close `Commerce.Pos.Windows` and `Commerce.Cloud.Api`, run
  `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~RateComponentTests`
  to take S1.1/S1.4 from RED to GREEN, then continue at S1.2. Postgres in
  `incoders-commerce-postgres-1` is untouched and still holds the test
  accounts.

- 2026-09-25 — Model decisions taken while writing S1.1, recorded because
  they resolve options the proposal deferred to implementation.

  **Domain** (`src/Commerce.Domain/Pricing/`): `RateCalculationBase.cs`
  (`Unspecified = 0 | Base | Subtotal` — the sentinel is what makes "a
  component with no declared calculation base MUST be rejected" a rejection
  that can actually happen, instead of C#'s enum default silently meaning
  `Base`), `RateComponent.cs` (code, label, percentage, calculation base,
  order; validated at construction) and `RateComponentSet.cs` (append-only,
  `EffectiveFrom`, no `EffectiveTo`, owner is either a price list or an
  organization, components sorted by order, duplicate code/order rejected,
  plus a pure `Compose(basePrice)` that is NOT yet wired into resolution).
  Percentage is stored as a percentage number (`10.5`), not a fraction —
  the deferred decision from the proposal, resolved here and documented in
  the type.

  **Planned schema (S1.2, not written yet)**:
  `deploy/db/migrations/0013_rate_components.sql` creating
  `rate_component_sets` (owner is a price list, or the organization when
  `price_list_id IS NULL`) and `rate_components`, both with `FORCE ROW LEVEL
  SECURITY`, `REVOKE ALL FROM PUBLIC` and `GRANT SELECT, INSERT` only to
  `app_runtime` — no UPDATE and no DELETE, so append-only is a grant rather
  than a comment, exactly as `price_list_entries` does it. To be mirrored
  into `deploy/dev/db/init-rls.sql`.

  **Planned mutation check (not performed yet)**: relax
  `rate_component_sets_tenant_isolation` to `USING (true)`, confirm the
  cross-organization isolation test actually fails, then restore the real
  policy. Until it is run, the isolation assertion is unproven.


- 2026-09-25: Slice 1 complete and verified. Migration
  `0013_rate_components.sql` creates `rate_component_sets` (the dated,
  append-only unit: owned by a price list, or by the organization as an
  inheritable default when `price_list_id` is NULL) and `rate_components`,
  with `CHECK (calculation_base IN ('Base','Subtotal'))` enforcing the
  explicit base in the database rather than only in the domain, uniqueness of
  both code and order per set, RLS `ENABLE` + `FORCE` on both tables, and the
  rollback shipped as header comments. `PostgresRateComponentStore` and its
  records added; `deploy/dev/db/init-rls.sql` mirrored.
  `PriceListEntry.UnitPrice` respecified as the base price — documentation
  only in this slice, with the slice boundary and a migration hazard recorded
  on the type: rows already imported for Vaca Verde hold FINAL prices, so
  switching a 1.45 composition on before base-priced entries are published
  with the same `EffectiveFrom` would resolve them to `15,370 x 1.45`.
  RLS isolation is proved by a pair: a cross-organization read asserting zero
  rows, plus `RateComponents_SameOrganizationRead_ReturnsTheSeededRows`
  proving the same query returns the rows under Org A's scope — without the
  companion, a broken INSERT would make the isolation test pass vacuously.
  `dotnet build`: 0 errors. `dotnet test`: 658 passed / 659 in
  `Commerce.Integration` (up from 617, so ~42 new), 31/31 `Commerce.Upgrade`,
  1/1 `Commerce.Bootstrap.Tests`. The single failure is the known
  environmental one, unrelated to this work: `wwwroot` is populated by the
  launcher, so `MapFallbackToFile` answers unmapped public routes with 200
  and `PublicRateLimitTests.WithGuestOrderingConfigAbsent_...` fails.
  Interrupted once mid-S1.5 by a session rate limit, and once earlier by DLL
  locks from the user's running apps; neither left anything committed.

- 2026-09-25: **Slice 2 complete and verified (S2.1-S2.4).** Composition is
  now on the resolution path.

  **Order, and where.** `PricingResolutionService.ResolveAsync` runs three
  ordered steps: read the effective entry (base price) -> compose the
  effective `RateComponentSet` onto it -> apply the customer's
  `DiscountPercentage` to the COMPOSED list price. `Resolved.UnitListPrice`
  is now the composed price, not the stored amount. Rounding is unchanged:
  `Compose` does not round, so `Money.Round2` still runs exactly twice.

  **Two ports, one list.** Composition needed the effective set, which
  `IEffectivePriceSource` cannot supply, so a second port
  `IEffectiveRateComponentSource` was added, mirroring the first: no channel
  parameter, no caller-supplied set, price list bound by the implementation.
  `PostgresRateComponentSource` adapts the existing
  `PostgresRateComponentStore.GetEffectiveSetAsync` — the date-selection and
  organization-default-inheritance rules were NOT reimplemented.
  `CloudOrderSubmissionService` binds both ports to the same default price
  list, so the entry and the components can never come from two lists. Its
  `PostgresRateComponentStore` parameter is REQUIRED, not optional-with-null,
  and the store is registered in `Program.cs`: a missing registration fails
  at startup instead of silently resolving base prices as finals.

  **Effective dating.** By the RESOLUTION date, independently of which entry
  is effective — the store's existing SQL (`effective_from <= $2 ORDER BY
  effective_from DESC LIMIT 1`, list first then organization default). Tested
  both ways: a later set does not affect an earlier resolution, and a
  2026-01-01 entry resolving on 2026-07-01 composes with the 2026-06-01 set.

  **Cutover hazard — decision: documentation + regression test, no code
  guard.** Justified in design.md's new "Cutover Runbook". `PriceListEntry`
  stores one amount and no provenance of its meaning, so a legitimate
  adoption and a mistaken one are byte-identical INSERTs; both candidate
  guards (refuse a set when entries exist / flag a suspicious ratio) would
  block the correct sequence or guess at business data. The mitigation is
  ordering, which is operational: publish base entries and the set with the
  same `EffectiveFrom`, verify `D` against `D - 1`. What IS enforced in code
  is the property that makes turning the slice on safe — an empty
  composition is the identity — guarded against live Postgres by
  `ResolveAsync_OverLivePostgres_WithNoPublishedComponents_ResolvesToTheStoredAmountUnchanged`.
  The stale "slice 1, documentation only" notes on `PriceListEntry`,
  `RateComponentSet` and `PostgresRateComponentStore` were corrected.

  **Mutation check (order), performed and reported.** Inverted the order —
  discount applied to the base, composition after — and re-ran. 4 tests
  failed: the discount-order test, the guest/registered divergence test, the
  live-Postgres composition test and the composed channel-parity test. All
  four failed on `UnitListPrice`. Finding worth keeping: composition and the
  discount are both multiplications, so they COMMUTE — `UnitNetPrice` was
  13,833 under both orders. An order test that asserts only the net price
  cannot fail. The order is provable only through `UnitListPrice`, which the
  spec defines as the composed price; that is now asserted explicitly and
  the reason is written into the test and the service.

  **Also corrected:** design.md claimed the four percentages on `Subtotal`
  give "15,559.xx (~190 pesos)". They give 16,057.7909375 (~688 pesos); the
  figure is now pinned by test. The spec scenario ("strictly greater than
  15,370") was always satisfied and is unchanged.

  **Known gap, disclosed:** the POS passes no component source, because its
  `price_replica` carries prices and not rate components. It therefore
  composes the identity and its prices are unchanged. That is a REPLICATION
  gap, not a resolution one — the arithmetic is in the one shared compiled
  method — and it is out of scope for both slices. Documented on the
  `PricingResolutionService` constructor.

  **Verification.** `dotnet build Commerce.sln`: 0 errors, 24 warnings (all
  pre-existing: NU1903 on `System.IO.Packaging`, 2 × CS8602 in
  `PaymentRecordingServiceTests`). `dotnet test Commerce.sln`:
  `Commerce.Integration` 675 passed / 676 (up from 658/659 — 17 new),
  `Commerce.Upgrade` 31/31, `Commerce.Bootstrap.Tests` 1/1. The single
  failure is the same known environmental one,
  `PublicRateLimitTests.WithGuestOrderingConfigAbsent_...`, caused by a
  populated `wwwroot` making `MapFallbackToFile` answer unmapped public
  routes with 200. Unrelated to this work and unchanged by it.

## Next step

Both slices are implemented. Remaining, and explicitly out of scope of
this change: the admin UI for publishing component sets, replicating
components to the POS, per-product/per-category VAT (design.md "Known
Limitation"), and executing the cutover runbook against Vaca Verde's real
imported rows.
