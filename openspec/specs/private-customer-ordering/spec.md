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

The catalogue MUST represent Product, Presentation, Category, contextual units, and the presentation's unit, measured-weight, or variable-weight behavior without turning vertical classifications into rigid product types. An order MUST retain the commercial meaning shown at submission; settlement and final variable-weight adjustment are outside this slice.

#### Scenario: Presentation-aware order draft

- GIVEN a product has unit and variable-weight presentations
- WHEN an enabled customer builds an order
- THEN each line identifies its presentation and permitted quantity semantics, with no forced product duplication

#### Scenario: Historical meaning retained

- GIVEN a catalogue or price changes after submission
- WHEN the order is later viewed
- THEN its submitted product, presentation, quantity semantics, and price context remain understandable

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
