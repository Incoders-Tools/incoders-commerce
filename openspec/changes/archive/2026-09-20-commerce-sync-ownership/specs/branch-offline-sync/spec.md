# Delta for Branch Offline Sync

## MODIFIED Requirements

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

(Previously: retry existed only as a manually-triggered action, with no
automatic-trigger requirement, and a failed attempt's durability and
visibility were unspecified beyond an in-memory list.)

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

## ADDED Requirements

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
