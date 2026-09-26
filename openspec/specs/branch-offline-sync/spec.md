# Branch Offline Sync Specification

## Purpose

Define branch-owned offline operation and cloud synchronization without copying database files or tables. Acceptance uses one organization with two branches; a second organization is a security fixture only. Synchronization effects remain durable, retryable, idempotent, and auditable.

## Requirements

### Requirement: Local Sale Continuity and Atomic Persistence

A local sale MUST be accepted without Internet availability. Its business
effect and the durable work needed for later synchronization MUST be
persisted atomically, or neither MUST be considered committed. Local
branch sales and cash remain branch authority; synchronization MUST
remain outside the sale critical path. A local sale MUST be line-item
based: each line carries a product/presentation identity and a unit
price resolved against the branch's locally cached, replicated price
(per `pricing-resolution`), except a line explicitly recorded via the
kept manual-total fallback per `pos-scan-sale`.

#### Scenario: Sale while offline

- GIVEN Branch 1 has no network connection
- WHEN an authorized cashier completes a valid sale
- THEN the sale succeeds locally, its cash and stock effects are committed once, and synchronization work is durable

#### Scenario: Interrupted local commit

- GIVEN a sale is interrupted before atomic persistence completes
- WHEN the branch restarts
- THEN no partial sale or orphan synchronization work is presented as committed

#### Scenario: Offline line-item sale prices from the local cache

- GIVEN Branch 1 has no network connection and its local cache holds
  currently effective prices
- WHEN a cashier composes a sale from scanned lines
- THEN each line's unit price comes from the local cache and the sale
  commits with a computed total, with no price typed by hand

### Requirement: Retryable Delivery with Idempotent Effects

Synchronization MUST retry unacknowledged operations until acknowledged
or explicitly requiring attention. Delivery retries are transport/process
attempts and MUST be distinguishable from business effects; duplicate
operation delivery MUST NOT repeat acceptance, stock/cash effects, or
downstream side effects. Synchronization requests MUST authenticate with
a server-issued, verifiable device credential; requests from an
installation the server cannot verify or has not registered MUST be
rejected before any operation is retried or acknowledged. Synchronization
MUST also be attempted automatically as soon as conditions (for example,
connectivity) allow, without requiring a manual trigger; a manual trigger
MAY remain additionally available. The automatic-trigger and retry
mechanism MAY stay simple, with no demonstrated requirement for
exponential backoff or dead-letter routing. A failed or still-pending
synchronization attempt MUST produce a durable outcome that survives a
restart and remains visible for authorized review per the existing
freshness/audit requirement; it MUST NOT be represented only by an
in-memory list. This capability's retry mechanism MAY stay invisible to
the local POS operator in this phase; an operator-facing status surface
is a later, separately proposed capability.

#### Scenario: Duplicate delivery

- GIVEN an operation was delivered but its acknowledgement was lost
- WHEN the same globally identified operation is delivered again
- THEN the receiver acknowledges the existing result and applies no
  second business effect

#### Scenario: Interrupted synchronization

- GIVEN synchronization stops after remote processing and before local
  acknowledgement
- WHEN connectivity returns
- THEN the operation is retried, converges to one effect, and becomes
  acknowledged without manual duplicate entry

#### Scenario: Unregistered installation is rejected before sync

- GIVEN a POS installation presents a device credential the server
  cannot verify or has never issued
- WHEN the installation attempts to synchronize
- THEN the synchronization request is rejected and no operation is
  retried, acknowledged, or applied

#### Scenario: Revoked installation credential blocks sync only

- GIVEN a POS installation's device credential was previously valid and
  has since been revoked server-side
- WHEN the installation attempts to synchronize
- THEN the synchronization request is rejected while local offline sale
  acceptance for that branch remains unaffected

#### Scenario: Automatic retry on connectivity restoration

- GIVEN a branch has pending outbox rows and no connectivity
- WHEN connectivity becomes available
- THEN synchronization is attempted automatically, without requiring a
  cashier or other local operator to trigger it manually

#### Scenario: Failed synchronization is durably recorded, not operator-facing

- GIVEN a synchronization attempt fails
- WHEN the branch is inspected afterward, including after a restart
- THEN the failure is durably recorded and remains visible for
  authorized review per the existing freshness/audit requirement, and
  no distinct sync-status surface is presented to the local POS operator
  in this phase

### Requirement: Generic Outbox Payload Contract

The branch outbox MUST accept a payload-kind and payload-body insertion
path that does not require sale-specific fields (for example, no
`SaleEffect`, no mandatory `sale_id` or `total_amount` column). The
existing `"sale"` payload kind MUST continue to enqueue and synchronize
through this generic path with unchanged observable behavior. The
envelope's payload field MUST carry the real, enqueued payload data for
every kind; it MUST NOT be represented by a constant placeholder value.

#### Scenario: Enqueue a non-sale payload kind

- GIVEN a branch has a payload kind other than "sale" ready to
  synchronize
- WHEN the branch enqueues it into the outbox
- THEN the outbox accepts the row using only a payload kind and payload
  body, with no sale-specific column populated

#### Scenario: Existing sale enqueue is unaffected

- GIVEN a branch completes an offline sale
- WHEN the sale effect is enqueued into the generalized outbox
- THEN the "sale" payload kind still enqueues and later delivers through
  the outbox's generic path, with unchanged observable sale-sync
  behavior and no dependency introduced on cloud reachability for the
  sale itself

#### Scenario: Envelope payload is not decorative

- GIVEN any payload kind is enqueued for synchronization
- WHEN its envelope is transmitted
- THEN the envelope's payload field carries the enqueued data, not a
  constant placeholder

### Requirement: Inbound Materialization Contract

An applied inbound envelope MUST materialize domain state through one
reusable seam shared across payload kinds, rather than through
bespoke per-caller application code. De-duplication (recording that an
`operation_id` was applied) and materialization (producing the
resulting domain state) MUST be performed as a single unit of work;
neither MUST be able to succeed while the other fails.

#### Scenario: Order envelope materializes through the shared contract

- GIVEN an inbound "order" envelope is applied at a branch
- WHEN it is processed
- THEN the resulting domain state is produced through the same
  materialization seam used by other payload kinds, not through
  kind-specific bespoke application code

#### Scenario: De-duplication and materialization cannot drift apart

- GIVEN an inbound envelope's `operation_id` is recorded as applied
- WHEN the branch is inspected afterward
- THEN materialized domain state exists for that operation, and it is
  not possible for the operation to be marked applied without its
  domain state having been materialized, or the reverse

### Requirement: Envelope vs. Cursor Pattern Selection

Synchronization for a new domain MUST be classified, before
implementation, as either push-envelope or pull-cursor/replica using a
written selection rule: push-envelope applies where an effect is a
discrete, idempotent, per-operation fact keyed by `operation_id` (for
example, "sale", "order"); pull-cursor/replica applies where the branch
needs a current full-row view of cloud-owned reference data (for
example, customers, catalog and price). The existing channels MUST be
classified against this rule as worked examples.

#### Scenario: New domain classified before implementation

- GIVEN a new domain requires branch-cloud synchronization
- WHEN its synchronization approach is selected
- THEN it is classified as push-envelope or pull-cursor/replica using
  the written selection rule, and the classification of the existing
  sale, order, customer, and catalog/price channels is available as a
  worked reference

### Requirement: Payload-Kind Versioning

A payload kind's contract MUST be able to evolve without breaking
in-flight rows — rows already enqueued or delivered but not yet
applied. Either the payload carries an explicit, checkable version that
lets the contract evolve, or the shape is frozen with that freeze
decision and its reasoning recorded. A payload kind's shape MUST NOT
change silently underneath rows already in flight.

#### Scenario: In-flight rows survive a payload-kind evolution decision

- GIVEN an outbox or inbox row is enqueued under one version of a
  payload kind's contract
- WHEN that payload kind's contract is later evolved, or explicitly
  frozen instead
- THEN the already-enqueued row remains processable under the contract
  it was written against, with no silent shape break

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

### Requirement: Freshness, Audit, and Conflict Visibility

The system MUST expose synchronization state and last successful cloud freshness for each branch without blocking local work. Confirmed movements MUST NOT be silently overwritten; unresolved conflicts MUST be visible for authorized review with auditable decisions.

#### Scenario: Stale branch view

- GIVEN Branch 2 has pending synchronization
- WHEN an authorized cloud user views branch data
- THEN the view shows the last successful synchronization time and pending or offline state

#### Scenario: Conflict review

- GIVEN two operations cannot be reconciled by the approved authority policy
- WHEN synchronization evaluates them
- THEN both confirmed histories remain traceable, the conflict requires authorized review, an audit entry records the decision, and no silent overwrite occurs

### Requirement: Replication of Effective Prices and Identification Codes

The existing cloud-to-local replication channel MUST extend to carry each
Presentation's currently effective price and identification code to each
branch. Staleness of this replicated data MUST be governed by the
existing ADR-002 freshness policy; this capability MUST NOT introduce a
second freshness mechanism.

#### Scenario: Price and code replicate to a branch

- GIVEN a Presentation's price and identification code change in the
  cloud
- WHEN the branch's next sync cycle completes
- THEN the branch's local cache reflects the updated price and code

#### Scenario: Stale replicated price follows ADR-002, not a new mechanism

- GIVEN a branch has pending synchronization and its cached price data is
  stale
- WHEN the branch is queried for freshness
- THEN the same freshness state ADR-002 already exposes for other
  replicated data is reused, with no separate price-freshness indicator

### Requirement: Replication And Inbox Scoped To The Paired Branch

Cloud-to-branch replication (catalog, prices, identification codes,
customers) MUST deliver only rows owned by the device's paired branch, and
cloud inbox reads MUST return only envelopes whose `branch_id` is that
branch. The branch MUST come from the server-issued device credential,
never from the request.

#### Scenario: Ruta 51 POS receives only Ruta 51 catalog

- GIVEN a POS paired to Vaca Verde's "Ruta 51" and products owned by
  "Ruta 51" and "Centro"
- WHEN the POS runs its catalog sync
- THEN only Ruta 51's products and prices are delivered
