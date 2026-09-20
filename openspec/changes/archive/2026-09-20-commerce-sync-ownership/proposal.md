# Proposal: Commerce Sync Ownership (Phase F)

## Intent

**Synchronization works, but nobody owns its rules.** Four sync mechanics exist in the repository with no written contract governing any of them:

- The branch outbox is **sale-shaped, not generic**. `src/Commerce.BranchNode/BranchSyncStore.cs:77-92` hardcodes `sale_id TEXT NOT NULL` and `total_amount TEXT NOT NULL`; `InsertOutboxRow` (~850-877) always requires a `SaleEffect`. There is no payload-only insertion path. The cloud side (`PostgresCloudInboxStore` in `CloudInboxStore.cs`) is already generic on `payload_kind` + `payload jsonb` — the two halves of the same channel disagree.
- **`SyncEnvelope.Payload` is a lie.** In `src/Commerce.Domain/Sync/SyncEnvelope.cs` it is the literal string `"{}"` at every call site; real data travels through out-of-band method parameters and hardcoded columns. `ContractVersion` is likewise hardcoded to `1` with no negotiation code.
- **Retry is 100% manual.** `SyncButton_Click` (`src/Commerce.Pos.Windows/MainWindow.xaml.cs` ~315-377) is the only trigger. Failures append to an in-memory list rendered as text; the row stays `Pending` until a cashier clicks again. No backoff, no dead letter, no alarm.
- **The inbox never materializes anything.** `BranchSyncStore.ApplyInbound` (~397-430) records only `(operation_id, applied_at_utc)` for de-duplication. The single cloud→branch kind (`"order"`, via `CloudOrderStore.AttemptDelivery`) applies its data bespoke, outside inbox logic.

On top of that, **two unrelated sync patterns coexist undocumented**: envelope push (`"sale"`, `"order"`) versus cursor-based full-row replica pull (`customers_replica` + `sync_cursors`, and a parallel catalog/price cursor channel). No ADR governs outbox/inbox schema shape, retry policy, or payload-kind evolution — ADR-002 and ADR-003 stop short.

**Why now.** Phase E (`commerce-payments`) hit this wall and routed around it: it locked a *parallel* payment-effect path explicitly to avoid the sale-shaped `outbox`, naming Phase F as the consolidator (its Decision 1 and risk row). Each further domain that routes around this gap adds a sync idiom nobody owns.

This change is **planning only** (`openspec/config.yaml` `approval_scope: planning-only`).

## Scope

### In Scope

1. **Generic outbox contract.** Bring the branch `outbox` to the cloud inbox's shape — `payload_kind` + payload body — with a payload-only insertion path that does not require a `SaleEffect`. Includes deciding the fate of `SyncEnvelope.Payload`: **use it for real, or delete the field**; a third round of "always `{}`" is not an acceptable outcome.
2. **Retry and failure policy.** Define what happens to a `Pending` row nobody clicks on: whether automated retry/backoff exists, what terminal failure (dead-letter) means, and how a stuck row becomes visible to a human. Manual-only is an admissible outcome **only with written reasoning and a visibility mechanism**; silent in-memory failure lists are not.
3. **Inbox→domain materialization contract.** Define the reusable seam that turns an applied inbox row into domain state, replacing bespoke per-caller application (`CloudOrderStore.AttemptDelivery`). De-duplication and materialization must not be able to drift apart.
4. **Pattern selection rule (envelope vs. cursor/replica).** Write down the decision rule a new domain follows to pick push-envelope or pull-cursor, with the existing four channels classified against it as worked examples.
5. **Minimal payload-kind versioning story.** Generalizing the outbox makes multiple payload shapes real; define the least mechanism that lets a kind evolve without breaking in-flight rows, or explicitly record why `ContractVersion: 1` stays frozen.
6. **A sync-ownership ADR** (or an explicit, reasoned deferral to `sdd-design` recorded in the design artifact).
7. **Reconciling `docs/architecture/synchronization.md`**, which predates the implementation and mentions neither the envelope, the outbox/inbox tables, nor the cursor/replica pattern.

### Out of Scope (non-goals)

- **Rewriting Phase E's parallel payment path.** Consolidation is a later, separately-proposed opportunity (see Decision 1). Phase E does not block on this change and this change does not block on Phase E.
- **Migrating the existing cursor/replica channels** (customers, catalog, price) onto envelopes. This change classifies them; it does not move them.
- **Real-time / push-based transport, background daemons, or a sync scheduler service.** Automation policy may be *defined*; a new hosted process is not in this slice.
- **Conflict resolution or merge semantics beyond current idempotency.** ADR-002 offline authority is unchanged and not re-litigated.
- **Multi-branch or branch↔branch sync.** The topology stays branch↔cloud.
- **Changing what any existing payload kind means.** `"sale"` and `"order"` semantics are preserved; only their transport shape is in question.
- **Observability infrastructure** (dashboards, metrics backends). Visibility requirements may be specified; the stack is not selected here.

## Decisions

### Locked here

1. **Phase E and Phase F are non-blocking in both directions.** Phase E's parallel payment-effect path was a deliberate decoupling. This change MUST NOT require Phase E to be rewritten, and MUST NOT wait on Phase E to land. Phase E's path becomes a **candidate** for later consolidation onto the generic outbox, proposed separately.
2. **The generic outbox follows the cloud inbox's existing shape** (`payload_kind` + payload body), not a newly invented third shape. The two halves of one channel converge; they do not each get a design.
3. **Existing `"sale"` behavior is preserved byte-for-byte at the semantic level.** Offline sale continuity and atomic local persistence (`branch-offline-sync`, ADR-002) are hard constraints: no generalization may make a local sale depend on cloud reachability or on sync succeeding.
4. **Idempotency stays keyed on `operation_id`.** Whatever materialization contract emerges, exactly-once application under replay is non-negotiable and remains the existing primitive — this change generalizes payload carriage, not identity.
5. **`SyncEnvelope.Payload` must stop being decorative.** Either it carries the payload or it is removed. Design picks which; specs may not preserve the current pretense.

### Explicitly deferred to `sdd-design`

- Payload encoding on SQLite (`TEXT` JSON vs. `BLOB`) and whether the branch mirrors Postgres `jsonb` semantics.
- Migration strategy for existing `outbox` rows with populated `sale_id`/`total_amount` (in-place widen + backfill vs. new table + drain).
- Whether retry automation lives in the POS shell, the branch node, or stays operator-triggered — and the concrete backoff curve if automated.
- The materialization seam's shape (handler registry keyed by `payload_kind` vs. a visitor/dispatch on the envelope) and where it lives across `Commerce.BranchNode` / `Commerce.Application`.
- Whether the versioning story is a `ContractVersion` bump protocol, additive-only payload evolution, or per-kind schema registry.
- Whether a new ADR is authored in this phase or the decisions live only in the design artifact.

### Product questions — answered by the user 2026-09-20

- (a) **No known incident of a lost or badly-delayed sale.** Manual sync has been operationally sufficient so far — there is no incident history forcing automated retry as a defensive necessity.
- (b) **The local cashier/operator is accountable** when sync fails or lags at a branch. No central-operations escalation path is expected for this phase.
- (c) **As fast as possible, with no fixed SLA — this is a stated architectural principle, not a numeric target.** The user's own framing: "el POS intenta vender como sea, y ni bien esté en condiciones se sincroniza" (the POS sells no matter what; it syncs the moment conditions allow). This restates and extends ADR-002's offline-sale-first principle to sync timing: **sync should be attempted automatically as soon as connectivity/conditions allow, not gated solely behind a manual button click.** This meaningfully informs Scope item 2 — even absent known incidents (answer a), the product principle itself favors an automatic "sync when possible" trigger over pure manual-only, though without an automated backoff/dead-letter apparatus being demanded.
- (d) **Invisible to the POS operator for now.** A future admin tool to see total system-wide sync status is explicitly named as a later, separate capability — not built in this phase.

**Net effect on Scope item 2 (retry policy)**: "manual-only" as originally scoped is **not** what the user described. The direction is **automatic sync attempts triggered by conditions becoming favorable** (e.g., connectivity restored), still without an operator-visible status surface and without proven need for exponential backoff/dead-letter sophistication given no incident history. `sdd-design` must reconcile these: an automatic-trigger mechanism with a simple, undemonstrated-need-for-complexity retry shape, kept invisible to the POS operator.

## Capabilities

### New Capabilities

None. This phase changes how an existing capability's sync contract is expressed, not what the product can do.

### Modified Capabilities

- `branch-offline-sync`: the outbox becomes generic on payload kind with a real payload channel; pending/failed effects gain a defined retry and visibility policy; inbound envelopes gain a defined materialization contract beyond de-duplication; the envelope-vs-cursor pattern selection rule becomes a stated requirement. Local sale continuity and `operation_id` idempotency requirements are preserved unchanged.

## Approach

Converge the branch outbox onto the shape the cloud inbox already proved (`payload_kind` + payload), so generalization is a **symmetry fix**, not a new design. With the payload channel real, the three remaining gaps become tractable in order: a generic row can carry a kind, a kind can be dispatched, and a dispatchable kind can have a retry policy attached without per-domain code.

The pattern selection rule is written as documentation grounded in the four channels that already exist — envelope where an effect is a discrete, idempotent, per-operation fact (sale, order), cursor/replica where the branch needs a current full-row view of cloud-owned reference data (customers, catalog, price) — so the rule is descriptive of working code before it is prescriptive for new domains.

Existing `"sale"` flow is the regression anchor throughout: generalization is correct only if the sale path behaves identically.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Commerce.BranchNode/BranchSyncStore.cs` | Modified | Generic `outbox` schema; payload-only insertion path; `ApplyInbound` materialization seam |
| `src/Commerce.Domain/Sync/SyncEnvelope.cs` | Modified | `Payload` used for real or removed; `ContractVersion` semantics decided |
| `src/Commerce.Cloud.Api/.../CloudInboxStore.cs` | Referenced | Already generic — the shape being converged onto, not redesigned |
| `src/Commerce.Cloud.Api/.../CloudOrderStore.cs` | Modified | Bespoke `AttemptDelivery` application moves onto the materialization contract |
| `src/Commerce.Pos.Windows/MainWindow.xaml.cs` | Modified | Sync failure surfacing beyond an in-memory list; trigger per retry policy |
| `deploy/db/migrations/00NN_*.sql` | New | Outbox generalization migration (branch-side and any cloud counterpart) |
| `docs/architecture/synchronization.md` | Modified | Document envelope, outbox/inbox, and cursor/replica patterns + selection rule |
| `docs/architecture/decisions/ADR-0NN-*.md` | New (or deferred) | Sync ownership: schema shape, retry policy, payload evolution |
| `openspec/changes/commerce-payments/` | **Untouched** | Decision 1: non-blocking in both directions |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Generalization regresses the offline sale path | Medium | Decision 3 hard constraint; existing `branch-offline-sync` sale scenarios re-run unchanged as the regression anchor |
| Migration of live `outbox` rows with `NOT NULL sale_id` fails or strands rows | Medium | Migration strategy explicitly deferred to design with a drain-vs-backfill decision; inverse statements required |
| Scope creep into a background sync service | Medium | Explicit non-goal; automation *policy* may be defined, a hosted process may not ship in this slice |
| Retry policy designed against imagined failure rates | Medium | Product questions (a)–(d) gate the policy; manual-only stays admissible with reasoning |
| Materialization contract over-abstracted for two payload kinds | Medium | Two real kinds (`"sale"`, `"order"`) are the only required consumers; no speculative handler framework |
| Payload versioning becomes a schema-registry project | Low | Scope item 5 explicitly permits "freeze at `ContractVersion: 1` with reasoning" as a valid outcome |
| Phase E's parallel path drifts further while this lands | Low | Accepted knowingly per Decision 1; consolidation is a named later proposal, not a hidden dependency |
| `docs/architecture/synchronization.md` stays stale again | Low | Listed as a success criterion, not a nice-to-have |

## Rollback Plan

Revert the commit: `BranchSyncStore`, `SyncEnvelope`, `CloudOrderStore` and the POS sync trigger return to their current shape. The migration ships inverse statements restoring `sale_id`/`total_amount` as `NOT NULL` columns. **Caveat**: rollback is safe only while every persisted outbox row is still expressible in the sale-shaped schema — once a non-sale payload kind has been written, the inverse migration cannot represent those rows and forward-fix is required. Design MUST record this cutover point explicitly. Narrower rollback: keep the generic schema but stop inserting non-sale kinds, which leaves the sale path on its current behavior.

## Dependencies

- ADR-002 (offline sale authority) and ADR-003 (idempotency / pending-order policy) — accepted, constrain this change, not re-litigated.
- Existing `branch-offline-sync` spec requirements (local sale continuity, retryable delivery with idempotent effects, freshness/audit, replication of effective prices) — the delta must preserve all four.
- Phase E `commerce-payments` — **explicitly non-blocking** in both directions (Decision 1).
- Product questions (a)–(d) answered 2026-09-20 (see Decisions); the direction favors automatic sync-when-possible over pure manual-only.

## Success Criteria

- [ ] An outbox row can be enqueued for a payload kind with no `SaleEffect` and no sale-specific columns, proven by test.
- [ ] `SyncEnvelope.Payload` either carries real payload data end-to-end or no longer exists; a test or structural assertion prevents a return to constant `"{}"`.
- [ ] The existing `"sale"` flow produces identical observable behavior before and after generalization (regression test against current `branch-offline-sync` scenarios).
- [ ] Replaying an envelope under the same `operation_id` applies exactly once, and de-duplication and materialization cannot succeed independently of each other.
- [ ] An inbound `"order"` envelope materializes through the shared contract, not through bespoke `AttemptDelivery` code.
- [ ] A failed sync attempt produces a durable, human-visible outcome rather than an in-memory list lost on restart.
- [ ] The retry/backoff/dead-letter policy is written down — including an explicit, reasoned "manual only" if that is the choice.
- [ ] `docs/architecture/synchronization.md` describes the envelope, outbox/inbox, and cursor/replica patterns, and states the selection rule between them.
- [ ] `openspec/changes/commerce-payments/` is byte-identical after this change.
- [ ] `dotnet test Commerce.sln` and `dotnet build Commerce.sln` pass.

## Proposal question round

**Round 1 (answered)** — the user was asked to scope "sync ownership", which was too vague to propose against, and selected **all four** identified sub-problems: generic outbox, retry/backoff policy, inbox materialization contract, and the envelope-vs-cursor selection rule. Scope items 5–7 (payload versioning, ADR, doc reconciliation) were added as direct consequences and are marked with explicit "or defer with reasoning" escape hatches.

**Round 2 (answered)** — no known sync-loss incidents; the local operator is accountable; sync should trigger automatically as soon as conditions allow rather than wait on a manual click (extending ADR-002's offline-sale-first principle to sync timing), with no operator-visible status surface for now (a future admin-facing tool is named as a separate later capability). This resolves Scope item 2 away from pure "manual-only" toward "automatic-trigger, simple retry shape, invisible to the POS operator."
