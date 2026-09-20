# Pricing Resolution Specification

## Purpose

Define the single server-side `PricingResolutionService` (ADR-010): the
one place a price is computed for a `(customer context, presentation,
quantity)` tuple, independent of the calling channel.

## Requirements

### Requirement: Single Server-Side Resolution Authority

The system MUST resolve price for a `(customer context, presentation,
quantity)` tuple in exactly one server-side service. No client (web, POS,
admin console) MAY compute a price; each MUST request resolution from
this service and MUST NOT submit a price it computed itself.

#### Scenario: Client-supplied price is rejected

- GIVEN a submission includes a caller-supplied price for a line
- WHEN the server processes the submission
- THEN the submission is rejected and no order or sale line is accepted
  with that price

### Requirement: Guest and Registered Divergence

For a guest customer context, resolution MUST return the Presentation's
effective list price with no discount applied. For a registered customer
context, resolution MUST return the list price adjusted by that
customer's `DiscountPercentage`. When the customer has a non-zero
discount, the registered result MUST be strictly lower than the guest
result for the same tuple.

#### Scenario: Guest resolves to list price

- GIVEN a Presentation has an effective list price of 100
- WHEN resolution runs for a guest context
- THEN the resolved price is 100 with no discount applied

#### Scenario: Registered customer resolves to a discounted price

- GIVEN the same Presentation has an effective list price of 100 and a
  registered customer has `DiscountPercentage` of 10
- WHEN resolution runs for that customer's context
- THEN the resolved price is strictly lower than 100 and reflects the
  10% discount

### Requirement: Channel Independence

Channel MUST NOT be an input to resolution. The same tuple MUST resolve
to an identical price regardless of whether the caller is the web
storefront, the POS, or the admin console.

#### Scenario: Same tuple resolves identically across channels

- GIVEN the same `(customer context, presentation, quantity)` tuple
- WHEN resolution is invoked from web, POS, and admin console
- THEN all three return the same resolved price

### Requirement: Explicit Error When No Effective Price Exists

If no `PriceListEntry` is effective for a Presentation on the resolution
date, resolution MUST return an explicit, visible error. It MUST NOT
return zero, null, or any silently substituted value.

#### Scenario: No effective price produces an explicit error

- GIVEN a Presentation has no `PriceListEntry` effective on or before the
  resolution date
- WHEN resolution is attempted
- THEN an explicit error is returned and no price value is produced
