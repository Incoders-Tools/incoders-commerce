# Branch Offline Sync Specification

## Purpose

Define branch-owned offline operation and cloud synchronization without copying database files or tables. Acceptance uses one organization with two branches; a second organization is a security fixture only. Synchronization effects remain durable, retryable, idempotent, and auditable.

## Requirements

### Requirement: Local Sale Continuity and Atomic Persistence

A local sale MUST be accepted without Internet availability. Its business effect and the durable work needed for later synchronization MUST be persisted atomically, or neither MUST be considered committed. Local branch sales and cash remain branch authority; synchronization MUST remain outside the sale critical path.

#### Scenario: Sale while offline

- GIVEN Branch 1 has no network connection
- WHEN an authorized cashier completes a valid sale
- THEN the sale succeeds locally, its cash and stock effects are committed once, and synchronization work is durable

#### Scenario: Interrupted local commit

- GIVEN a sale is interrupted before atomic persistence completes
- WHEN the branch restarts
- THEN no partial sale or orphan synchronization work is presented as committed

### Requirement: Retryable Delivery with Idempotent Effects

Synchronization MUST retry unacknowledged operations until acknowledged
or explicitly requiring attention. Delivery retries are transport/process
attempts and MUST be distinguishable from business effects; duplicate
operation delivery MUST NOT repeat acceptance, stock/cash effects, or
downstream side effects. Synchronization requests MUST authenticate with
a server-issued, verifiable device credential; requests from an
installation the server cannot verify or has not registered MUST be
rejected before any operation is retried or acknowledged.

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
