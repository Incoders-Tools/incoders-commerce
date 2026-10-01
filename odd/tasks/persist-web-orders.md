# Persist Web Orders

## Objective
Store web orders in Postgres so they survive API restarts, and number them
`P{branch}-W-{seq}` (e.g. `P01-W-37`) as decided in
`odd/tasks/human-document-numbers.md`.

## Problem
`CloudOrderStore` keeps orders in an in-memory `Dictionary<Guid, Order>`
registered as a singleton (`src/Commerce.Cloud.Api/Program.cs:157`,
`Ordering/CloudOrderStore.cs`). There is no `orders` table: every API restart
loses all registered and guest orders. Orders also reach branches as "order"
envelopes (`OrderPayloadV1`, SQLite `inbound_orders`).

## Why
Owner decision (2026-09-30): orders must never be lost; this is the next
feature after `human-document-numbers`, and it is mandatory.

## Scope (to confirm at start)
- `orders` + `order_lines` tables with RLS, a `PostgresOrderStore` replacing
  the in-memory store, idempotent submit on `OrderId`, pending list and status
  transitions persisted.
- Per-branch order counter allocated in the submit transaction;
  `UNIQUE (organization_id, branch_id, sequence)`; `OrderPayloadV1` optional
  trailing number; web confirmation shows the number with the composition
  tooltip.
- Docs/specs: `order-payment-lifecycle`, `guest-ordering`,
  `private-customer-ordering`, `public-order-surface`,
  `docs/document-numbering.md`.

## Tasks
To be planned after `human-document-numbers` closes (explore first).

## Progress
- 2026-09-30: placeholder created from the owner's decision.

## Next step
Start after `human-document-numbers` T5.
