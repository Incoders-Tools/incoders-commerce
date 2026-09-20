# Delta for Branch Offline Sync

## MODIFIED Requirements

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
(Previously: did not specify line-item structure or price sourcing for
local sales.)

#### Scenario: Sale while offline

- GIVEN Branch 1 has no network connection
- WHEN an authorized cashier completes a valid sale
- THEN the sale succeeds locally, its cash and stock effects are
  committed once, and synchronization work is durable

#### Scenario: Interrupted local commit

- GIVEN a sale is interrupted before atomic persistence completes
- WHEN the branch restarts
- THEN no partial sale or orphan synchronization work is presented as
  committed

#### Scenario: Offline line-item sale prices from the local cache

- GIVEN Branch 1 has no network connection and its local cache holds
  currently effective prices
- WHEN a cashier composes a sale from scanned lines
- THEN each line's unit price comes from the local cache and the sale
  commits with a computed total, with no price typed by hand

## ADDED Requirements

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
