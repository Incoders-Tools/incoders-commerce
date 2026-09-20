# Private Customer Ordering Specification

## Purpose

Define a private, enabled-customer catalogue-to-order walking slice bound to an organization and destination branch. Acceptance uses the primary organization's two branches and a second organization only as a security fixture. It preserves reusable Product/Presentation distinctions and per-presentation sale and inventory behavior while deferring settlement and provider choices.

## Requirements

### Requirement: Customer Reference Integrity

`Order.CustomerId` MUST reference a real, persisted `Customer` row in the
same organization as the order. An order MUST NOT be accepted for a
`CustomerId` with no matching customer row in the caller's organization.
There is no pre-launch order data requiring migration; the constraint is
enforced strictly from the first row (`NOT NULL` foreign key, no
legacy/orphaned rows).

#### Scenario: Order accepted with a valid same-organization customer

- GIVEN `Order.CustomerId` references a persisted customer row in the same
  organization as the order
- WHEN the order is submitted
- THEN the order is accepted and the reference remains valid

#### Scenario: Order rejected for a non-existent or cross-organization customer

- GIVEN a submitted `Order.CustomerId` does not match any persisted customer
  row in the caller's organization
- WHEN the order is submitted
- THEN the order is rejected and no order row is created

### Requirement: Bound and Revocable Customer Access

The ordering channel MUST use an unpredictable access credential bound to
exactly one organization and customer. Access evaluation MUST resolve the
credential and the customer's enabled state from the persisted store; a
request-supplied enabled flag (e.g. `AccessEnabled`) MUST NOT be accepted or
read from the request body. An unknown, disabled, or cross-organization
credential MUST be denied even when the caller asserts it is enabled. Access
MUST be limited to the customer's enabled state and MUST be revocable;
cross-organization or unbound use MUST be denied. Remote revocation while a
destination is offline remains subject to the approved offline-identity ADR
and MUST NOT be represented as immediately observed.
(Previously: evaluation accepted a caller-supplied `AccessEnabled` boolean
from the request body with no persisted store consulted.)

#### Scenario: Enabled customer catalogue access

- GIVEN an enabled customer presents a valid credential for Organization A
- WHEN the customer requests the permitted catalogue
- THEN the catalogue is returned for Organization A only

#### Scenario: Revocation and cross-organization denial

- GIVEN the credential is revoked or presented against Organization B
- WHEN the customer requests catalogue or order access
- THEN the request is denied and the denial is auditable

#### Scenario: Self-asserted enabled flag is rejected for an unknown credential

- GIVEN a credential/customer pair does not exist in the persisted access
  store
- WHEN an order is submitted with a self-asserted `accessEnabled: true` for
  that pair
- THEN the request is denied without consulting or trusting the submitted
  flag

#### Scenario: Revoked credential denies the next order and is auditable

- GIVEN a customer's persisted access credential was previously enabled and
  is then revoked
- WHEN the next order submission uses that credential
- THEN the order is denied and the denial produces an auditable entry

#### Scenario: Cross-organization credential is denied

- GIVEN a credential is valid and enabled in Organization A's persisted
  access store
- WHEN it is presented against Organization B
- THEN the request is denied and no Organization B data is disclosed

### Requirement: Reusable Catalogue Semantics

The catalogue MUST represent Product, Presentation, Category, contextual
units, and the presentation's unit, measured-weight, or variable-weight
behavior without turning vertical classifications into rigid product
types. An order MUST retain the commercial meaning shown at submission;
settlement and final variable-weight adjustment are outside this slice.
Each Presentation MAY carry an identification code (barcode/SKU), unique
per `catalog-item-identification`, usable for lookup during ordering and
import matching.

#### Scenario: Presentation-aware order draft

- GIVEN a product has unit and variable-weight presentations
- WHEN an enabled customer builds an order
- THEN each line identifies its presentation and permitted quantity semantics, with no forced product duplication

#### Scenario: Historical meaning retained

- GIVEN a catalogue or price changes after submission
- WHEN the order is later viewed
- THEN its submitted product, presentation, quantity semantics, and price context remain understandable

#### Scenario: Identification code available for lookup

- GIVEN a Presentation carries an identification code
- WHEN the catalogue is queried by that code
- THEN the matching Presentation is returned for use in order composition

### Requirement: Registered Order Origin Stamping

Order submissions completed through the credential-based private customer
ordering channel MUST be stamped with `OrderOrigin.RegisteredCustomer` and
MUST carry the resolved `CustomerId`. This addition does not alter the
credential requirements of "Bound and Revocable Customer Access", which
remain unchanged and in force verbatim under Decision 1 (coexist):
`CustomerOrderingAccess` continues to operate alongside the
admin-provisioned login introduced by `user-credentials`, both binding to
the same `Customer`.

#### Scenario: Credential-based order carries RegisteredCustomer origin

- GIVEN an enabled customer submits an order using a valid
  `CustomerOrderingAccess` credential
- WHEN the order is constructed
- THEN its `OrderOrigin` is `RegisteredCustomer` and its `CustomerId` is the
  credential's resolved customer

#### Scenario: Admin-provisioned login also stamps RegisteredCustomer origin

- GIVEN a customer submits an order while signed in through the
  admin-provisioned, `CustomerId`-linked login rather than a
  `CustomerOrderingAccess` credential
- WHEN the order is constructed
- THEN its `OrderOrigin` is `RegisteredCustomer` and its `CustomerId` is the
  linked customer, consistent with the credential-based path

### Requirement: Idempotent Submission and Pending Delivery

Order submission MUST have a stable business identity so retries cannot create duplicate acceptance or downstream effects. Delivery attempts MUST be retryable independently of business acceptance and MUST be acknowledged when the destination accepts the existing order. The pending-offline-order behavior is a reversible proposal assumption, not an unqualified product decision; an ADR MUST approve or replace it before implementation.

#### Scenario: Duplicate submission

- GIVEN a submission acknowledgement was lost
- WHEN the same business order is submitted again
- THEN one order acceptance exists and the retry returns its existing outcome

#### Scenario: Destination branch offline

- GIVEN the destination branch is offline when an order is submitted
- WHEN the cloud accepts the order origin
- THEN the order remains visibly pending destination confirmation, shows freshness or unavailable availability, and makes no stock promise or final stock effect

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
