# Pricing Resolution Specification

## Purpose

Extend the single server-side `PricingResolutionService` (ADR-010) with
the rate composition step: the service derives a final list price from the
effective base price and the effective rate components before applying the
customer's discount. Composition is part of resolution, not of import and
not of any client.

## Requirements

### Requirement: Resolution Composes Rate Components Before The Customer Discount

Resolution MUST derive the final list price by applying the effective rate
components to the effective entry's base price, in each component's
declared order, using each component's declared calculation base. It MUST
then apply the customer's `DiscountPercentage` to that composed final list
price. The system MUST NOT apply the discount to the base price before
composition, and MUST NOT compose after discounting.

#### Scenario: Composition precedes the discount

- GIVEN a price list whose components compose a base of 10,600 into a
  final list price of 15,370, and a registered customer with a
  `DiscountPercentage` of 10
- WHEN resolution runs for that customer's context
- THEN the discount is applied to 15,370, not to 10,600

#### Scenario: Components are applied in their declared order

- GIVEN a component set containing at least one component whose
  calculation base is the accumulated subtotal
- WHEN resolution composes the final list price
- THEN the components are applied in their declared order and the
  subtotal-based component uses the subtotal produced by the components
  ordered before it

### Requirement: Composition Is Part Of The Single Resolution Authority

Rate composition MUST happen inside `PricingResolutionService` and nowhere
else. No client (web, POS, admin console) MAY compose a price from a base
and a component set, and no import path MAY persist a pre-composed final
price in place of a base price. The existing prohibition on
caller-supplied prices extends unchanged to caller-supplied composed
prices and caller-supplied component sets.

#### Scenario: Client-composed price is rejected

- GIVEN a submission includes a caller-composed final price or a
  caller-supplied rate component set for a line
- WHEN the server processes the submission
- THEN the submission is rejected and no order or sale line is accepted
  with that price

#### Scenario: Same tuple composes identically across channels

- GIVEN the same `(customer context, presentation, quantity)` tuple on a
  price list carrying rate components
- WHEN resolution is invoked from web, POS, and admin console
- THEN all three return the same composed price

### Requirement: Vaca Verde Delivery List Composition

For a price list declaring `IVA` 10.5%, `IB` 2.5%, `FLETE` 7%, and
`REMARCACION` 25%, each with the base price as its calculation base,
resolution MUST compose a base price into that base multiplied by 1.45.

#### Scenario: Asado completo composes to its sheet value

- GIVEN the delivery list's four base-calculated components and a
  `PriceListEntry` with a base price of 10,600
- WHEN resolution runs for a guest context
- THEN the resolved price is 15,370

#### Scenario: Bola de lomo composes to its sheet value

- GIVEN the same component set and a base price of 11,400
- WHEN resolution runs for a guest context
- THEN the resolved price is 16,530

#### Scenario: Entraña composes to its sheet value

- GIVEN the same component set and a base price of 22,500
- WHEN resolution runs for a guest context
- THEN the resolved price is 32,625

#### Scenario: Components do not compound

- GIVEN the same four percentages declared with the accumulated subtotal
  as their calculation base instead of the base price
- WHEN resolution composes a base price of 10,600
- THEN the result is strictly greater than 15,370, demonstrating that the
  declared calculation base and not an implicit default determines the
  outcome

### Requirement: Empty Composition Resolves To The Base Price

When neither the price list nor its organization has an effective rate
component set for the resolution date, resolution MUST return the entry's
base price as the final list price. An absent component set MUST NOT be
treated as an error and MUST NOT be substituted with any default rate.
This is distinct from an absent price, which remains an explicit error.

#### Scenario: No component set yields the base price unchanged

- GIVEN a `PriceListEntry` with a base price of 15,370 on a price list
  whose organization declares no default components
- WHEN resolution runs for a guest context
- THEN the resolved price is 15,370

#### Scenario: Absent components and absent prices are treated differently

- GIVEN a Presentation has no `PriceListEntry` effective on or before the
  resolution date, on a list that also has no component set
- WHEN resolution is attempted
- THEN an explicit error is returned for the missing price, not an empty
  composition result

### Requirement: Composition Uses The Rates Effective On The Resolution Date

Resolution MUST use the rate component set with the latest
`EffectiveFrom` at or before the resolution date, selected independently
of which `PriceListEntry` is effective. A component set published later
MUST NOT affect a resolution dated before it.

#### Scenario: A later rate change does not alter an earlier resolution

- GIVEN a component set effective 2026-01-01 carrying `IVA` at 10.5% and
  a later set effective 2026-06-01 carrying `IVA` at 21%
- WHEN resolution runs for 2026-03-01
- THEN the 10.5% rate is used

#### Scenario: Entry and component effective dates are selected independently

- GIVEN a `PriceListEntry` effective from 2026-01-01 and a component set
  effective from 2026-06-01 on the same list
- WHEN resolution runs for 2026-07-01
- THEN the 2026-01-01 entry's base price is composed with the 2026-06-01
  component set

### Requirement: Guest And Registered Divergence Over The Composed Price

For a guest customer context, resolution MUST return the composed final
list price with no discount applied. For a registered customer context,
resolution MUST return the composed final list price adjusted by that
customer's `DiscountPercentage`. When the customer has a non-zero
discount, the registered result MUST be strictly lower than the guest
result for the same tuple.

#### Scenario: Guest resolves to the composed list price

- GIVEN a base price of 10,600 composing to 15,370
- WHEN resolution runs for a guest context
- THEN the resolved price is 15,370 with no discount applied

#### Scenario: Registered customer resolves below the composed list price

- GIVEN the same composed list price of 15,370 and a registered customer
  with a `DiscountPercentage` of 10
- WHEN resolution runs for that customer's context
- THEN the resolved price is strictly lower than 15,370 and reflects the
  10% discount
