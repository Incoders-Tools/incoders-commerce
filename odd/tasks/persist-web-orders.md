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

## Next step

Owner manual verification: apply `0026_orders_guest_check.sql` to `commerce_dev` by hand as owner (0025 must already be applied), restart the Cloud API, submit a guest and a registered order from the web order screen and confirm "Pedido P01-W-n recibido" with the tooltip, restart the API and confirm the orders are still listed, then provoke a denied credential and check a `customer-ordering-access.denied` row in `audit_log` (owner role). Follow-up: `StaffOrderScreen` still shows the plain accepted message and a raw GUID form.
