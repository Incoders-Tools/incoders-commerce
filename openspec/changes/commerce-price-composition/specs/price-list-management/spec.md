# Price List Management Specification

## Purpose

Extend the versioned, effective-dated `PriceList` aggregate with the rate
composition that currently lives only in the customer's spreadsheet: an
open, ordered set of rate components owned by the price list, inheritable
defaults declared by the organization, and an append-only effective-dated
history for both. A `PriceListEntry`'s amount becomes the base price from
which the final list price is derived, rather than the final price itself.

## Requirements

### Requirement: Price List Rate Components

A `PriceList` MUST be able to declare an ordered set of rate components.
Each component MUST carry a stable machine-readable code, a
human-readable label, a percentage, a calculation base, and an explicit
order. The system MUST NOT model rates as fixed named fields such as VAT,
gross-receipts tax, freight, or markup; adding a new charge MUST be
expressible as an additional component, not a schema change.

A price list MAY declare no components at all; that list's composition is
empty and its final price equals its base price.

#### Scenario: A delivery list declares four rate components

- GIVEN a price list named for delivery
- WHEN an admin declares components `IVA` 10.5%, `IB` 2.5%, `FLETE` 7%,
  and `REMARCACION` 25%, each with an explicit order
- THEN all four persist on that price list in the declared order with
  their codes, labels, percentages, and calculation bases

#### Scenario: A new charge is a component, not a new field

- GIVEN a price list already carrying components
- WHEN the business adds a zone surcharge
- THEN it is declared as an additional rate component on that list, and
  no new named rate field is introduced

#### Scenario: A list may carry no components

- GIVEN a price list that declares no rate components
- WHEN its composition is read
- THEN the component set is empty and no error is reported

### Requirement: Rate Components Are Scoped To The List, Not The Organization

Two price lists in the same organization MUST be able to declare
different rate components. The system MUST NOT force every list in an
organization to share one composition.

#### Scenario: Delivery and counter lists carry different freight

- GIVEN one organization owns a delivery price list and a counter price
  list
- WHEN the delivery list declares a `FLETE` component and the counter
  list does not
- THEN each list retains its own component set and neither is altered by
  the other

### Requirement: Organization Default Rate Components

An organization MUST be able to declare a default set of rate components.
A price list that declares no component set of its own MUST use the
organization's effective default set. A price list that declares its own
set MUST use it in full and MUST NOT merge it with the organization's
defaults. If neither the list nor the organization declares a set, the
composition MUST be empty rather than an error.

#### Scenario: A list without its own set inherits the organization defaults

- GIVEN an organization declares default components and one of its price
  lists declares none
- WHEN that list's effective composition is read
- THEN the organization's default components are used

#### Scenario: A list's own set fully overrides the organization defaults

- GIVEN an organization declares default components including `FLETE`,
  and one of its price lists declares its own set without `FLETE`
- WHEN that list's effective composition is read
- THEN only the list's own components are used and `FLETE` is not applied

#### Scenario: No set anywhere is an empty composition, not an error

- GIVEN neither a price list nor its organization declares any component
- WHEN that list's effective composition is read
- THEN the composition is empty and no error is reported

### Requirement: Explicit Calculation Base Per Component

Each rate component MUST declare whether its percentage applies to the
entry's base price or to the subtotal accumulated by the components
applied before it. The system MUST NOT infer, default, or hard-code this
value; a component without a declared calculation base MUST be rejected.

#### Scenario: Base-calculated components do not compound

- GIVEN four components of 10.5%, 2.5%, 7%, and 25%, each declaring the
  base price as its calculation base
- WHEN they are applied to a base price of 10,600
- THEN each component is computed from 10,600 and the composed result is
  15,370

#### Scenario: Subtotal-calculated components chain in order

- GIVEN two components declaring the accumulated subtotal as their
  calculation base, with explicit orders
- WHEN they are applied
- THEN the second component's percentage is computed from the subtotal
  produced by the first, not from the base price

#### Scenario: A component without a calculation base is rejected

- GIVEN a rate component declared with a code, label, percentage, and
  order but no calculation base
- WHEN it is submitted
- THEN the request is rejected and no component is persisted

### Requirement: Append-Only Effective-Dated Rate Component History

The system MUST persist a rate component set with an `EffectiveFrom` date
and MUST NOT allow a persisted set's components, percentages, calculation
bases, orders, or effective date to be mutated or deleted. Changing a rate
MUST be expressed by publishing a new set with a later `EffectiveFrom`,
never by overwriting the prior one. The effective set for a given date is
the set with the latest `EffectiveFrom` at or before that date; there is
no end date and no gap between sets.

#### Scenario: A rate change publishes a new set without deleting history

- GIVEN a price list has a component set effective from 2026-01-01
  carrying `IVA` at 10.5%
- WHEN an admin publishes a new set effective from 2026-06-01 carrying
  `IVA` at 21%
- THEN both sets persist and the 2026-01-01 set remains readable as
  history

#### Scenario: Direct mutation of a persisted component set is rejected

- GIVEN a persisted rate component set exists
- WHEN a request attempts to change a component's percentage, calculation
  base, order, or the set's effective date in place
- THEN the request is rejected and the original set is unchanged

#### Scenario: Resolution picks the latest applicable component set

- GIVEN component sets effective 2026-01-01 and 2026-06-01 exist for a
  price list
- WHEN the composition is read for 2026-07-01
- THEN the 2026-06-01 set's components are used

### Requirement: Price List Entry Amount Is A Base Price

A `PriceListEntry`'s amount MUST be the base price from which the final
list price is derived by composition. The system MUST NOT persist the
composed final price as a second stored value on the entry. An entry whose
effective composition is empty MUST yield a final list price exactly equal
to its stored amount.

Entries persisted before rate components existed MUST be treated as base
prices with no components, so that their resolved price is unchanged.

#### Scenario: An entry with no components resolves to its stored amount

- GIVEN a `PriceListEntry` with an amount of 15,370 on a price list whose
  effective composition is empty
- WHEN its final list price is derived
- THEN the result is exactly 15,370

#### Scenario: The composed final price is not stored on the entry

- GIVEN a price list with rate components and an entry with a base amount
- WHEN the entry is read
- THEN it carries only the base amount, and the final price is derived
  rather than read from a persisted field

#### Scenario: Pre-existing entries are unaffected by the model change

- GIVEN entries persisted before rate components were introduced, on
  lists and organizations that declare no components
- WHEN their prices are resolved
- THEN each resolves to exactly the amount it resolved to previously

### Requirement: Organization-Scoped Component Persistence With RLS

The system MUST persist rate component rows, for both price-list sets and
organization default sets, scoped to exactly one `OrganizationId`,
protected by Postgres row-level security following
`PostgresOrganizationStore`'s convention (`FORCE ROW LEVEL SECURITY`,
`REVOKE ALL FROM PUBLIC` plus explicit `GRANT`,
`NULLIF(current_setting(...))` pooler idiom).

#### Scenario: Second organization cannot read another org's components

- GIVEN a rate component set exists in Organization A
- WHEN a request scoped to Organization B reads rate component sets
- THEN Organization A's set is not returned
