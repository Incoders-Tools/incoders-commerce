# Design: Commerce Price Composition

## Technical Approach

The system currently persists a fully loaded price and nothing about how
it was built. This change splits that single number into a **base price**
(already persisted, reinterpreted) and an **ordered set of rate
components** (new), and moves the arithmetic that lives in the
spreadsheet today into `PricingResolutionService`.

Three properties drive the whole shape:

**(1) Components belong to the price list.** The real sheet is titled
"Precios Vaca Verde **Reparto** con Porcentajes" — delivery. Its 7%
freight exists because it is the delivery list; a counter/pickup list
would not carry it, and a wholesale list would carry a different markup.
That is one organization with several compositions, so the composition
cannot be an organization property. The organization instead declares
**inheritable defaults**: a list that declares no components of its own
uses the organization's, and a list that declares its own overrides them.

**(2) Components are rows, not columns.** Nothing in the model names VAT,
gross-receipts tax, freight, or markup. A component is a record with a
code, a human-readable label, a percentage, a calculation base, and an
order. A zone surcharge, a new IIBB perception, or a promotional
adjustment is a new row in an existing table — never a schema migration
and never a new branch in the resolution code.

**(3) The calculation base is explicit, never inferred.** Every component
declares whether its percentage applies to the **base price** or to the
**running subtotal** accumulated by the components before it. Vaca Verde
uses `Base` for all four; a business whose freight is charged on the
already-taxed amount uses `Subtotal`. Leaving this implicit would hard-code
one business's arithmetic into the engine.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Where components live** | On the `PriceList`, as an ordered collection, with the organization supplying inheritable defaults. The delivery/counter/wholesale split within one organization is a real, present requirement (the sheet's own title), not a hypothetical. | **On the organization only** — cannot express two lists with different freight in the same business, which is exactly Vaca Verde's next list. **On the entry** — repeats four identical rows per product and makes a rate change a full-catalog rewrite, the very problem this change removes. |
| **Component shape** | Open rows: `Code` (stable machine identifier, e.g. `IVA`, `IB`, `FLETE`, `REMARCACION`), `Label` (human-readable, shown to an admin), `Percentage`, `CalculationBase`, `Order`. | **Fixed `iva`/`ib`/`freight`/`markup` columns** — every new charge becomes a migration, and a business without one of them carries a meaningless null column. |
| **Calculation base** | A required two-valued enumeration: `Base` (the entry's `UnitPrice`) or `Subtotal` (base plus everything applied before this component, per `Order`). No default; a component with no declared base is invalid. | **Always compound on the subtotal** — produces 10,600 -> 15,559.xx for Vaca Verde instead of the verified 15,370. **Always apply to the base** — closes the model to chained compositions that other distributors genuinely use. |
| **Ordering** | `Order` is an explicit integer on each component, not insertion order and not code-alphabetical. It is only *observable* when at least one component uses `Subtotal`, but it must be declared unconditionally so switching a component to `Subtotal` later does not silently reorder the composition. | **Implicit ordering** — makes the result depend on row identity or write order, which is unreviewable and untestable. |
| **Effective dating** | Components are append-only and carry `EffectiveFrom`, with **no `EffectiveTo`**, exactly mirroring `PriceListEntry`. A rate change is an INSERT. "No component set effective for this date" is zero matching records, never a gap between ranges. | **Mutable percentage on a component row** — a VAT change would retroactively alter every historical order's resolved price, which is the failure this discipline exists to prevent. |
| **Versioning granularity** | The component **set** is the effective-dated unit: a dated set fully replaces the previously effective set for that list. Changing one rate publishes a new complete set. | **Per-component effective dating** — resolution would have to assemble a set from independently dated rows, and an accidental omission would silently drop a tax from a price rather than fail visibly. |
| **`UnitPrice` semantics** | Respecified as the **base price**. The final list price is derived by composition at resolution time and is never persisted as a second column. | **Persisting both base and final** — two sources of truth for one value, guaranteed to drift the first time a component is added without a catalog rewrite. |
| **Inheritance rule** | All-or-nothing at the set level: a list with its own effective component set uses it; a list with none uses the organization's effective default set; if neither exists, no components apply and the final price equals the base. | **Per-component merge** — a list overriding freight would silently inherit an organization VAT it never declared, and the effective set for any date would not be readable from one record. |
| **Resolution placement** | Composition happens inside `PricingResolutionService`, between reading the effective entry and applying the customer's `DiscountPercentage`. It is not a separate service, not a client concern, and not an importer concern. | **Composing at import time** — reintroduces the exact problem: a rate change forces a re-import. **Composing in a client** — violates `pricing-resolution`'s "Single Server-Side Resolution Authority". |
| **Discount interaction** | The customer's `DiscountPercentage` applies to the **composed final list price**, last. A guest resolves to the composed list price with no discount, preserving `pricing-resolution`'s "Guest and Registered Divergence" verbatim. | **Discounting the base before composition** — changes what a discount means (it would be discounted again by the markup component) and would silently alter every existing customer's effective price. |
| **Absence handling** | An entry whose list resolves to an empty component set composes to the base itself. This is what makes the migration a reinterpretation instead of a data rewrite. Absence of a *price* keeps failing loudly per `pricing-resolution`'s "Explicit Error When No Effective Price Exists"; absence of *components* is a legitimate empty composition, not an error. | **Treating a missing component set as an error** — would break every currently-loaded price on day one. |

## Data Flow

```text
Resolve a price (single server-side authority, unchanged entry point)
  PricingResolutionService.Resolve(customerContext, presentation, quantity, date)

  1. entry = latest PriceListEntry for (default PriceList, presentation)
             with EffectiveFrom <= date
     -> none  => explicit error (UNCHANGED: "Explicit Error When No
                 Effective Price Exists"; never zero, never null)

  2. base = entry.UnitPrice                       <- NOW MEANS "base price"

  3. components = latest effective set for (priceList, date)
     -> none  => latest effective ORGANIZATION default set for date
     -> none  => empty set

  4. subtotal = base
     for component in components ordered by Order:
        operand = component.CalculationBase == Base ? base : subtotal
        subtotal += operand * component.Percentage

  5. finalListPrice = subtotal

  6. guest      => finalListPrice                 <- no discount applied
     registered => finalListPrice adjusted by customer.DiscountPercentage
```

Worked against the real sheet, Vaca Verde's delivery list, all four
components declared with `CalculationBase = Base`:

```text
components: IVA 10.5% | IB 2.5% | FLETE 7% | REMARCACION 25%   (all on Base)
multiplier: 1 + 0.105 + 0.025 + 0.07 + 0.25 = 1.45

Asado completo   base 10,600 -> 1,113.00 + 265.00 +   742.00 + 2,650.00 -> 15,370
Bola de lomo     base 11,400 -> 1,197.00 + 285.00 +   798.00 + 2,850.00 -> 16,530
Entraña          base 22,500 -> 2,362.50 + 562.50 + 1,575.00 + 5,625.00 -> 32,625
```

The same four percentages declared with `CalculationBase = Subtotal`
compound instead of summing:

```text
10,600 x 1.105 x 1.025 x 1.07 x 1.25 = 16,057.79
```

The two results differing by ~688 pesos on one cut is the reason the
calculation base is a required explicit value. (Slice 2 correction: this
paragraph previously read "15,559.xx" and "~190 pesos", which does not
follow from these four percentages. The exact figure is now pinned by
`PricingCompositionTests.ResolveAsync_SameFourPercentagesOnSubtotal_...`;
the spec scenario's requirement — strictly greater than 15,370 — is
unchanged and was never in question.)

## Migration Strategy for `UnitPrice`

The migration is a **semantic reinterpretation with zero data rewrite**.

- No existing `PriceListEntry` row is updated. Its `UnitPrice` keeps the
  exact value it holds today; only its documented meaning changes from
  "final price" to "base price".
- No price list and no organization is given a component set by the
  migration. Every existing list therefore resolves to an empty
  composition.
- Because an empty composition yields `finalListPrice == base`, every
  currently-loaded price resolves to the identical number before and
  after. **Observable behavior does not change for existing data.**
- The append-only discipline is preserved end to end: adopting components
  for an existing list is an INSERT of a new dated component set, and the
  prices already loaded as finals are corrected — if the business chooses
  to — by INSERTing new dated entries carrying true base prices, never by
  UPDATEing the historical ones.
- The practical consequence for Vaca Verde: their currently imported rows
  hold 15,370-style finals. Turning on the 1.45 composition without also
  publishing base-priced entries would resolve 15,370 x 1.45. The adoption
  sequence — publish base entries and the component set with the same
  `EffectiveFrom`, as one dated cutover — is an operational requirement of
  this model and must be stated in any future runbook.

## Deploy Order: 0013/0014 Go Before The API (review round 1)

Finding R4-deploy-order-hard-dependency. Slice 2 made order pricing read
`rate_component_sets` on **every priced line**. That is an unconditional
hard dependency, not a feature flag: an API deployed against a database
where `0013` has not run fails every line of every order with
`relation "rate_component_sets" does not exist`, which
`ResolveLinesAsync` turns into `no-effective-price`. The customer sees an
unexplained denial and nothing names the cause.

**Decision: both halves, because they fail differently.**

1. *Enforced in code.* `PostgresReadinessHealthCheck` now verifies
   `rate_component_sets` and `rate_components` — table, forced RLS and
   tenant-isolation policy — exactly as it already verifies
   `price_list_entries`. A mis-ordered deploy is a red `/health/ready`:
   the instance never takes traffic, and the unhealthy message names the
   migration. Guarded by
   `PostgresReadinessHealthCheckTests.HealthReady_IsUnhealthy_BeforeRateComponentsMigrationApplied`.
2. *Written down here.* The gate stops the outage; it does not tell an
   operator staring at a red readiness probe at 3am what to do. **Apply
   `deploy/db/migrations/0013_rate_components.sql` and
   `0014_rate_component_tenancy.sql` BEFORE rolling the API.** Both are
   idempotent and forward-only, and both are additive — no existing
   column, row, policy or grant changes — so they are safe to apply
   against a running older API, which reads neither table.

The code half alone would have been a probe with no instructions; the
document half alone would have been an instruction nobody is holding
during the deploy that skips it.

## Cutover Runbook (slice 2)

Slice 2 put composition on the resolution path. Turning it on changed no
price, because every list that has published no component set composes to
the identity — proven end to end against a real database by
`PricingCompositionTests.ResolveAsync_OverLivePostgres_WithNoPublishedComponents_...`.

The hazard is therefore **not** the release; it is the first
`PublishSetAsync` onto a list whose entries were imported as finals.

**Why there is no code guard.** A deliberate decision, not an omission.
`PriceListEntry` records one amount and no provenance of its meaning: an
entry holding a base price and an entry holding a final price are the same
row. A legitimate adoption (base entries published the same day) and a
mistaken one (components published over old finals) are byte-identical
INSERTs, so any check would have to guess. The two candidate guards were
rejected for that reason:

- *Refuse a non-empty set when the list already has entries* — blocks the
  correct adoption sequence too, since the base entries are published
  first. It would train operators to work around it.
- *Flag a "suspicious" ratio between old and new entries* — a heuristic
  over business data, with no true answer, that would fail on a genuine
  price change.

Adding either would grow the system without shrinking the risk. The
correct mitigation is ordering, which is operational, so it is written
down here and named on `PriceListEntry.UnitPrice`, and a real-database
regression test guards the identity that makes the ordering safe.

**The sequence, per price list:**

1. Read the current entries. Every amount is a FINAL price.
2. Decide the component set (for Vaca Verde: `IVA` 10.5, `IB` 2.5,
   `FLETE` 7, `REMARCACION` 25, all `Base` — multiplier 1.45).
3. Pick one `EffectiveFrom` date, `D`, for the whole cutover.
4. INSERT new `PriceListEntry` rows effective `D` carrying the BASE
   prices (`final / 1.45` for Vaca Verde, or the negotiated base if the
   business has it). Never UPDATE the historical rows — resolutions dated
   before `D` must keep returning the old numbers.
5. INSERT the component set effective `D`, in the same maintenance window.
6. Verify before announcing: resolve one known presentation for `D` and
   for `D - 1 day`. Both must return the same final price. If the `D`
   resolution is 1.45x the `D - 1` one, step 4 was skipped or dated wrong.

**If step 5 lands without step 4**, the correction is append-only like
everything else: INSERT base-priced entries dated `D`. The wrong prices
resolved between the mistake and the fix remain historically accurate,
which is the point of the append-only discipline.

## Known Limitation: VAT Belongs on the Product, Not the List

VAT is **statutory per product class**, not per price list: meat is 10.5%
in Argentina, general merchandise is 21%. Modeling it as a list-level
component is correct only while a list's entire catalog shares one rate.

- For Vaca Verde it holds today: the catalog is entirely meat, one VAT
  rate, and both COLGADOS and CORTES AL VACIO carry 10.5% in the real
  sheet.
- It breaks for any business selling meat and, say, packaged groceries
  from the same list: two products in one list would need two VAT rates,
  and a single list-level `IVA` component cannot express that.
- **This is not implemented now and not scoped by this change.**
- The reason it is safe to defer: because components are open rows with a
  code and an explicit calculation base, moving VAT later means giving a
  component an optional product/category scope — an added dimension on an
  existing model. It is not a redesign of composition, not a change to the
  resolution order, and not a change to how the other three components
  behave. Deferring costs a later additive change; modeling VAT on the
  product now would cost a product-taxonomy decision this business does
  not yet need.

## Interfaces / Contracts

Illustrative shapes only; exact types, precision, and persistence are
implementation decisions.

```csharp
// The calculation base is required; there is deliberately no default.
public enum RateCalculationBase
{
    Base,      // percentage applies to the entry's base UnitPrice
    Subtotal   // percentage applies to the accumulated running subtotal
}

// One open rate row. Adding a charge is adding one of these, never a column.
public sealed class RateComponent
{
    public string Code { get; }                       // "IVA", "FLETE", ...
    public string Label { get; }                      // "IVA (10,5%)"
    public decimal Percentage { get; }
    public RateCalculationBase CalculationBase { get; }
    public int Order { get; }
}

// Append-only, effective-dated, NO EffectiveTo - PriceListEntry's discipline.
public sealed class RateComponentSet
{
    public Guid Id { get; }
    public Guid? PriceListId { get; }        // set for a list-owned set
    public Guid? OrganizationId { get; }     // set for an org default set
    public DateOnly EffectiveFrom { get; }
    public IReadOnlyList<RateComponent> Components { get; }  // ordered by Order
}
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | Vaca Verde's four `Base` components compose 10,600 -> 15,370, 11,400 -> 16,530, 22,500 -> 32,625 | xUnit, the real sheet's verified rows |
| Unit | An empty component set composes to the base itself, unchanged | xUnit |
| Unit | `Subtotal` components chain in `Order` and produce a different, correct result than the same percentages on `Base` | xUnit |
| Unit | A component with no declared calculation base is rejected at construction | xUnit |
| Unit | Inheritance: a list with its own set ignores the org defaults; a list without one uses them; neither means empty | xUnit |
| Unit | Effective dating: a set published later does not affect a resolution dated before it | xUnit |
| Unit | Composition runs before the customer discount; a guest gets the composed list price undiscounted | xUnit |
| Integration | An existing entry with no components resolves to exactly its stored `UnitPrice` after migration | live Postgres |
| Integration | Component rows are org-scoped under RLS following `PostgresOrganizationStore`'s convention | live Postgres, two seeded orgs |
| Integration | An attempt to mutate a persisted component set in place is rejected | live Postgres |

## Open Questions

- [ ] None blocking. Four items are deliberately deferred to
      implementation and named in the proposal: percentage storage form
      and precision, provenance fields on component sets, whether an
      admin UI lands with or after the model, and the operational cutover
      runbook for converting Vaca Verde's already-imported final prices
      into base prices plus a component set.
