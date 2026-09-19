# Delta for Private Customer Ordering

## MODIFIED Requirements

### Requirement: Reusable Catalogue Semantics

The catalogue MUST represent Product, Presentation, Category, contextual
units, and the presentation's unit, measured-weight, or variable-weight
behavior without turning vertical classifications into rigid product
types. An order MUST retain the commercial meaning shown at submission;
settlement and final variable-weight adjustment are outside this slice.
Each Presentation MAY carry an identification code (barcode/SKU), unique
per `catalog-item-identification`, usable for lookup during ordering and
import matching.
(Previously: did not mention an identification code on Presentation.)

#### Scenario: Presentation-aware order draft

- GIVEN a product has unit and variable-weight presentations
- WHEN an enabled customer builds an order
- THEN each line identifies its presentation and permitted quantity
  semantics, with no forced product duplication

#### Scenario: Historical meaning retained

- GIVEN a catalogue or price changes after submission
- WHEN the order is later viewed
- THEN its submitted product, presentation, quantity semantics, and price
  context remain understandable

#### Scenario: Identification code available for lookup

- GIVEN a Presentation carries an identification code
- WHEN the catalogue is queried by that code
- THEN the matching Presentation is returned for use in order composition

## ADDED Requirements

### Requirement: Snapshotted Resolved Price on Order Lines

`OrderLineSnapshot` MUST carry the resolved unit price, the applied
discount, and the line total for each line, computed server-side by
`pricing-resolution` at submission time and frozen per ADR-003. A
subsequent price list change MUST NOT alter an already-submitted order's
snapshotted values.

#### Scenario: Order freezes resolved price at submission

- GIVEN a customer submits an order for a Presentation with a resolved
  unit price of 90 after discount
- WHEN the order is accepted
- THEN its `OrderLineSnapshot` records unit price 90, the applied
  discount, and the line total

#### Scenario: Later price change does not alter a submitted order

- GIVEN an order was submitted with a snapshotted line price
- WHEN the Presentation's price list later changes
- THEN the previously submitted order's snapshotted price, discount, and
  line total remain unchanged

### Requirement: Submission Rejects a Caller-Supplied Price

`SubmitOrderRequest` MUST NOT accept a price, discount, or line total
from the caller. The server MUST compute these values itself via
`pricing-resolution`; a request that supplies any of them MUST be
rejected.

#### Scenario: Request with a caller-supplied price is rejected

- GIVEN a `SubmitOrderRequest` includes a unit price or line total field
  populated by the client
- WHEN the server validates the request
- THEN the request is rejected and no order is created from it
