# Delta for Branch Offline Sync

## ADDED Requirements

### Requirement: Payment-Effect Synchronization Path Parallel to the Sale Outbox

A payment-affecting effect MUST synchronize idempotently under a stable
`operationId`, reusing the existing `operation_id` idempotency and
`inbox` de-duplication pattern. Payment effects MUST NOT extend or reuse
the sale-shaped `outbox` table's schema; they MUST travel through a
parallel payment-effect path that leaves `outbox` semantics untouched.
Duplicate delivery of the same payment-affecting `operationId` MUST NOT
apply a second payment effect.

#### Scenario: Payment effect synchronizes without touching the sale outbox schema

- GIVEN a branch records a payment-affecting effect locally
- WHEN that effect synchronizes to the cloud
- THEN it travels through the payment-effect path and no column or row is
  added to the sale-shaped `outbox` table as a result

#### Scenario: Duplicate payment-effect delivery applies once

- GIVEN a payment-affecting effect was delivered but its acknowledgement
  was lost
- WHEN the same effect is delivered again under the same `operationId`
- THEN the receiver acknowledges the existing result and applies no
  second payment effect

### Requirement: Offline Sale Path Stays Non-Blocking on Payment Confirmation

The offline branch sale path MUST remain non-blocking with respect to
payment-effect synchronization (ADR-002). A branch MUST remain
authoritative for its own sale regardless of whether a payment-affecting
effect has yet reached or been acknowledged by the cloud.

#### Scenario: Sale commits while its payment effect is still pending sync

- GIVEN a branch has no network connection when a sale with a recorded
  payment completes
- WHEN the sale is committed locally
- THEN the sale succeeds and the payment-affecting effect remains durable
  and pending, without delaying or blocking the sale's local commit
