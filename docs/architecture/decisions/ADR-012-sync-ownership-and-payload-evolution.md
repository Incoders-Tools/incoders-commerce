# ADR-012: Sync ownership — outbox shape, payload evolution, retry policy, envelope-vs-cursor selection

## Status

Accepted (implemented — commerce-sync-ownership, Phase F)

## Context

Synchronization mechanics existed with no written contract. The branch
`outbox` was sale-shaped (`sale_id`, `total_amount` `NOT NULL`) while the
cloud `sync_inbox` was already generic on `payload_kind` + `payload`;
`SyncEnvelope.Payload` was the literal constant `"{}"` at every producer;
retry was 100% manual, with failures held only in an in-memory list lost on
restart; and the inbox recorded de-duplication (`operation_id`,
`applied_at_utc`) without ever materializing domain state — `AttemptDelivery`
treated `DuplicateIgnored` as confirmation of an order nothing had applied.
Two unrelated patterns (push envelope; pull cursor/replica) coexisted with no
selection rule. This ADR records the durable rules a later domain must obey,
per the precedent ADR-011 set: a cross-cutting rule gets an ADR, not only an
archived SDD change folder.

## Decision

- **Outbox shape.** The branch outbox converges onto the cloud inbox's shape:
  a new, additive `sync_outbox` table carries only envelope columns
  (`operation_id`, branch/org/aggregate/version/actor/correlation/occurred),
  `payload_kind`, `payload`, `status`, `acknowledged_at_utc`, plus
  `attempt_count`/`last_attempt_at_utc`/`last_error`. Every new write —
  including `"sale"` — goes to `sync_outbox`. The legacy `outbox` table's DDL
  is never edited and never written again; `GetPendingOutbox` returns the
  UNION of `sync_outbox` and any still-`Pending` legacy row, reconstituting
  the legacy row's payload into a real `SalePayloadV1` **at read time** from
  its `sale_id`/`total_amount` + `sale_effects.sale_kind` join. No migration
  statement ships; the legacy table becomes inert once its pending count
  reaches zero and is dropped by a later, separately-proposed change.
- **`SyncEnvelope.Payload` is real, not decorative.** It is kept, not
  deleted, and its constructor rejects both the literal `"{}"` and any
  non-JSON-object value. A return to the decorative constant is a
  construction-time failure, not a convention. `payload` is stored as
  `TEXT` with a `CHECK (json_valid(payload) AND payload <> '{}')` — mirroring
  the cloud `jsonb` column's validity guarantee without mirroring its
  normalization, so the branch's stored bytes never diverge from what was
  pushed.
- **Payload-kind versioning: freeze `ContractVersion: 1`, evolve additively,
  breaking change = new kind.** No version column exists anywhere in this
  system (the cloud never persisted one; both readers already synthesized
  `1`), so there is nothing to negotiate. A payload field may be **added**
  (readers ignore unknown members); it may never be removed, renamed, or
  repurposed. A breaking `"sale"` change ships as `"sale.v2"` with its own
  handler — in-flight `"sale"` rows keep draining through the handler they
  were written for, with zero new mechanism.
- **Inbound materialization: one shared, transactional seam.** An
  `IInboundEffectHandler { PayloadKind; Apply(SyncEnvelope, SqliteTransaction) }`
  registry (two entries, no discovery, no reflection) is invoked **inside**
  `ApplyInbound`'s existing transaction: dedup check → resolve handler →
  `Apply` → insert the `inbox` row → one commit. De-duplication and
  materialization therefore succeed or fail together by construction. An
  unknown `payload_kind` rolls back the whole transaction and returns
  `InboundApplyOutcome.UnknownKind` — no `inbox` row is written, so the
  sender retries after the receiver upgrades instead of the envelope being
  silently swallowed as applied.
- **Retry: fixed-interval automatic sweep, no backoff, no dead letter,
  invisible to the operator.** `SyncButton_Click`'s body is extracted into
  `RunSyncAsync(SyncTrigger)`, shared by a startup run, a 60-second
  `DispatcherTimer` sweep, a fire-and-forget post-sale nudge, and the manual
  button — one implementation, four triggers, reentrancy-guarded so a sweep
  never overlaps another. A push failure durably records
  `attempt_count`/`last_attempt_at_utc`/`last_error` on `sync_outbox` and the
  row stays `Pending` forever; there is no terminal/dead-letter state,
  because a state nobody can see would be a silently dropped sale. UI text
  updates only for the manual button trigger — the other three stay entirely
  invisible to the local operator. A background/hosted sync service remains
  an explicit non-goal; the scheduler is an in-process WPF timer only.
- **Envelope vs. cursor/replica selection rule.** Use a push envelope when
  the fact is a discrete, idempotent, per-operation event owned by the
  sender, keyed meaningfully by `operation_id` (worked examples: `"sale"`
  branch→cloud, `"order"` cloud→branch). Use a cursor/replica pull when the
  receiver needs the sender's current full-row view of reference data it
  does not own (worked examples: `customers_replica`, `catalog_replica`/
  `price_replica`, both keyed by `sync_cursors.channel`). An envelope kind
  must never be used to replicate mutable reference state, and a replica row
  must never be treated as an audit fact.

## Consequences

- A future domain's sync mechanic must be classified against the envelope-
  vs-cursor rule above **before** implementation, not discovered
  after the fact.
- Consolidating Phase E's `payment_outbox`/`payment_effects` onto
  `sync_outbox` is a named, later, separately-proposed opportunity — this
  ADR does not require or schedule it, and Phase E's parallel path is
  unaffected by this change in both directions.
- Dropping the legacy `outbox` table once provably drained (pending count
  reaches zero) is a later, separately-proposed change; this ADR does not
  schedule it.
- A `payload_kind`'s shape may only ever grow (additive fields); any
  contributor proposing to remove, rename, or repurpose an existing field
  contradicts this ADR and must instead introduce a new `payload_kind`.
- Any future change that reintroduces a decorative constant payload, a
  dead-letter/terminal outbox state, or a hosted background sync process
  contradicts this ADR and requires an amendment.
