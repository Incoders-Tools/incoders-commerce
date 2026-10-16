# Design: Commerce Sync Ownership (Phase F)

## Technical Approach

The proposal's five locked decisions are not re-opened. This design resolves its
six deferrals and records the **verified codebase facts** that force each answer.

**Verified 1 — the two halves of one channel disagree.** `BranchSyncStore.cs:77-92`
declares `outbox` with `sale_id TEXT NOT NULL` and `total_amount TEXT NOT NULL`;
`InsertOutboxRow` (`:850-877`) always writes both from a `SaleEffect`, and
`ReadExistingOutboxSale` (`:824-848`) joins `outbox` to `sale_effects` on
`sale_id`. The cloud counterpart `PostgresCloudInboxStore.TryApplyInboundAsync`
(`:82-101`) inserts **only** envelope columns plus `payload_kind` + `payload::jsonb`.
Decision 2 makes the cloud shape the target.

**Verified 2 — SQLite cannot widen the column in place.** SQLite has no
`ALTER TABLE ... ALTER COLUMN`; dropping `NOT NULL` from `sale_id` requires the
12-step rebuild (create new table, copy, drop, rename). **"In-place widen" is
therefore not an available operation — it is a new table plus a destructive
rename**, run against a live `branch.db` that may hold unsynced sales. This is
the decisive evidence for the additive route below.

**Verified 3 — `Payload` is `"{}"` at all three producers.**
`BranchNodeService.CompleteOfflineSale` (`:58`), `CompleteScannedSale` (`:92`),
and `CloudOrderStore.AttemptDelivery` (`:169`). Both readers hardcode
`ContractVersion: 1` (`BranchSyncStore:889`, `PostgresCloudInboxStore:171`), and
the cloud `sync_inbox` has **no `contract_version` column at all** — there is no
version to negotiate anywhere in the system.

**Verified 4 — the inbox applies nothing.** `ApplyInbound` (`:397-430`) inserts
`(operation_id, applied_at_utc)` and commits; `AttemptDelivery` (`:171-175`)
treats `Applied` *or* `DuplicateIgnored` as confirmation, so a duplicate order
is "confirmed" having materialized nothing — dedup and materialization are
already drifted, not merely at risk of drifting.

**Verified 5 — Phase E is untouched.** `payment_effects`/`payment_outbox` are
additive tables carrying only generic envelope columns. This design consumes
that precedent and requires **no edit to `commerce-payments`' design, tables or
columns**; consolidating `payment_outbox` onto `sync_outbox` is named as a later,
separately-proposed change, exactly as Phase E anticipated.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Outbox generalization** (deferral 2) | **New generic table `sync_outbox` + read-time drain of legacy `outbox`.** `sync_outbox` carries only envelope columns (`operation_id` PK, branch/org/aggregate/version/actor/correlation/occurred, `payload_kind`, `payload`, `status`, `acknowledged_at_utc`) plus the three retry columns below — the cloud `sync_inbox` shape, and already the shape `payment_outbox` uses. **Every new write goes to `sync_outbox`, including `"sale"`.** `GetPendingOutbox` returns the UNION of `sync_outbox` and any still-`Pending` legacy `outbox` rows, **reconstituting each legacy row's payload from its `sale_id`/`total_amount` + `sale_effects.sale_kind` join into a real `SalePayloadV1`**. `Acknowledge` updates whichever table holds the id. The legacy table is never written again and is dropped by a later change once provably drained. **Verified 2 makes this strictly cheaper**: no rebuild, no rename, no data-migration statement, and a partially-drained file is always consistent. It also **dissolves the proposal's rollback caveat** — `outbox`'s DDL is byte-identical, so reverting the commit strands nothing, and a non-sale kind lives in a table the old code simply ignores. | **Widen + backfill `outbox`** — not expressible in SQLite without a destructive rebuild of a table that may hold unsynced sales; a crash mid-rebuild is exactly the sale-loss scenario ADR-002 forbids, and it makes rollback lossy the moment it runs. **Leave `outbox` for `"sale"` forever, new table for others** — the proposal's own "generic outbox" goal fails: `"sale"` would keep the sale-shaped path permanently, and the repo would carry three outbox idioms instead of converging on one. |
| **Payload encoding on SQLite** (deferral 1) | **`payload TEXT NOT NULL CHECK (json_valid(payload))`, UTF-8 JSON.** Every existing branch column is `TEXT`; `Microsoft.Data.Sqlite` maps `TEXT → string`, which is exactly `SyncEnvelope.Payload`'s type and what `PostgresCloudInboxStore` binds to `$10::jsonb`. The `json_valid` CHECK mirrors the **validity** guarantee of `jsonb` (parse-on-write) **without** mirroring its normalization — jsonb reorders keys and drops whitespace, so mirroring it would make the branch's stored bytes differ from what was pushed, destroying any future signature/audit over the payload. | **`BLOB`** — forces manual encode/decode at every call site, is invisible to SQLite's JSON1 functions, and buys nothing: payloads are small text documents, not binary. **No CHECK** — a malformed payload would then fail for the first time at the cloud `::jsonb` cast, i.e. after leaving the branch, turning a local bug into a sync failure. |
| **`SyncEnvelope.Payload`** (locked Decision 5) | **Kept and made real, with a constructor guard.** Per-kind records live in `Commerce.Domain/Sync/Payloads/` (`SalePayloadV1`, `OrderPayloadV1`), serialized with `System.Text.Json`. The `SyncEnvelope` constructor **rejects a payload that is not a valid JSON object or that equals `{}`** — a return to the decorative constant becomes a construction-time failure, not a convention. The legacy drain never produces `{}` because it reconstitutes a real payload from the sale columns (see above). | **Delete the field** — the cloud `sync_inbox.payload` column and the generic outbox both need a payload body; removing it would just re-hide the data in out-of-band parameters, which is the defect being fixed. **A test that greps call sites** — misses reflection/dynamic construction and rots; the guard is enforced by the type. |
| **Versioning** (deferral 5) | **Freeze `ContractVersion: 1`; evolve by additive fields, and by a NEW `payload_kind` for a breaking change.** No column is added anywhere. Verified 3 shows there is no version to negotiate: the cloud never persisted one and both readers synthesize `1`. `payload_kind` is *already* the dispatch key, so a breaking `"sale"` change ships as `"sale.v2"` with its own handler, and in-flight `"sale"` rows keep draining through the handler they were written for — zero in-flight breakage with zero new mechanism. Rule: a payload field may be **added** (readers ignore unknown members, the `System.Text.Json` default); it may never be removed, renamed or repurposed. | **A `ContractVersion` bump protocol** — requires a persisted version column on both sides plus negotiation code, for a system that has exactly one version and two kinds. **A per-kind schema registry** — the proposal's own Low risk row names this as the over-build to avoid. |
| **Materialization contract** (deferral 4) | **Handler registry keyed by `payload_kind`, invoked INSIDE `ApplyInbound`'s existing transaction.** `IInboundEffectHandler { string PayloadKind; void Apply(SyncEnvelope, SqliteTransaction) }` lives in `Commerce.BranchNode` (it needs the SQLite transaction; `Commerce.Domain` stays dependency-free and owns only the payload records). `ApplyInbound` becomes: dedup check → **resolve handler → `Apply` → insert the `inbox` row → one commit**. Dedup and materialization therefore succeed or fail together by construction, satisfying the proposal's success criterion structurally rather than by test discipline. **An unknown `payload_kind` rolls back and returns a new `InboundApplyOutcome.UnknownKind`** — no `inbox` row is written, so the sender retries after upgrade instead of the envelope being swallowed as applied. Two handlers ship: `SaleInboundHandler` (branch-authored kind, inbound no-op beyond dedup, stated explicitly) and `OrderInboundHandler` (writes `inbound_orders`). | **Visitor / dispatch on the envelope** — `SyncEnvelope` is a sealed `Commerce.Domain` record; a visitor forces Domain to name every handler, inverting the dependency and making each new kind an edit to a shared sealed type. **Handler outside the transaction** — reproduces today's exact defect (Verified 4), where `DuplicateIgnored` confirms an order nothing applied. |
| **Retry: where and how** (deferral 3) | **Policy in `Commerce.BranchNode`, trigger in the POS shell; fixed interval, no backoff curve, no dead letter.** `sync_outbox` gains `attempt_count INTEGER NOT NULL DEFAULT 0`, `last_attempt_at_utc TEXT NULL`, `last_error TEXT NULL` — the **durable** replacement for `SyncButton_Click`'s in-memory `List<string> failures`, readable later by the named future admin tool and invisible in the POS UI per answer (d). `SyncButton_Click`'s body is extracted verbatim into `RunSyncAsync(SyncTrigger trigger)`; a WPF `DispatcherTimer` (`SyncScheduler`, fixed 60 s) plus a run at startup and a fire-and-forget nudge after each sale commit call the **same** method, with a reentrancy flag so a sweep never overlaps. A row stays `Pending` forever and is retried on every sweep. Justification: answer (c) demands "sync the moment conditions allow" — a periodic sweep *is* connectivity detection, since a push attempt against a down link fails in milliseconds and needs no network-change subscription; answer (a) (no incident history) plus answer (d) (no operator surface) remove the justification for exponential backoff and make a dead letter actively harmful, because a terminal state nobody can see is a silently dropped sale. The trigger cannot live in `Commerce.BranchNode`: it has no HTTP dependency (`CloudSyncClient` is a `Commerce.Pos.Windows` type) and a hosted daemon is an explicit non-goal. | **Exponential backoff + dead letter** — designed against imagined failure rates the proposal names as a Medium risk, and a dead letter with no visible surface violates answer (d) by losing a sale silently. **A hosted background service / Windows service** — explicit non-goal. **Manual-only** — answer (c) rules it out. **`NetworkChange.NetworkAvailabilityChanged`** — reports link state, not cloud reachability (captive portal, DNS, down API), so it would both miss and falsely trigger; the sweep subsumes it. |
| **ADR** (deferral 6) | **Author `ADR-012-sync-ownership-and-payload-evolution.md` in this change.** These are cross-cutting, long-lived rules (table shape, payload evolution, retry policy, envelope-vs-cursor selection) that later phases must obey; `ADR-011` set the precedent that a durable rule gets an ADR rather than living in an archived change folder. `docs/architecture/synchronization.md` is rewritten to describe the implemented mechanics and to link the ADR. | **Design-only decisions** — the proposal explicitly permits it, but the next domain would rediscover the rule by reading an archived SDD folder, which is precisely how the current undocumented state arose. |
| **Envelope vs. cursor selection rule** (scope 4) | **Rule: use a push envelope when the fact is a discrete, idempotent, per-operation event owned by the sender (`operation_id` is meaningful); use a cursor/replica pull when the receiver needs the sender's current full-row view of reference data it does not own.** Worked classification: `"sale"` (branch→cloud event) and `"order"` (cloud→branch event) are envelopes; `customers_replica`, `catalog_replica`/`price_replica` (all keyed by `sync_cursors.channel`) are cursor pulls of cloud-owned reference data. Corollary: an envelope kind must never be used to replicate mutable reference state, and a replica row must never be treated as an audit fact. | **A single unified channel** — out of scope, and the four existing channels prove both idioms are load-bearing. |

## Data Flow

```text
Branch -> cloud (sale; ADR-002: the sale path never awaits any of this)
  CompleteOfflineSale / CompleteScannedSale
    -> SalePayloadV1 -> JSON  <- NOT "{}"; SyncEnvelope ctor rejects it
    -> ONE SQLite tx: sale_effects (+ sale_lines) + sync_outbox(status='Pending')
    -> returns immediately; no cloud call on this path        [unchanged]

Automatic sync (POS process only; no daemon)
  startup | DispatcherTimer 60s | after a sale commit | SyncButton_Click
    -> RunSyncAsync(trigger)      <- ONE body, four callers, reentrancy-guarded
    -> GetPendingOutbox = sync_outbox UNION legacy outbox
                                   legacy rows: payload REBUILT from
                                   sale_id/total_amount + sale_effects.sale_kind
    -> PushAsync -> ok  : Acknowledge (whichever table holds the id)
                 -> fail: attempt_count += 1, last_attempt_at_utc, last_error
                          row stays Pending  <- never terminal, never dead-lettered
    -> POS UI updated ONLY when trigger = Button   [answer (d): invisible]

Cloud -> branch (order)
  CloudOrderStore.AttemptDelivery
    -> OrderPayloadV1 -> JSON   <- replaces the literal "{}"
    -> BranchSyncStore.ApplyInbound(envelope)
         ONE tx:  dedup on operation_id
                  -> handler = registry[payload_kind]
                       unknown -> ROLLBACK, UnknownKind, no inbox row
                  -> handler.Apply(envelope, tx)      <- inbound_orders
                  -> INSERT inbox(operation_id, applied_at_utc)
                  -> COMMIT                <- dedup + materialization, together

Untouched by construction
  outbox DDL ................. byte-identical (drained, never written again)
  sale_effects / sale_lines / inbox / sync_cursors / *_replica ... unchanged
  cloud sync_inbox + both CloudInboxStores ....... already generic, unchanged
  openspec/changes/commerce-payments/ ............ byte-identical
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `src/Commerce.Domain/Sync/SyncEnvelope.cs` | **Modify** | Constructor guard: `Payload` must be a valid JSON object and must not be `{}`. `ContractVersion` documented as frozen at `1`. |
| `src/Commerce.Domain/Sync/Payloads/SalePayloadV1.cs` | Create | `SaleId`, `TotalAmount`, `SaleKind`, `OccurredAtUtc`, `Lines[]`. |
| `src/Commerce.Domain/Sync/Payloads/OrderPayloadV1.cs` | Create | `OrderId`, `DestinationBranchId`, `Origin`, `Lines[]` (frozen `LineTotal`s). |
| `src/Commerce.Domain/Sync/SyncPayloadCodec.cs` | Create | The one `System.Text.Json` serialize/deserialize pair; ignores unknown members (the additive-evolution rule, enforced by options). |
| `src/Commerce.Domain/Sync/InboundApplyOutcome.cs` | **Modify** | Add `UnknownKind`. |
| `src/Commerce.BranchNode/IInboundEffectHandler.cs` | Create | `PayloadKind` + `Apply(SyncEnvelope, SqliteTransaction)`. |
| `src/Commerce.BranchNode/InboundEffectRegistry.cs` | Create | `IReadOnlyDictionary<string, IInboundEffectHandler>`; no discovery, no reflection, two entries. |
| `src/Commerce.BranchNode/Handlers/SaleInboundHandler.cs` | Create | Dedup-only by design, stated rather than implied. |
| `src/Commerce.BranchNode/Handlers/OrderInboundHandler.cs` | Create | Writes `inbound_orders` in the caller's transaction. |
| `src/Commerce.BranchNode/BranchSyncStore.cs` | **Modify** | `sync_outbox` + `inbound_orders` DDL appended to the constructor block; writes go to `sync_outbox`; `GetPendingOutbox` unions + rebuilds legacy payloads; `Acknowledge`/`GetStatus`/`RowExists` span both tables; `RecordAttemptFailure`; `ApplyInbound` invokes the registry inside its transaction. **No edit to the `outbox` / `sale_*` / `inbox` DDL.** |
| `src/Commerce.BranchNode/BranchNodeService.cs` | **Modify** | Build real `SalePayloadV1` payloads in both sale methods. |
| `src/Commerce.Cloud.Api/Ordering/CloudOrderStore.cs` | **Modify** | Real `OrderPayloadV1`; treat `UnknownKind` as not-confirmed (stays `Pending`). |
| `src/Commerce.Pos.Windows/SyncScheduler.cs` | Create | `DispatcherTimer` (60 s) + startup run + reentrancy guard; swallows and records all exceptions. |
| `src/Commerce.Pos.Windows/MainWindow.xaml.cs` | **Modify** | Extract `RunSyncAsync(SyncTrigger)`; `SyncButton_Click` delegates to it; UI text updated only for `SyncTrigger.Button`; post-sale nudge. `CommitSaleButton_Click` still never awaits sync. |
| `docs/architecture/decisions/ADR-012-sync-ownership-and-payload-evolution.md` | Create | Outbox shape, payload evolution, retry policy, envelope-vs-cursor rule. |
| `docs/architecture/synchronization.md` | **Modify** | Replace the pre-implementation text with the envelope / outbox-inbox / cursor-replica mechanics and the selection rule; link ADR-012. |
| `deploy/db/migrations/` | **None** | The cloud `sync_inbox` is already generic (Verified 1); **no SQL migration ships in this change.** |
| `tests/Commerce.Domain/SyncPayloadTests.cs` | Create | Guard rejects `{}`/invalid JSON; round-trip; unknown member ignored. |
| `tests/Commerce.BranchNode/GenericOutboxTests.cs`, `InboundMaterializationTests.cs`, `LegacyOutboxDrainTests.cs` | Create | Payload-only enqueue; atomic dedup+materialize; `UnknownKind` rollback; drain of a pre-existing sale-shaped row. |
| `tests/Commerce.BranchNode/SaleRegressionTests.cs` | **Modify** | Existing sale scenarios re-run unchanged as the regression anchor. |

## Interfaces / Contracts

```csharp
// Registry, not a framework: two entries, no discovery, no reflection.
public interface IInboundEffectHandler
{
    string PayloadKind { get; }
    void Apply(SyncEnvelope envelope, SqliteTransaction transaction);
}

// Dedup and materialization share one transaction; an unknown kind rolls the
// whole thing back so the sender retries after upgrade.
public enum InboundApplyOutcome { Applied, DuplicateIgnored, Denied, UnknownKind }
```

```sql
-- BranchSyncStore constructor DDL, APPENDED. `outbox` is not touched.
CREATE TABLE IF NOT EXISTS sync_outbox (
    operation_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL,
    organization_id TEXT NOT NULL, aggregate_id TEXT NOT NULL,
    aggregate_version INTEGER NOT NULL, actor_id TEXT NOT NULL,
    correlation_id TEXT NOT NULL, occurred_at_utc TEXT NOT NULL,
    payload_kind TEXT NOT NULL,
    payload TEXT NOT NULL CHECK (json_valid(payload) AND payload <> '{}'),
    status TEXT NOT NULL, acknowledged_at_utc TEXT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,      -- durable, replaces the
    last_attempt_at_utc TEXT NULL,                 -- in-memory failure list
    last_error TEXT NULL);
CREATE TABLE IF NOT EXISTS inbound_orders (
    order_id TEXT PRIMARY KEY, organization_id TEXT NOT NULL,
    destination_branch_id TEXT NOT NULL, payload TEXT NOT NULL,
    materialized_at_utc TEXT NOT NULL);
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | `SyncEnvelope` rejects `"{}"`, empty, and non-JSON payloads | xUnit |
| Unit | `SalePayloadV1`/`OrderPayloadV1` round-trip; an unknown member deserializes without throwing (additive-evolution rule) | xUnit |
| BranchNode | An outbox row is enqueued for a payload kind with **no `SaleEffect`** and no sale-specific column | xUnit + SQLite temp file |
| BranchNode | Existing `"sale"` scenarios produce identical observable behaviour before/after (regression anchor) | existing suite, unchanged assertions |
| BranchNode | A pre-seeded **legacy** `outbox` row is returned by `GetPendingOutbox` with a reconstituted `SalePayloadV1`, acknowledges correctly, and the `outbox` DDL is byte-identical afterwards | xUnit + `PRAGMA table_info` assertion |
| BranchNode | `ApplyInbound` with an **unknown** kind writes **no** `inbox` row and returns `UnknownKind`; a handler that throws leaves **no** `inbox` row (atomicity) | xUnit, `SimulateInterrupted*` idiom |
| BranchNode | Replaying one `operation_id` materializes **exactly one** `inbound_orders` row | xUnit |
| BranchNode | A failed push leaves `attempt_count = 1` + `last_error` **durably on disk after reopening the file** | xUnit |
| POS | `RunSyncAsync` is reentrancy-guarded (a second trigger during a sweep is a no-op) and a throwing push never surfaces in the UI for a non-Button trigger | xUnit over the extracted method with a fake client |
| POS (ADR-002) | A sale commits with the scheduler running **and** the cloud unreachable; the commit does not await, block on, or fail from the sweep | xUnit |

## Threat Matrix

| Native row | Applicability |
|---|---|
| Routing | **N/A** — no endpoint is added or re-mapped; sync reuses the existing `/sync` receiver and device-token auth. |
| Process integration | **N/A** — the scheduler is an in-process WPF `DispatcherTimer`, not a subprocess, service or external integration; an explicit non-goal forbids a hosted process. Concurrency safety is covered by the reentrancy and ADR-002 rows in Testing Strategy. |
| Shell / subprocess | N/A — none introduced. |
| Executable-file classification | N/A — no upload/download or classification boundary. |
| Git repository selection / Commit / Push / PR | N/A — no product code runs Git or PR automation. |
| Documentation-like paths | N/A — no file-classification boundary. |

## Migration / Rollout

**No SQL migration, no data migration, no coordinated device replacement.** The
branch change is an **application upgrade only**: two `CREATE TABLE IF NOT EXISTS`
statements run against the existing `branch.db` on first start of the upgraded
POS executable — the same additive pattern `EnsureSaleKindColumnExists`
documents. The cloud requires no schema change (Verified 1).

**Legacy drain.** Pre-existing `Pending` rows in `outbox` are pushed and
acknowledged by the unioned read path; nothing is copied, rewritten or deleted.
The legacy table becomes inert once its pending count reaches zero; **dropping it
is a later, separately-proposed change** gated on that observation.

**Rollback.** Revert the commit. `outbox`, `sale_effects`, `sale_lines`, `inbox`,
the cursor/replica tables and the cloud schema were never modified, so the old
executable resumes on `outbox` and simply ignores `sync_outbox`/`inbound_orders`.
**Cutover point**: rollback becomes lossy the moment a row exists in
`sync_outbox` that the reverted code will not push — which is any row written
after the upgrade, sale kinds included. Recovery is forward-fix (re-deploy), not
a schema inverse; no row is ever destroyed, only unread. **Narrowest rollback**:
stop constructing `SyncScheduler` — the automatic trigger disappears, the manual
button is unchanged, and the generic tables keep working.

## Review Workload Forecast

Decision needed before apply: Yes
Chained PRs recommended: Yes
400-line budget risk: High

Estimated ~900–1,100 authored lines against the 400-line default budget
(`delivery_strategy = ask-on-risk`). Four dependency-ordered units:

| Unit | Scope | Budget | Boundary | Rollback |
|---|---|---|---|---|
| 1 | Payload records, codec, `SyncEnvelope` guard, `UnknownKind` + unit tests | ~230 | `{}` is unconstructible | Revert; producers still compile only after Unit 2 |
| 2 | `sync_outbox` DDL, writes, unioned read, legacy drain, attempt columns, `BranchNodeService` payloads + tests | ~330 | Payload-only enqueue works; sale regression green; `outbox` DDL byte-identical | Revert; legacy table untouched |
| 3 | Handler interface, registry, two handlers, `inbound_orders`, transactional `ApplyInbound`, `CloudOrderStore` payload + tests | ~280 | Dedup and materialization cannot succeed apart | Revert; inbox returns to dedup-only |
| 4 | `SyncScheduler`, `RunSyncAsync` extraction, ADR-012, `synchronization.md` + tests | ~260 | Sale never blocked by a sweep | Stop constructing the scheduler |

Units 1–3 are invisible to the operator; Unit 4 is the only behavioural change
at the terminal and is the one with a one-line rollback.

## Open Questions

- [ ] Non-blocking, for `sdd-apply`: the 60 s sweep interval is a starting value,
      not a measured one — answers (a) and (c) give a principle ("as soon as
      conditions allow") but no SLA. It is a single constant with no dependent
      logic, so tuning it later is a one-line change.
- [ ] Non-blocking: `payment_outbox` (Phase E) remains a second generic outbox
      table with the same shape as `sync_outbox`. Consolidation is a **later,
      separately-proposed change**; this design requires no edit to
      `commerce-payments` and makes no claim on its schedule.
- [ ] Non-blocking: `SaleInboundHandler` exists only so that no `payload_kind`
      reaching the branch is unregistered. If the branch never legitimately
      receives its own `"sale"` kind, a later change may remove it and let the
      kind fall through to `UnknownKind`.
- [ ] Non-blocking: cloud `Order` state is still in-memory (`CloudOrderStore`),
      so a cloud restart can leave a durable `inbound_orders` row whose order is
      gone. This is the already-accepted Phase B persistence gap, unchanged here.
