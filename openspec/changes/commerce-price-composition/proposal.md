# Proposal: Commerce Price Composition

## Intent

**Today the spreadsheet is the pricing engine, and the system only stores
its output.** Verified in code:

- `src/Commerce.Domain/Pricing/PriceListEntry.cs` carries exactly one
  monetary value, `UnitPrice`, plus `EffectiveFrom`, `Source`, and
  `ImportBatchId`. There is no tax, freight, or markup dimension anywhere
  on the entry.
- `src/Commerce.Domain/Pricing/PriceList.cs` carries `Id`,
  `OrganizationId`, `Name`, and `IsDefault`. It has no rate, percentage,
  or component collection.
- `src/Commerce.Application/Pricing/PricingResolutionService.cs` resolves
  a price by reading the effective entry's `UnitPrice` and applying the
  customer's `DiscountPercentage` (`src/Commerce.Domain/Customers/Customer.cs`);
  a `null` discount means a guest and yields the list price unchanged.
- Grepping the domain for VAT/IVA, gross-receipts/IB, freight, or markup
  concepts returns nothing. **No rate composition is modeled at all.**

Meanwhile the real customer, Vaca Verde (a meat distributor), keeps its
prices in a spreadsheet titled "Precios Vaca Verde Reparto con
Porcentajes". Its header declares four rates — VAT 10.5%, gross-receipts
tax (IB) 2.5%, freight 7%, markup 25% — and every row applies all four to
the base price, not compounded:

```text
final = base x (1 + 0.105 + 0.025 + 0.07 + 0.25) = base x 1.45
```

Verified against three rows of the real sheet: Asado completo
10,600 -> 15,370; Bola de lomo 11,400 -> 16,530; Entraña 22,500 -> 32,625.

**Why now.** Because the composition lives only in the spreadsheet, the
`UnitPrice` we store is already the final, fully loaded number. The
consequences are concrete: changing the 25% markup means recomputing every
row in the sheet and re-importing the entire catalog; a VAT change becomes
a full catalog reprice with no record of *why* the number moved; and no
part of the system can state what a price is composed of — only what it
totals. The base price, which is the one number the business actually
negotiates with its supplier, is unrecoverable from what we persist.

This change is **planning only**: proposal, design, and delta specs. No
implementation, no migration, no `tasks.md`.

## Scope

### In Scope

1. **Rate components on the price list.** An ordered, open collection of
   rate components owned by a `PriceList`, each carrying a code, a
   human-readable label, a percentage, a calculation base, and an order.
   No fixed `iva`/`ib`/`freight`/`markup` columns.
2. **Organization-level inheritable defaults.** An organization MAY
   declare default rate components that a price list uses when it declares
   none of its own.
3. **Explicit calculation base per component** — either the base price or
   the running subtotal — so a chained composition and a Vaca-Verde-style
   flat composition are both expressible without changing the model.
4. **Effective-dated, append-only component history**, matching the
   existing `PriceListEntry` discipline, so a historical order still
   resolves at the rates in force on its own date.
5. **Redefining `UnitPrice` as the base price**, with the final list price
   derived by composition, plus a migration strategy that treats every
   existing entry as a base with no components — observably identical
   output for already-loaded data.
6. **Resolution order** in `PricingResolutionService`: base -> components
   in order -> final list price -> customer discount.

### Out of Scope (non-goals)

- **Per-product or per-category VAT.** Acknowledged in design as a known
  limitation (see Decision 7), deliberately not built here.
- **Any admin UI** for creating or editing rate components. This change
  defines the model and resolution semantics; the screens that drive them
  are a separate proposal.
- **Changes to `supplier-price-import`.** Importing a sheet that already
  carries a final price keeps working exactly as it does today; teaching
  the importer to split a sheet into base plus components is not scoped.
- **Rounding policy changes.** Whatever rounding the current resolution
  applies is preserved; this change does not introduce a new money type,
  a new rounding mode, or per-component rounding.
- **Tax reporting, fiscal documents, or invoice line-item breakdowns.**
  The composition is a pricing mechanism, not an accounting or AFIP
  integration.
- **Component-driven discounts.** The customer `DiscountPercentage` stays
  exactly where it is, applied last, unchanged.

## Decisions

### Locked here

1. **Rate components live on the price list, not on the organization.**
   The sheet is titled "Reparto" (delivery) — the 7% freight exists
   *because* it is the delivery list. A counter/pickup list would not
   carry it, and a wholesale list would carry a different markup. Same
   organization, different rates. The organization contributes inheritable
   defaults, not the authoritative values.
2. **Open components, not fixed fields.** A collection of rows, each with
   code, label, percentage, calculation base, and order. A new zone
   surcharge or a new IIBB perception must be a row, not a migration.
3. **Calculation base is explicit per component**: on the base price, or
   on the accumulated subtotal. Vaca Verde uses the base for all four;
   other businesses chain. Left implicit, the model is closed.
4. **Append-only with effective dating**, identical in philosophy to
   `PriceListEntry`: a rate change is an INSERT, never an UPDATE.
5. **`UnitPrice` means the base price**; the final price is derived.
   Migration treats existing entries as bases with zero components, so
   observable behavior for existing data does not change.
6. **Resolution order is fixed**: base -> components in order -> final list
   price -> customer discount. `PricingResolutionService` remains the
   single resolution authority, as `pricing-resolution` already requires.
7. **The long-term home of VAT is the product or category**, because it is
   statutory per product class (meat 10.5%, general 21%). A business
   selling both needs two VAT rates within one list. For Vaca Verde, which
   sells only meat, the list suffices today. Because components are open,
   moving VAT to the product later is adding a scope dimension, not a
   redesign. Declared as a known limitation in design; not implemented.

### Deferred to design

- Whether component inheritance is all-or-nothing (a list either declares
  its own full set or inherits the organization's) or per-component merge.
- Whether the percentage is stored as a decimal fraction (`0.105`) or as a
  percentage number (`10.5`), and its precision/scale.
- Whether a component set is versioned as a whole set or per component
  row, and how `EffectiveFrom` keys that.
- Whether `Source`/`ImportBatchId`-style provenance applies to component
  rows as well.

## Capabilities

### Modified Capabilities

- `price-list-management`: gains rate components on the price list,
  organization-level inheritable defaults, effective-dated append-only
  component history, and the redefinition of a `PriceListEntry`'s amount
  as a base price.
- `pricing-resolution`: gains the composition step between the effective
  base price and the customer discount, and the requirement that an
  entry with no applicable components resolves exactly as it does today.

## Approach

Model the components first as a domain concept with their own append-only
effective-dated history, mirroring `PriceListEntry`'s shape so the two
histories read the same way. Then redefine the meaning of `UnitPrice` in
the spec, holding output compatibility by making "no applicable
components" resolve to the base price itself — which is precisely the
current behavior, so the migration is a semantic reinterpretation rather
than a data rewrite. Only then insert the composition step into
`PricingResolutionService`, ahead of the discount it already applies.

## Affected Areas

| Area | Impact | Description |
|------|--------|--------------|
| `src/Commerce.Domain/Pricing/PriceList.cs` | Modified | Owns an ordered rate-component collection |
| `src/Commerce.Domain/Pricing/PriceListEntry.cs` | Referenced | `UnitPrice` is respecified as the base price; shape unchanged |
| `src/Commerce.Domain/Pricing/*` | New | Rate component type, calculation base, effective-dated component history |
| `src/Commerce.Application/Pricing/PricingResolutionService.cs` | Modified | Composition step before the customer discount |
| `src/Commerce.Domain/Organizations/*` | Modified | Inheritable default rate components |
| `deploy/db/migrations/*` | New | Component tables, org defaults, RLS following the existing convention |
| `openspec/specs/price-list-management/spec.md` | Modified | Component, inheritance, effective-dating, append-only requirements |
| `openspec/specs/pricing-resolution/spec.md` | Modified | Composition requirement and ordering |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Existing entries silently reprice after the migration because they are treated as bases and some default component set applies to them | Medium | Locked: a list with no declared components and an organization with no declared defaults composes to the base itself; the migration declares neither |
| Compounding is introduced by accident (applying each component to the running total by default) and Vaca Verde's numbers drift | High | Calculation base is a required, explicit per-component value; the Vaca Verde row is a spec scenario with verified numbers |
| VAT modeled on the list becomes load-bearing and blocks a mixed-catalog business later | Medium | Named as a known limitation in design; open components make the later move additive |
| Component history and entry history diverge in shape, giving two effective-date semantics in one aggregate | Medium | Components reuse `PriceListEntry`'s exact append-only/`EffectiveFrom` discipline, no `EffectiveTo` |
| Rounding differences make the composed price disagree with the spreadsheet by cents | Medium | Vaca Verde's verified rows are spec scenarios; design fixes where rounding happens |

## Rollback Plan

Planning-only: reverting the commit removes the change folder and the two
delta specs. No code, schema, or data is touched by this change.

## Dependencies

- `price-list-management` (shipped) — the append-only effective-dated
  `PriceListEntry` discipline this change extends to components.
- `pricing-resolution` (shipped) — "Single Server-Side Resolution
  Authority", "Guest and Registered Divergence", and "Explicit Error When
  No Effective Price Exists", all of which must survive unchanged.
- `supplier-price-import` (shipped) — referenced, not modified.

## Success Criteria

- [ ] A price list can declare an ordered set of rate components with
      explicit codes, labels, percentages, and calculation bases.
- [ ] An organization can declare default components that a list inherits
      when it declares none.
- [ ] Vaca Verde's four components on a base of 10,600 resolve to 15,370.
- [ ] A rate change is expressed as a new effective-dated component
      record; an order dated before it still resolves at the prior rate.
- [ ] An existing entry with no components resolves to exactly its stored
      `UnitPrice`, unchanged.
- [ ] Composition happens before the customer discount, inside
      `PricingResolutionService` and nowhere else.
