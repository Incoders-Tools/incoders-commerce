# Persist Web Orders

## Objective
Store web orders in Postgres so they survive API restarts, number them
`P{branch}-W-{seq}` (e.g. `P01-W-37`) as decided in
`odd/tasks/human-document-numbers.md`, and stop losing other business data
kept only in memory.

## Problem
- `CloudOrderStore` keeps orders in an in-memory `Dictionary<Guid, Order>`
  singleton (`src/Commerce.Cloud.Api/Program.cs:158`,
  `Ordering/CloudOrderStore.cs`): every API restart loses all registered and
  guest orders. Its idempotent lookup ignores the organization.
- In production every order stays `PendingDestination/DestinationOffline`
  (no caller of `RetryDelivery`, cloud has no branch store).
- `SubmitGuestAsync` consumes the guest email verification before the order is
  stored; a failure in between burns the verification with no order.
- `InMemoryAuditSink` (`Program.cs:149`) holds customer catalog
  access-denial audit entries only in memory.
- Web: `api/types.ts:137-149` types the outcome order as `{ id, ... }` while the
  server serializes `orderId`; no number is shown after ordering.

## Why
Owner decisions: orders must never be lost (2026-09-30, mandatory next
feature); GUIDs are never shown to users; numbering format `P01-W-37`
(`docs/document-numbering.md`).

## Scope (authorized)
- `orders` + `order_lines` (+ per-branch order counter) with RLS, an
  `IOrderStore` abstraction, `PostgresOrderStore` replacing the in-memory store
  in DI; idempotent submit per (organization, order id); number assigned at
  insert in the same transaction; guest verification consumed in that same
  transaction; readiness requires the new objects.
- `OrderPayloadV1` optional trailing `OrderNumber`; web confirmation shows the
  number with a composition tooltip; fix the outcome type.
- Persist the access-denial audit into `audit_log`.
- Docs/specs.

Out of scope: real branch delivery/confirmation flow (no production caller
today), `StaffOrderScreen` raw GUID form (staff/testing tool; follow-up).

## Constraints
- Commit on `dev`; Conventional Commits; no AI attribution; strict TDD.
- Next migration `0025`; idempotent; FORCE RLS with the NULLIF tenant policy;
  composite FK to `branches_org_scoped_uk`; `init-rls.sql` verbatim append;
  fixture chains; `deploy/README.md`; `docs/document-numbering.md` table.
- Counter race-free: advisory lock on a new seed (3) keyed by branch, or a
  counter row `UPDATE ... RETURNING`, same transaction as the insert;
  `UNIQUE (organization_id, branch_id, sequence)`. Idempotent resubmit returns
  the same number without advancing the counter.
- Enums persisted as text; full `OrderLineSnapshot` persisted.
- Unit tests that `new CloudOrderStore()` keep a fake behind `IOrderStore`;
  persistence behavior is tested against Postgres.
- Keep each commit small (review tool rejects candidates over ~2000 lines).
- Full .NET suite from a throwaway worktree; Postgres + pgbouncer up; never
  `--artifacts-path`; never `docker compose down`; dev DB migrations applied by
  the parent.

## TDD
Strict. Runners: `dotnet test tests/Commerce.Integration --filter <Class>`,
`npm test` in `src/Commerce.Web`.

## Delivery
Trunk on `dev`; work-unit commits under RDD. First review boundary: `84c0f7b`
(previous slice `7f38033..84c0f7b` pending).

## Tasks
- [x] T1 Persistence: migration `0025_orders.sql`, `IOrderStore`,
  `PostgresOrderStore` (submit idempotent per org, find, list pending),
  `P{branch}-W-{seq}` allocation at insert, DI swap, readiness, fixture
  chains; guest branch without a `branches` row → typed denial.
  Route: delegated direct.
- [x] T2 Guest verification consumed in the order transaction (no burned
  verification on failure). Route: delegated direct (with T1 or after).
- [x] T3 Number on the wire and in the web: `OrderPayloadV1.OrderNumber`,
  outcome type fix, number + tooltip in `OrderScreen` (guest and registered),
  i18n es/en. Route: delegated direct.
- [x] T4 Persist the access-denial audit (`InMemoryAuditSink` →
  `audit_log`). Route: delegated direct.
- [x] T5 Docs/specs: `docs/document-numbering.md` (web orders now live),
  `order-payment-lifecycle`, `guest-ordering`, `private-customer-ordering`,
  `public-order-surface`, `deploy/README.md`. Route: with each task.

## Acceptance criteria
- An order submitted, then an API restart: the order is still listed and found.
- Each new order has a unique `P..-W-..` number per branch; resubmitting the
  same order id returns the same number.
- A failed order submission never consumes the guest verification.
- No GUID shown to the customer; the number has a composition tooltip.

## Progress
- 2026-09-30: placeholder created from the owner's decision.
- 2026-10-01: planned after delegated mapping (4-file trigger). Also found:
  `InMemoryAuditSink` (now T4) and `BootstrapTokenRegistry` (ephemeral by
  design, left in memory).
- 2026-10-01: T1 and T2 done in a throwaway worktree, integrated into `dev`
  by fast-forward. Route: delegated direct (writers; first writer cut off by a
  usage limit, resumed and its WIP reviewed critically).
  - Commits: `feat(db)` 0025 migration (+readiness, init-rls, README, ~590
    lines); `feat(domain)` OrderNumber (~190); `feat(api)` IOrderStore +
    PostgresOrderStore + OrderDelivery + store tests (~900, one coherent unit:
    the store and its tests cannot be split without a non-building commit);
    `feat(api)` DI swap, async endpoints, in-memory store moved to tests,
    endpoint tests seed a real branch (~280); `feat(api)` guest verification
    spent in the order transaction (~520 with specs and docs).
  - RED: after the DI swap the full suite failed exactly 2 tests
    (`OrderPendingListTests`, `CustomerOrderSubmissionTests`: random
    destination branch GUID, now a typed denial); T2 tests were RED 8 of 21
    (verification not consumed / not atomic) before the store spent it.
  - Reviewed and fixed in the inherited work: duplicate
    `OrderSubmissionOutcome` types (build break), branch-name collision in the
    store test seed, missing endpoint-level restart and unknown-branch tests;
    0025 itself (idempotent guard block, FORCE RLS + NULLIF policy, composite
    FK, init-rls verbatim, readiness negative test) checked and left as is.
  - Decisions: (1) the guest verification is spent by one conditional UPDATE
    inside the order transaction, after the branch check and the branch lock;
    (2) resubmitting an existing order id returns that order without spending
    anything, but a guest retry must present the ticket that admitted it
    (consumed_order_id match) or gets `verification-invalid`, so a stray ticket
    cannot read orders back; (3) `TryConsumeAsync` and the store's separate
    `ConsumeAsync` were removed (non-atomic, no remaining caller);
    (4) in-memory store kept only as a test fake (no number, ignores the
    verification).
  - Checks: `dotnet build Commerce.sln` 0 errors; `PostgresOrderStoreTests`
    21/21; full `dotnet test tests/Commerce.Integration` 1647 passed, 0 failed,
    0 skipped (Postgres + pgbouncer up); iconv UTF-8 clean on changed files.
    Engram mirror `odd/persist-web-orders/tasks` left for the parent to sync.

- 2026-10-01: review follow-ups and T3-T5 done in a throwaway worktree
  (`wt-orders2`), integrated into `dev` by fast-forward. Route: delegated direct
  (one writer). Review: slice A lineage `review-e3ff0d3e4c4ad43f`
  (`7f38033..5320fe9`) and slice B lineage `review-12950e47dc7a944c`
  (`5320fe9..0cdc096`), both approved and acknowledged; next review boundary
  `0cdc096`.
  - Review findings and resolutions: (1) guest CHECK let NULL pass -> new
    migration `0026_orders_guest_check.sql` (0025 is applied, never edited);
    (2) torn reads -> orders are read first, then only their lines;
    (3) delivery before commit -> delivery runs after the commit and its state
    is persisted by a follow-up transaction; a failure there leaves the order
    stored and pending; (4) unbounded pending list -> only
    `PendingDestination`, deterministic order, limit 200 (max 500);
    (5) docs: 0024 deploy-window wording, `pos_sales` vs `orders`, tooltip now
    shipped; (6) suggestions: `OrderNumber` length constant, Spanish `Describe`
    removed from the domain (tooltip is web i18n), UndefinedTable branch
    deduplicated in `PosSaleProjection`, conflict-retry test added.
  - Commits (changed lines): `fix(db)` 0026 + tests + init-rls + README (~155);
    `fix(api)` store reads/delivery/limit + tests (~367); `refactor` (~26);
    `feat(sync)` `OrderPayloadV1.OrderNumber` (~57); `feat(web)` number +
    tooltip, outcome type fix, i18n, e2e regex (~239); `feat(api)`
    `PostgresAuditSink` (~192); `docs` specs and guides (~133).
  - RED/GREEN: 0026 tests RED 2 of 6 (NULL/blank guest parts accepted, replay)
    before the migration; store tests RED 3 (list limit and status filter, torn
    read probe, phantom delivery) before the fixes; payload tests did not compile
    until the field existed. Web lib and component tests were written with the
    implementation in one step (no separate observed RED), screen tests updated
    to the real `orderId`/`orderNumber` shape.
  - Decisions: (1) delivery after commit; the stored initial state is
    pending/`DestinationOffline`, and delivery or follow-up failures are
    contained (order never lost, retry is idempotent); (2) the pending list keeps
    its endpoint contract, the limit is an optional store parameter; (3) the
    audit sink is `IAuditSink.RecordAsync` (default interface method) with a
    Postgres implementation, fail-closed, one transaction per decision under the
    organization scope, actor kind `customer`; every decision (allowed and
    denied) is written, as the in-memory sink did; (4) the number tooltip is
    built from es/en i18n keys by `lib/orderNumber.ts`; the domain no longer
    holds display text.
  - Checks: `dotnet build Commerce.sln` 0 errors; full
    `dotnet test tests/Commerce.Integration` 1657 passed, 0 failed, 0 skipped;
    `npm test` 348 passed; `npm run build` ok; `npm run lint` exit 0 (existing
    warnings); e2e standalone `tsc --noEmit` clean; iconv UTF-8 clean.
    Engram mirror `odd/persist-web-orders/tasks` left for the parent to sync.

- Final orders review (lineage review-23d44c9b050c7efa, approved and
  acknowledged; next boundary 8222618) follow-ups, 2026-10-01:
  - Owner decision: the customer-catalog access audit FAILS OPEN with alerting.
    If the `audit_log` write fails, the access decision proceeds unchanged and
    the failure is logged at Error level (organization, decision, reason,
    correlation id, exception); only cancellation still propagates.
  - Advisories and resolutions: (R4 warning + sync-over-async) `PostgresAuditSink`
    catches non-cancellation exceptions and logs; `CustomerCatalogAccessService`
    was already async end to end (`RecordAsync`), so the blocking `Record` bridge
    was removed and now throws `NotSupportedException`. (R3 warning) the sink is
    no longer the shared `IAuditSink`: the shared sink is `InMemoryAuditSink`
    again and the Postgres sink is a keyed registration used only by
    `CustomerCatalogAccessService` (`AddCustomerCatalogAccessAudit`); a test
    proves the shared sink writes no `customer` row. Follow-up, out of scope:
    the staff/branch audit entries of the other consumers still live only in
    memory and are lost at restart. (R4 warning) a post-commit delivery failure is
    logged at Error (order id and number, branch, exception) and the order stays
    pending and retryable; request cancellation after the commit no longer
    misreports the outcome (accepted, follow-up state write not cancelled).
    (R2 warnings) `orderNumber.ts` doc names the plain accepted fallback;
    `OrdersMigrationTests` has separate `OrdersMigration` (0025) and
    `GuestCheckMigration` (0026) constants. (Suggestions) `ListPendingAsync` takes
    the cancellation token last and its truncation is documented on the
    interface; `docs/document-numbering.md` no longer calls 0026 "additive".
  - Commits: `fix(api)` fail-open audit, scoped sink, delivery logging + tests
    (~390); `refactor(api)` token order, interface docs, constants, doc fixes
    (~55).
  - RED/GREEN: the new tests did not compile (no logger constructors, no
    registration extension) before the code; after it the focused classes
    passed 41 of 41.
  - Checks: `dotnet build Commerce.sln` 0 errors; full `dotnet test tests/Commerce.Integration` 1662 passed, 0 failed, 0 skipped; `npm test` 348 passed (comment-only web change); iconv UTF-8 clean.

- Final audit review (lineage review-a81c593e458087c1, approved and acknowledged;
  next boundary bafd604) follow-ups, 2026-10-01, throwaway worktree `wt-audit`:
  - R1-001 (warning): the shared `IAuditSink` was in-memory again. Now ONE durable
    sink is the shared `IAuditSink` (`AddDurableAuditSink`); the keyed customer-only
    registration is gone. `AuditEntry` gained a required `ActorKind`
    (`AuditActorKind`: OrgUser, Device, Customer) so every call site states who
    acted: `TenantAuthorizationService` and `UpdaterService` use OrgUser,
    `CustomerCatalogAccessService` uses Customer. `PostgresAuditSink` maps it to
    `AuditActorKinds`. Every entry carries an organization, so none is skipped; RLS
    insert policy is satisfied by the per-entry org scope. Fail-open with Error
    logging for all consumers. Scope note: the POS/branch host
    (`PosHostBuilder`) keeps `InMemoryAuditSink` on purpose (local node, no cloud
    `audit_log`; a branch outbox is a separate unit); `CustomerOrderingAccessService`
    (Enable/Revoke) is registered but not called by any cloud endpoint, and its sync
    path is durable through the blocking `Record`.
  - R2 (warning): `Record` no longer throws. Async-only was rejected because
    `TenantAuthorizationService.Authorize` and its branch, updater and POS callers
    are synchronous end to end and would cascade. Instead the cloud's only live
    staff path (catalog rename endpoint) is now async
    (`TenantAuthorizationService.AuthorizeAsync`, `CatalogManagementService.RenameProductAsync`,
    adapter and endpoint), and the sync `Record` is the same durable fail-open
    write, documented as blocking.
  - R2/R3 (warning): the cancellation-after-commit test uses a post-commit seam
    (`PostgresOrderStore` optional `afterCommit` hook), asserts the token was
    cancelled, the outcome is Accepted and the delivery state is persisted.
  - Suggestions: OCE filter now propagates only cancellation of the caller's own
    token (a timeout fails open); `CapturingLogger` moved to its own test file;
    Program comment sits on the registration; sink name was already generic.
  - Commits: `test(api)` seam (~52); `feat(api)` durable audit for every consumer
    (~367); this note.
  - RED/GREEN: the new tests did not compile (no `AuditActorKind`,
    `AddDurableAuditSink`, `RenameProductAsync`, hook parameter) before the code;
    after it the focused classes passed 87 of 87.
  - Checks: `dotnet build Commerce.sln` 0 errors; full `dotnet test tests/Commerce.Integration` 1666 passed, 0 failed, 0 skipped; iconv UTF-8 clean.

## Next step

Owner manual verification (after the fail-open audit follow-up): apply `0026_orders_guest_check.sql` to `commerce_dev` by hand as owner (0025 must already be applied), restart the Cloud API, submit a guest and a registered order from the web order screen and confirm "Pedido P01-W-n recibido" with the tooltip, restart the API and confirm the orders are still listed, then provoke a denied credential and check a `customer-ordering-access.denied` row in `audit_log` (owner role). Follow-up: `StaffOrderScreen` still shows the plain accepted message and a raw GUID form.

- 2026-10-01 review of the durable-audit slice (`bafd604..069690e`): lineage
  `review-766f2b8f5e6d4db7` approved and acknowledged; next boundary
  `069690e`. Advisories left as follow-ups: the synchronous `Record` bridge in
  `PostgresAuditSink` blocks a thread-pool thread for the write (accepted
  trade-off: making `Authorize` async cascades into branch/updater/POS
  callers; migrate remaining sync callers to `RecordAsync` over time); no test
  proves a non-caller `OperationCanceledException` (timeout) fails open; POS
  local audit stays in memory (needs a branch outbox to reach `audit_log`).
