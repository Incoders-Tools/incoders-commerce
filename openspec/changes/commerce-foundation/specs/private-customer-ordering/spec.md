# Private Customer Ordering Specification

## Purpose

Define a private, enabled-customer catalogue-to-order walking slice bound to an organization and destination branch. Acceptance uses the primary organization's two branches and a second organization only as a security fixture. It preserves reusable Product/Presentation distinctions and per-presentation sale and inventory behavior while deferring settlement and provider choices.

## Requirements

### Requirement: Bound and Revocable Customer Access

The ordering channel MUST use an unpredictable access credential bound to exactly one organization and customer. Access MUST be limited to the customer’s enabled state and MUST be revocable; cross-organization or unbound use MUST be denied. Remote revocation while a destination is offline remains subject to the approved offline-identity ADR and MUST NOT be represented as immediately observed.

#### Scenario: Enabled customer catalogue access

- GIVEN an enabled customer presents a valid credential for Organization A
- WHEN the customer requests the permitted catalogue
- THEN the catalogue is returned for Organization A only

#### Scenario: Revocation and cross-organization denial

- GIVEN the credential is revoked or presented against Organization B
- WHEN the customer requests catalogue or order access
- THEN the request is denied and the denial is auditable

### Requirement: Reusable Catalogue Semantics

The catalogue MUST represent Product, Presentation, Category, contextual units, and the presentation’s unit, measured-weight, or variable-weight behavior without turning vertical classifications into rigid product types. An order MUST retain the commercial meaning shown at submission; settlement and final variable-weight adjustment are outside this slice.

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

