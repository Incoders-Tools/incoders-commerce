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
- [ ] T1 Persistence: migration `0025_orders.sql`, `IOrderStore`,
  `PostgresOrderStore` (submit idempotent per org, find, list pending),
  `P{branch}-W-{seq}` allocation at insert, DI swap, readiness, fixture
  chains; guest branch without a `branches` row → typed denial.
  Route: delegated direct.
- [ ] T2 Guest verification consumed in the order transaction (no burned
  verification on failure). Route: delegated direct (with T1 or after).
- [ ] T3 Number on the wire and in the web: `OrderPayloadV1.OrderNumber`,
  outcome type fix, number + tooltip in `OrderScreen` (guest and registered),
  i18n es/en. Route: delegated direct.
- [ ] T4 Persist the access-denial audit (`InMemoryAuditSink` →
  `audit_log`). Route: delegated direct.
- [ ] T5 Docs/specs: `docs/document-numbering.md` (web orders now live),
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

## Next step
T1.
