# Tasks: Commerce Sync Ownership (Phase F)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~900–1,100 (design forecast) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes (skill default) — **overridden by user 2026-09-20** |
| Suggested split | PR 1 → PR 2 → PR 3 → PR 4, dependency-ordered (Units 1–4 from design.md; kept as internal implementation/commit order within one PR) |
| Delivery strategy | ask-on-risk |
| Chain strategy | **single-pr** — user chose one PR over the 4-unit chain, consistent with the same decision made for Phase E |

Decision made 2026-09-20: single PR for all of Phase F. The Unit 1-4 ordering
is preserved as the internal task/commit sequence (still RED→GREEN, still
dependency-ordered, Unit 4/SyncScheduler still last since it's the only
operator-visible behavioural change) — only the PR boundary changed from 4
PRs to 1.
400-line budget risk: High (accepted knowingly by the user)

`delivery_strategy = ask-on-risk` does not auto-resolve chain topology. Before
`sdd-apply` starts Unit 1, the user must confirm: (a) that four chained PRs
are acceptable, and (b) stacked-to-main vs. feature-branch-chain. The four
units below are **taken from design.md's own Review Workload Forecast table,
not reinvented**. Units 1–3 are invisible to the operator (no behavioural
change reaches the terminal); Unit 4 is the only behavioural change and has
the narrowest rollback (stop constructing `SyncScheduler`).

`openspec/changes/commerce-payments/` and its `payment_effects`/
`payment_outbox` tables are out of scope for every unit below; Unit 2's
`BranchSyncStore.cs` edits are additive-only alongside them.

### Suggested Work Units

| Unit | Scope | Budget | Likely PR | Focused test command | Boundary proven | Rollback |
|---|---|---|---|---|---|---|
| 1 | **Payload contract**: `SalePayloadV1`, `OrderPayloadV1`, `SyncPayloadCodec`, `SyncEnvelope` ctor guard, `InboundApplyOutcome.UnknownKind` + tests | ~230 | PR 1 → main | `dotnet test --filter SyncPayloadTests\|SyncEnvelopeTests\|InboundApplyOutcomeTests` | `{}` is unconstructible; round-trip + unknown-member-ignored proven | Revert; producers still compile only after Unit 2 |
| 2 | **Generic outbox**: `sync_outbox` DDL, generic writer, unioned+legacy-draining `GetPendingOutbox`, attempt columns, `BranchNodeService` real payloads + tests | ~330 | PR 2 → PR 1's branch | `dotnet test --filter GenericOutboxTests\|LegacyOutboxDrainTests\|SaleRegressionTests` | Payload-only enqueue works; sale regression green; `outbox` DDL byte-identical | Revert; legacy `outbox` table untouched |
| 3 | **Inbound materialization**: `IInboundEffectHandler`, registry, `SaleInboundHandler`/`OrderInboundHandler`, `inbound_orders`, transactional `ApplyInbound`, `CloudOrderStore` real payload + tests | ~280 | PR 3 → PR 2's branch | `dotnet test --filter InboundMaterializationTests\|CloudOrderStoreTests` | Dedup and materialization cannot succeed apart; unknown kind rolls back with no `inbox` row | Revert; inbox returns to dedup-only |
| 4 | **Automatic retry + docs**: `SyncScheduler`, `RunSyncAsync` extraction, ADR-012, `synchronization.md` + tests | ~260 | PR 4 → PR 3's branch | `dotnet test --filter RunSyncAsyncTests\|SyncSchedulerTests` | Sale commit never blocked by a sweep; UI stays silent for non-Button triggers | Stop constructing `SyncScheduler`; manual button unchanged |

## Phase 1: Payload Contract (Unit 1, ~230 lines)

- [x] 1.1 RED: `SyncEnvelopeTests` — constructor rejects a payload equal to
      `"{}"` and rejects a non-JSON-object payload (Requirement: Generic
      Outbox Payload Contract, scenario "Envelope payload is not decorative";
      Design "`SyncEnvelope.Payload`" decision — constructor guard)
- [x] 1.2 GREEN: `src/Commerce.Domain/Sync/SyncEnvelope.cs` modify — ctor
      guard rejecting `payload == "{}"` or invalid JSON object
- [x] 1.3 RED: `SyncPayloadTests` — `SalePayloadV1`/`OrderPayloadV1` round-trip
      through the codec; an unknown JSON member deserializes without throwing
      (additive-evolution rule) (Requirement: Payload-Kind Versioning,
      scenario "In-flight rows survive a payload-kind evolution decision")
- [x] 1.4 GREEN: `src/Commerce.Domain/Sync/Payloads/SalePayloadV1.cs`
      (`SaleId`, `TotalAmount`, `SaleKind`, `OccurredAtUtc`, `Lines[]`),
      `OrderPayloadV1.cs` (`OrderId`, `DestinationBranchId`, `Origin`,
      `Lines[]`), `src/Commerce.Domain/Sync/SyncPayloadCodec.cs` — one
      `System.Text.Json` serialize/deserialize pair, ignore-unknown-members
      option
- [x] 1.5 RED: `InboundApplyOutcomeTests` — `UnknownKind` exists as a member
      distinct from `Applied`/`DuplicateIgnored`/`Denied` (Design
      "Materialization contract" decision — "`UnknownKind`"; Requirement:
      Inbound Materialization Contract)
- [x] 1.6 GREEN: `src/Commerce.Domain/Sync/InboundApplyOutcome.cs` modify —
      add `UnknownKind`
- [x] 1.7 Regression guard: `dotnet test --filter
      SyncPayloadTests|SyncEnvelopeTests|InboundApplyOutcomeTests` green; no
      producer call site (`BranchNodeService.cs`, `CloudOrderStore.cs`)
      touched yet — Unit 2/3 wire them

## Phase 2: Generic Outbox (Unit 2, ~330 lines)

- [x] 2.1 RED: `GenericOutboxTests` — a payload kind other than `"sale"`
      enqueues into `sync_outbox` with no `SaleEffect` and no sale-specific
      column populated (Requirement: Generic Outbox Payload Contract,
      scenario "Enqueue a non-sale payload kind")
- [x] 2.2 GREEN: `src/Commerce.BranchNode/BranchSyncStore.cs` modify —
      append `sync_outbox` `CREATE TABLE IF NOT EXISTS` DDL (envelope
      columns, `payload_kind`, `payload TEXT NOT NULL CHECK (json_valid
      (payload) AND payload <> '{}')`, `status`, `acknowledged_at_utc`,
      `attempt_count`, `last_attempt_at_utc`, `last_error`); generic
      `EnqueueOutbox(envelope)` writer with no `SaleEffect` parameter; **no
      edit to the `outbox`/`sale_*`/`inbox` DDL**
- [x] 2.3 RED: `SaleRegressionTests` modify — `CompleteOfflineSale`/
      `CompleteScannedSale` enqueue `"sale"` through the generic path with
      unchanged observable sync behavior and no dependency on cloud
      reachability for the sale itself (Requirement: Generic Outbox Payload
      Contract, scenario "Existing sale enqueue is unaffected")
- [x] 2.4 GREEN: `src/Commerce.BranchNode/BranchNodeService.cs` modify
      (`:58`, `:92`) — build real `SalePayloadV1` payloads in
      `CompleteOfflineSale`/`CompleteScannedSale` instead of `"{}"`; route
      the sale write through `sync_outbox`
- [x] 2.5 RED: `LegacyOutboxDrainTests` — a pre-seeded legacy `outbox` row is
      returned by `GetPendingOutbox` with a reconstituted `SalePayloadV1`
      (from `sale_id`/`total_amount` + `sale_effects.sale_kind`), acknowledges
      correctly, and `PRAGMA table_info(outbox)` is byte-identical afterward
      (Design "Outbox generalization" decision — "`GetPendingOutbox` returns
      the UNION..."; Testing Strategy row)
- [x] 2.6 GREEN: `BranchSyncStore.cs` modify — `GetPendingOutbox` unions
      `sync_outbox` and still-`Pending` legacy `outbox` rows, reconstituting
      the legacy payload at read time; `Acknowledge`/`GetStatus`/`RowExists`
      span both tables
- [x] 2.7 RED: `GenericOutboxTests` — a failed push leaves `attempt_count = 1`
      plus `last_error` durably on disk after reopening the SQLite file
      (Testing Strategy "A failed push leaves `attempt_count = 1`...durably
      on disk after reopening the file")
- [x] 2.8 GREEN: `BranchSyncStore.RecordAttemptFailure(operationId, error)` —
      updates `attempt_count`/`last_attempt_at_utc`/`last_error` on whichever
      table holds the id; row stays `Pending`, never terminal
- [x] 2.9 Regression guard: `dotnet test --filter
      GenericOutboxTests|LegacyOutboxDrainTests|SaleRegressionTests` green;
      `outbox`/`sale_effects`/`sale_lines`/`inbox` DDL byte-identical to
      pre-change

## Phase 3: Inbound Materialization (Unit 3, ~280 lines)

- [x] 3.1 GREEN (no RED — pure port interface, mirrors this repo's existing
      store-abstraction pattern): `src/Commerce.BranchNode/
      IInboundEffectHandler.cs` — `string PayloadKind { get; }`,
      `void Apply(SyncEnvelope envelope, SqliteTransaction transaction)`
- [x] 3.2 RED: `InboundMaterializationTests` — `ApplyInbound` with an unknown
      `payload_kind` writes **no** `inbox` row, rolls back the transaction,
      and returns `InboundApplyOutcome.UnknownKind` (Requirement: Inbound
      Materialization Contract, scenario "De-duplication and materialization
      cannot drift apart"; Design "Materialization contract" decision —
      "unknown → ROLLBACK, UnknownKind, no inbox row")
- [x] 3.3 GREEN: `src/Commerce.BranchNode/InboundEffectRegistry.cs`
      (`IReadOnlyDictionary<string, IInboundEffectHandler>`, two entries, no
      discovery/reflection); `BranchSyncStore.ApplyInbound` modify — dedup
      check → resolve handler → `Apply` → insert `inbox` row → one commit;
      unknown kind rolls back and returns `UnknownKind`
- [x] 3.4 RED: `InboundMaterializationTests` — a handler that throws leaves
      **no** `inbox` row (atomicity), via the existing
      `SimulateInterrupted*` idiom (Testing Strategy "a handler that throws
      leaves no inbox row (atomicity)")
- [x] 3.5 GREEN: `BranchSyncStore.SimulateInterruptedInboundApply` —
      mirrors the existing `SimulateInterrupted*` idiom
- [x] 3.6 RED: `InboundMaterializationTests` — an inbound `"order"` envelope
      materializes into `inbound_orders` through the shared handler seam, not
      bespoke `AttemptDelivery` code; replaying one `operation_id`
      materializes exactly one `inbound_orders` row (Requirement: Inbound
      Materialization Contract, scenario "Order envelope materializes through
      the shared contract")
- [x] 3.7 GREEN: `BranchSyncStore.cs` modify — append `inbound_orders`
      `CREATE TABLE IF NOT EXISTS` DDL; `src/Commerce.BranchNode/Handlers/
      OrderInboundHandler.cs` — writes `inbound_orders` inside the caller's
      transaction; `src/Commerce.BranchNode/Handlers/SaleInboundHandler.cs`
      — dedup-only, stated explicitly rather than implied
- [x] 3.8 RED: `CloudOrderStoreTests` — `AttemptDelivery` builds a real
      `OrderPayloadV1` instead of `"{}"`; an `UnknownKind` outcome is treated
      as not-confirmed (order stays `Pending`) (Design File Changes
      `CloudOrderStore.cs` — "Real `OrderPayloadV1`; treat `UnknownKind` as
      not-confirmed")
- [x] 3.9 GREEN: `src/Commerce.Cloud.Api/Ordering/CloudOrderStore.cs` modify
      (`:169`) — build real `OrderPayloadV1`; map `UnknownKind` to `Pending`,
      never `Applied`/`DuplicateIgnored`
- [x] 3.10 Regression guard: `dotnet test --filter
      InboundMaterializationTests|CloudOrderStoreTests` green; dedup and
      materialization share one transaction by construction (structural, not
      only test-proven)

## Phase 4: Automatic Retry + Docs (Unit 4, ~260 lines)

- [x] 4.1 RED: `RunSyncAsyncTests` — `RunSyncAsync(trigger)` is
      reentrancy-guarded: a second trigger fired during an in-flight sweep is
      a no-op (Requirement: Retryable Delivery with Idempotent Effects;
      Design "Retry: where and how" decision — reentrancy flag)
- [x] 4.2 GREEN: `src/Commerce.Pos.Windows/MainWindow.xaml.cs` modify —
      extract `SyncButton_Click`'s body verbatim into
      `RunSyncAsync(SyncTrigger trigger)` with a reentrancy guard;
      `SyncButton_Click` delegates to it
- [x] 4.3 RED: `RunSyncAsyncTests` — a throwing push during `RunSyncAsync`
      never surfaces in the UI for a non-Button trigger; UI text updates only
      when `trigger == SyncTrigger.Button` (Requirement: Retryable Delivery
      with Idempotent Effects, scenario "Failed synchronization is durably
      recorded, not operator-facing")
- [x] 4.4 GREEN: `MainWindow.xaml.cs` modify — UI update path gated on
      `SyncTrigger.Button`; failures always call
      `BranchSyncStore.RecordAttemptFailure` regardless of trigger
- [x] 4.5 RED: `SyncSchedulerTests` — a sale commits with the scheduler
      running and the cloud unreachable; the commit does not await, block on,
      or fail from the sweep (Requirement: Retryable Delivery with Idempotent
      Effects, scenario "Automatic retry on connectivity restoration";
      ADR-002 non-blocking sale path)
- [x] 4.6 GREEN: `src/Commerce.Pos.Windows/SyncScheduler.cs` create —
      `DispatcherTimer` (60 s) + startup run + reentrancy guard, swallows and
      records all exceptions; `MainWindow.xaml.cs` modify — construct
      `SyncScheduler` at startup; `CommitSaleButton_Click` fires a
      fire-and-forget post-commit nudge without awaiting
- [x] 4.7 GREEN (no RED — documentation): `docs/architecture/decisions/
      ADR-012-sync-ownership-and-payload-evolution.md` create — outbox
      shape, payload evolution, retry policy, envelope-vs-cursor selection
      rule (Proposal Scope item 6)
- [x] 4.8 GREEN (no RED — documentation): `docs/architecture/
      synchronization.md` modify — replace pre-implementation text with the
      envelope / outbox-inbox / cursor-replica mechanics and the selection
      rule; link ADR-012 (Requirement: Envelope vs. Cursor Pattern
      Selection, scenario "New domain classified before implementation";
      Proposal Scope item 7)
- [x] 4.9 Regression guard: `dotnet test --filter
      RunSyncAsyncTests|SyncSchedulerTests` green; full `SaleRegressionTests`
      re-run green (final ADR-002 non-blocking proof)

## Phase 5: Full-Suite Verification

- [x] 5.1 `dotnet test Commerce.sln` full pass across Units 1–4
- [x] 5.2 `dotnet build Commerce.sln` clean
- [x] 5.3 Confirm the Threat Matrix stays entirely N/A per design.md (no
      routing/process/shell/executable-classification/Git boundary is
      touched); no RED test required for this row
- [x] 5.4 Trace every checkbox in `proposal.md`'s Success Criteria to the
      specific test task above that proves it (traceability closure pass)
- [x] 5.5 Confirm `openspec/changes/commerce-payments/` is byte-identical
      (`git diff --stat` check) and `payment_effects`/`payment_outbox` are
      untouched
