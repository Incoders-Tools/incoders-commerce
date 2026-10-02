# Local-cloud synchronization

The product synchronizes explicit operations and events; it does not blindly
replicate database files or tables. The functional source remains
[PRD section 11](../../PRD.md#11-operación-local-y-cloud). This document
describes the two implemented sync mechanics — the envelope/outbox/inbox
push pattern and the cursor/replica pull pattern — and the selection rule
between them. Cross-cutting rules (table shape, payload evolution, retry
policy) are recorded durably in
[ADR-012](./decisions/ADR-012-sync-ownership-and-payload-evolution.md); this
document explains the mechanics, ADR-012 states the rules.

## Confirmed constraints

- A local sale is confirmed locally and does not wait for cloud connectivity
  (ADR-002).
- Operations use global identifiers, support idempotent retry, and remain
  persisted until confirmation.
- Synchronization is attempted automatically as soon as conditions allow —
  at startup, on a fixed periodic sweep, and after a local sale commits —
  and also runs outside the POS critical path; a manual trigger remains
  additionally available.
- A failed or still-pending synchronization attempt produces a durable
  outcome that survives a restart, rather than an in-memory list.
- Confirmed movements are never silently overwritten; unresolved conflicts
  require review.
- Cloud views show freshness when a branch is disconnected.

## Pattern 1: envelope push (outbox → inbox)

Used for a discrete, idempotent, per-operation fact the sender owns —
today, `"sale"` (branch → cloud) and `"order"` (cloud → branch).

```text
Branch -> cloud (sale; ADR-002: the sale path never awaits any of this)
  CompleteOfflineSale / CompleteScannedSale
    -> real SalePayloadV1 -> JSON (never the decorative "{}")
    -> ONE SQLite tx: sale_effects (+ sale_lines) + sync_outbox(status='Pending')
    -> returns immediately; no cloud call on this path

Automatic sync (in-process POS only; no daemon — see ADR-012)
  startup | 60s DispatcherTimer sweep | after a sale commit | manual button
    -> RunSyncAsync(trigger) — ONE body, four callers, reentrancy-guarded
    -> GetPendingOutbox = sync_outbox UNION legacy outbox
                           (legacy rows: payload reconstituted at read time
                            from sale_id/total_amount + sale_effects.sale_kind)
    -> push -> ok  : Acknowledge (whichever table holds the id)
            -> fail: attempt_count += 1, last_attempt_at_utc, last_error;
                     row stays Pending — never dead-lettered
    -> POS UI updated ONLY for the manual button trigger — the other three
       triggers stay invisible to the local operator

Cloud -> branch (order)
  CloudOrderStore.AttemptDelivery
    -> real OrderPayloadV1 -> JSON (never the decorative "{}")
    -> BranchSyncStore.ApplyInbound(envelope)
         ONE tx: dedup on operation_id
                 -> handler = registry[payload_kind]
                      unknown -> ROLLBACK, UnknownKind, no inbox row
                 -> handler.Apply(envelope, tx)   <- e.g. inbound_orders
                 -> INSERT inbox(operation_id, applied_at_utc)
                 -> COMMIT  — dedup and materialization succeed or fail together
```

De-duplication (the `inbox` insert) and materialization (the handler's
domain-state write) happen in the same transaction by construction — neither
can succeed while the other fails (see ADR-012, "Inbound materialization").

## Pattern 2: cursor/replica pull

Used when the branch needs the sender's current full-row view of reference
data it does not own — today, `customers_replica`, `stock_replica` and
`catalog_replica`/`price_replica`, all keyed by `sync_cursors.channel`.

```text
Branch pull (part of the same RunSyncAsync sweep as the outbox push)
  read last-synced cursor for the channel
    -> pull changes since cursor from cloud
    -> ONE tx: upsert changed rows + delete removed rows + advance cursor
    -> a failure (unreachable, non-2xx, empty body) is non-fatal: the
       replica and cursor stay byte-identical, and the sale path is never
       blocked
```

A replica row is never treated as an audit fact, and an envelope kind is
never used to replicate mutable reference state (ADR-012).

### Sale to stock projection (branch -> cloud)

Stock is cloud-authoritative and derived (`SUM(stock_movements.quantity)`). When the inbox transaction projects a
`"sale"` envelope into `pos_sales`, it also writes one negative `Sale` stock movement per sale line
(`PosSaleStockProjection`), in the same transaction, in its own savepoint:

- source `PosSale`, `source_id` = sale id, `occurred_at_utc` = the sale time (`SalePayloadV1.OccurredAtUtc`), quantity =
  `-line.Quantity` (kilos for Weighted lines: 2,5 kg is `-2.5`);
- the payload lines carry a per-sale `LineNumber`, not a global id, so the idempotency key `source_line_id` is derived
  deterministically: first 16 bytes of `SHA-256("pos-sale-line:{saleId}:{lineNumber}")`. The unique index on
  `(organization_id, branch_id, source_type, source_line_id)` makes a redelivery a no-op, even under another
  `operation_id`;
- a line whose presentation the branch catalog does not know, or whose quantity is not positive, is skipped with a
  logged warning; the other lines and the sale itself are still ingested. Any failure of the stock projection is
  contained by the savepoint and never blocks ingestion (same rule as the sale number);
- there is no sale void/return payload kind today, so no compensating movement exists. When one is added, it
  writes `PosSaleVoid` movements (a `Reversal` of the sale movement) idempotently on the same key scheme.


### Stock replica channel (cloud -> branch)

Stock is cloud-authoritative and derived (see the sale projection above), so the branch needs a READ replica to know
availability offline. Channel `stock` follows the cursor/replica pattern exactly:

```text
GET /device/stock/sync?since=<cursor>        (device bearer; org AND branch from the stored credential)
  -> { items: [{ presentationId, onHand }], serverTimeUtc }
Branch (same RunSyncAsync sweep): ONE tx upsert items into stock_replica + advance sync_cursors('stock')
```

- Each item is an ABSOLUTE on-hand snapshot (`SUM(stock_movements.quantity)`, may be negative) of a presentation that
  had a movement since the cursor, never a delta; a replay is a harmless upsert. A presentation that never moved has no
  row: the POS treats it as unknown, not zero.
- The cursor column is `stock_movements.created_at_utc` (index `stock_movements_created_idx`, migration 0034). It is
  `now()` of the writing transaction, i.e. its start, so a slow commit could land after a later cursor; the server
  therefore reads from `since - 5 minutes` (`PostgresStockStore.ReplicaGrace`). The overlap costs nothing because items
  are absolute. No per-presentation version or stored balance is maintained: the work per sweep is one index range scan
  plus the SUM of only the presentations that changed.
- A failed pull (unreachable, non-2xx, empty body) leaves the replica and cursor byte-identical. The replica stays
  usable offline; the POS labels the stock with the cursor time ("al dd/MM HH:mm"), the time of the last successful
  refresh, which is also true for rows that did not change.
- POS policy v1: the sale card shows "Stock: 117,5 kg" plus the as-of time, and a line over the known stock shows a
  warning ("... la venta no se bloquea") in the card and in the scan message. It never blocks the sale. Not covered: no
  local decrement between syncs (two terminals of one branch each see the stock as of their last pull), and the unit
  label "kg" in the scan message is omitted for a line whose card is not on screen.
- Contract v1 is additive: a new endpoint and a new channel; no existing payload or channel changes.

## Envelope vs. cursor/replica: the selection rule

A new domain's synchronization approach is classified **before**
implementation:

- **Push envelope** — the fact is a discrete, idempotent, per-operation event
  owned by the sender, meaningfully keyed by `operation_id`.
- **Pull cursor/replica** — the receiver needs the sender's current full-row
  view of reference data it does not own.

| Channel | Direction | Classification |
|---|---|---|
| `"sale"` | Branch → cloud | Push envelope — a discrete sale event |
| `"order"` | Cloud → branch | Push envelope — a discrete order-acceptance event |
| Customers | Cloud → branch | Cursor/replica — branch needs the cloud's current customer view |
| Catalog + price | Cloud → branch | Cursor/replica — branch needs the cloud's current catalogue/price view |
| Stock | Cloud → branch | Cursor/replica — branch needs the cloud's current on-hand snapshot (stock is cloud-authoritative) |

See ADR-012 for the full rule text and its rationale.

## Preliminary authority

| Information | Primary authority |
|---|---|
| Sales and cash | Local branch |
| Branch hardware | Local branch |
| Stock (derived from movements) | Cloud (owner decision 2026-10-02, deviating from PRD 724); the branch holds a read replica |
| Preparation and delivery | Local branch operation |
| Online-order origin | Cloud |
| Recorded-payment authority | The node that recorded it — branch for POS cash, cloud for web orders (ADR-011; commerce-payments) |
| Settlement state (owed/settled per order or customer) | Cloud, derived — `SettlementCalculator.Fold` over the ledger, never stored |
| Campaigns and consolidated reporting | Cloud |
| Shared masters, users, and permissions | Pending by data type |

## Pending decisions

The authority and conflict policy for shared masters, users, permissions,
cross-branch transfers, and offline identity continuity must be documented
before implementation. Database, transport, provider, and runtime choices
remain open.

For notebook replacement safeguards, see
[deployment profiles](./deployment-profiles.md).
