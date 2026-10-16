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
rejected before any operation is retried or acknowledged.

(Previously: this requirement covered retry and idempotency only, with no
constraint on how a synchronization request authenticates before its
operations are processed.)

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
