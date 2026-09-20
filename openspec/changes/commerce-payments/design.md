# Design: Commerce Payments

## Technical Approach

The proposal's four locked decisions are not re-opened. This design resolves its
four deferrals and records the **verified codebase facts** that force each
answer.

**Verified 1 — `outbox` is a sale envelope, not a generic one.**
`BranchSyncStore.cs:77-92` declares `sale_id TEXT NOT NULL` and
`total_amount TEXT NOT NULL`, and `InsertOutboxRow` (`:850-877`) always writes
both from a `SaleEffect`. A payment effect has no `sale_id` when it settles a
web order, so it cannot use this table without nullable widening — which
Decision 1 forbids. Confirmed: **the design below adds tables and changes no
column of `outbox`, `sale_effects`, `sale_lines` or `inbox`.**

**Verified 2 — the cloud inbox is already generic.**
`PostgresCloudInboxStore.TryApplyInboundAsync` (`:58-105`) inserts only
`SyncEnvelope` columns plus `payload jsonb` / `payload_kind`, keyed on
`operation_id`, inside one tenant-scoped transaction; `CloudInboxStore` is the
in-memory RLS-equivalent double with the same semantics. Payment effects
therefore need **no new cloud inbox table and no change to either store** — they
are a new `payload_kind` on the path that already exists.

**Verified 3 — the money rules are already written once.**
`Money.Round2` (2 decimals, `AwayFromZero`) is the repo's single rounding
policy, and `OrderLineSnapshot.LineTotal` is frozen server-side by
`OrderSnapshotFactory` at submission (Phase C). A payment therefore settles an
**already-frozen amount**; this change computes no price and re-rounds no line.

**Verified 4 — the fail-open precedent is email-shaped.** `LogOnlyEmailSender`
is selected when `RESEND_API_KEY` is absent. That substitution pattern is
deliberately **not** reproduced here (Decision 2).

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Aggregate shape** (deferral 1) | **Append-only ledger with a per-entry state machine.** `Payment` is the aggregate; `PaymentEntry` rows are immutable and never updated or deleted. A reversal is a **new entry** of kind `Reversal` referencing the reversed entry id — prior balance is restored by appending, so history is intact (proposal success criterion 3). The only state machine is *inside one entry*: `Requested → Approved \| Declined \| Unavailable`, terminal on write; a manually-recorded payment is created already `Approved` because the cash is physically in the drawer. **Nothing stores a balance.** | **An order-level payment state machine** — a single mutable `PaymentStatus` cannot express two partial payments plus a reversal without destroying the intermediate truth, and it is one refactor away from the enum ADR-011 forbids. **A pure event stream with no aggregate root** — no other aggregate in this repo is event-sourced; it would be a second persistence idiom for one feature. |
| **Where the arithmetic lives** (deferral 1) | **One pure fold, `Commerce.Application/Payments/SettlementCalculator`**, mirroring `PricingResolutionService`'s "one server-side policy" shape. `Settlement = fold(frozen target, ordered entries)`; the target is `Σ OrderLineSnapshot.LineTotal` for an order or `SaleEffect.TotalAmount` for a POS sale. **Derived on read, never persisted, never computed by a client** — the same discipline `SubmitOrderLine` enforces for price (no client-writable member exists). | **A stored `outstanding_balance` column** — a denormalization that drifts the first time an entry is inserted outside the service, and the proposal names exactly that drift as a Medium risk. **Arithmetic in the endpoint** — duplicates the rule at every call site. |
| **One aggregate for POS cash and future online** (deferral 3) | **One aggregate.** The method is a value, not a type: `PaymentMethod { Cash, AccountCredit, BankTransfer, Card, MercadoPago }` — **Mercado Pago is a first-class member, not a `Card` sub-case** (answered question (a)). The subject is a value too: `PaymentSubject(PaymentSubjectKind { Order, Sale }, Guid SubjectId)`. A single order settled part-cash and part-Mercado-Pago is one ledger with two entries; a second aggregate would make that split unrepresentable and would duplicate `SettlementCalculator`. Provider-specific data stays in an optional `ProviderReference` (opaque string) on the entry — adding a provider is a new enum member and a new gateway implementation, not a schema reshape. | **Separate `CashPayment` / `OnlinePayment` aggregates** — two settlement queries, two reversal rules, and no way to answer "what is owed" without unioning them. **A generic `Card` bucket with a provider discriminator inside** — the user explicitly asked for Mercado Pago to be named in the taxonomy. |
| **Storage shape of the parallel effect path** (deferral 2) | **Two new branch tables + the existing `inbox` + the existing cloud `sync_inbox`, all unchanged.** `payment_effects` (the durable local effect, `SaleEffect`'s counterpart) and `payment_outbox` carrying **only the generic `SyncEnvelope` columns** (`operation_id` PK, branch/org/aggregate/actor/correlation/occurred/payload_kind/payload/status/acknowledged_at) with **no payment-specific column at all** — the payment detail lives in `payload`. Both are added to the same `CREATE TABLE IF NOT EXISTS` block in the `BranchSyncStore` constructor; the `outbox` DDL is byte-identical to today. Inbound de-duplication reuses `inbox` verbatim (no per-effect-type inbox). Cloud side is the existing `sync_inbox` + `PostgresCloudInboxStore` with a new `payload_kind`. **`payment_outbox` is therefore already the generic envelope Phase F will consolidate `outbox` into** — the consolidation is a rename plus a backfill, not an unwind. | **Reusing `inbox` only, with no local payment outbox** — `inbox` is inbound de-dup; it has no `status`/`acknowledged_at`, so an offline branch would have nowhere to queue an unsent payment, breaking ADR-002. **Widening `outbox`** — Decision 1, locked. **A second cloud inbox table** — `sync_inbox` is already generic; a parallel table would fork the idempotency guarantee. |
| **Rounding / allocation policy** (deferral 4) | **Settlement is order-scoped; there is no per-line allocation in any balance.** A payment settles the order's outstanding amount, so the uneven-division problem **does not arise in the money path at all** — the only rounding is `Money.Round2` on the entry amount at capture, and the outstanding amount is a subtraction of already-rounded decimals. For the reporting surface only, a **derived, never-stored** per-line allocation uses **largest-remainder (Hamilton) over the frozen `LineTotal`s**, ties broken by ascending line index, so `Σ allocations == payment amount` exactly by construction. `decimal` end to end; `double` appears nowhere. | **Allocating every payment to lines and summing lines back into the balance** — makes the balance depend on a rounding convention, and a residual cent then becomes a permanent "unpaid" order. **Pro-rata with independent per-line rounding** — provably fails to sum to the payment amount. |
| **Fail-closed approval** (Decision 2) | **`IPaymentApprovalGateway` returning `PaymentApprovalOutcome { Approved, Declined, Unavailable }`** with exactly one implementation this phase: `ManuallyRecordedApproval` (staff attestation of money already received). **Absent or unreachable configuration binds `UnavailablePaymentApproval`, which returns `Unavailable` — never `Approved`, never a zero amount.** The endpoint maps `Unavailable` to **503 with an explicit body**, and **no ledger entry is appended**. This is deliberately the inverse of the `LogOnlyEmailSender` substitution. | **A no-op gateway mirroring `LogOnlyEmailSender`** — the proposal names this as the exact failure mode to avoid: a silent approval of money. **Throwing** — an exception is a 500 that reads as a bug, not as a decidable business outcome a caller can retry. |
| **Customer instrument reference** (Decision 4) | **A new nullable `Customer.BillingInstrumentReference` (opaque provider token/alias)** with a constructor guard rejecting any value that is a 13–19 digit string (a PAN shape), mirroring the existing `tax_id` invariant style that is also mirrored by a DB `CHECK`. `PaymentTerms` is **not touched** — not its type, not its position, not its guard. | **Extending `PaymentTerms`** — Decision 4, locked; it is informational free text. **An unvalidated free-text field** — the cheapest structural guard against a PAN ever landing in the column. |
| **Endpoint shape and authorization** | **`Endpoints/Payments.cs`**, one `MapGroup("/payments")` with `.RequireAuthorization()` + `TenantScopeEndpointFilter`, exactly `OrderingEndpoints`' shape: thin mapping onto an application service, organization always from `CloudTenantScope`, never a request field. **The same permission that manages the order records or reverses a payment — no second approver** (answered question (c)). Settlement reads are **reporting-only and gate nothing**: no order, dispatch or submission path reads a settlement value (answered question (b)). | **A dedicated approver permission / dual control** — explicitly not asked for. **A `blockUnpaid` check on submission** — (b) removes it from scope entirely. |

## Data Flow

```text
Web/managed order payment (cloud-authoritative)
  POST /payments {subjectKind:Order, subjectId, method, amount, operationId}
    -> scope from TenantScopeEndpointFilter          <- never a body field
    -> IPaymentApprovalGateway.Approve(...)
         Unavailable -> 503 explicit outcome, NO entry appended   [Decision 2]
         Declined    -> 200 with a Declined entry (history, not a balance change)
         Approved    -> append PaymentEntry(kind=Payment, Money.Round2(amount))
    -> ONE tx: INSERT payment_entries (+ sync_inbox row for the operation_id)
  POST /payments/{entryId}/reversal
    -> append PaymentEntry(kind=Reversal, reverses=entryId)   <- never a DELETE
  GET /orders/{orderId}/settlement   |   GET /customers/{id}/settlement
    -> SettlementCalculator.Fold(target = Sigma frozen LineTotal, entries)
         { Target, Settled, Outstanding, IsSettled, Entries[] }  <- derived only
         optional ?allocate=true -> largest-remainder per-line view, NOT stored

POS cash payment at a branch (ADR-002: never blocks)
  Cashier records payment
    -> ONE SQLite tx: payment_effects + payment_outbox   <- COMMITTED LOCALLY
    -> returns immediately; no cloud call, no gateway call on this path
  Later, when connectivity returns (outside the sale path)
    -> GetPendingPaymentOutbox(branchId) -> POST /sync   [existing receiver]
    -> PostgresCloudInboxStore.TryApplyInboundAsync      [UNCHANGED code]
         duplicate operation_id -> DuplicateIgnored, projection skipped
         new                    -> sync_inbox row + payment_entries projection
                                   in the SAME transaction
    -> Acknowledge -> payment_outbox.status = 'Acknowledged'

Untouched by construction
  outbox / sale_effects / sale_lines / inbox DDL .... byte-identical
  Order.Status / Order.PendingReason ............... byte-identical
  Customer.PaymentTerms ............................ byte-identical
  The sale commit path calls no payment code ....... ADR-002 preserved
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `src/Commerce.Domain/Payments/PaymentMethod.cs` | Create | `Cash \| AccountCredit \| BankTransfer \| Card \| MercadoPago`. |
| `src/Commerce.Domain/Payments/PaymentSubject.cs` | Create | `record PaymentSubject(PaymentSubjectKind Kind, Guid SubjectId)` + non-empty guard. |
| `src/Commerce.Domain/Payments/PaymentEntry.cs` | Create | Immutable ledger entry: id, subject, `PaymentEntryKind { Payment, Reversal }`, method, `decimal Amount`, `PaymentApprovalState`, `ReversesEntryId?`, `ProviderReference?`, actor, `RecordedAtUtc`. Guards: positive amount; a `Reversal` requires `ReversesEntryId`. |
| `src/Commerce.Domain/Payments/Payment.cs` | Create | Aggregate root over one subject: ordered `IReadOnlyList<PaymentEntry>`; `Append(entry)` only — no mutate, no remove. |
| `src/Commerce.Domain/Payments/Settlement.cs` | Create | `record Settlement(decimal Target, decimal Settled, decimal Outstanding, bool IsSettled)`. |
| `src/Commerce.Domain/Payments/PaymentEffect.cs` | Create | The branch-durable effect, `SaleEffect`'s counterpart (same record style). |
| `src/Commerce.Domain/Customers/Customer.cs` | **Modify** | Add `BillingInstrumentReference` + PAN-shape guard. `PaymentTerms` untouched. |
| `src/Commerce.Domain/Ordering/Order.cs` | **Unchanged** | ADR-011. A structural test asserts this. |
| `src/Commerce.Application/Payments/SettlementCalculator.cs` | Create | The one fold + the largest-remainder report allocation. |
| `src/Commerce.Application/Payments/IPaymentApprovalGateway.cs` | Create | `Approve(...) -> PaymentApprovalOutcome`. |
| `src/Commerce.Application/Payments/ManuallyRecordedApproval.cs` | Create | The only implementation this phase. |
| `src/Commerce.Application/Payments/UnavailablePaymentApproval.cs` | Create | Fail-closed binding when configuration is absent/unreachable. |
| `src/Commerce.Application/Payments/PaymentRecordingService.cs` | Create | Record / reverse / query settlement; maps gateway outcome to an outcome record. |
| `src/Commerce.Cloud.Api/Persistence/PaymentRecords.cs` | Create | DTO/record shapes, `CustomerRecords.cs` style. |
| `src/Commerce.Cloud.Api/Persistence/PostgresPaymentStore.cs` | Create | Append-only insert + ordered read, `set_config` tenant scope first (the `PostgresCloudInboxStore` idiom). |
| `src/Commerce.Cloud.Api/Payments/PaymentEffectApplier.cs` | Create | Projects a `payload_kind = "PaymentRecorded"` envelope into `payment_entries` in the same transaction as the `sync_inbox` insert. |
| `src/Commerce.Cloud.Api/Endpoints/Payments.cs` | Create | `POST /payments`, `POST /payments/{entryId}/reversal`, `GET /payments/orders/{orderId}/settlement`, `GET /payments/customers/{customerId}/settlement`. |
| `src/Commerce.Cloud.Api/CloudSyncReceiver.cs` | Modify | Dispatch by `payload_kind`; existing sale behaviour unchanged. |
| `src/Commerce.Cloud.Api/Program.cs` | Modify | Register store, service, gateway (fail-closed default), map the group. |
| `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` | Modify | Assert `payment_entries` + `relforcerowsecurity` + policy. |
| `src/Commerce.BranchNode/BranchSyncStore.cs` | **Modify** | `payment_effects` + `payment_outbox` in the constructor DDL block; `CommitPaymentAtomically`, `GetPendingPaymentOutbox`, `AcknowledgePayment`, `SimulateInterruptedPaymentCommit`. **No edit to the `outbox`/`sale_*`/`inbox` DDL or to any sale method.** |
| `deploy/db/migrations/0011_payments.sql` | Create | `payment_entries` + `customers.billing_instrument_reference`; RLS/FORCE/policy mirroring `0010`; `GRANT SELECT, INSERT` only — **no UPDATE, no DELETE** (append-only enforced by grant). Inverse block as comments. |
| `deploy/dev/db/init-rls.sql` | Modify | Same DDL appended (the parity convention `MigrationRlsTests` asserts). |
| `deploy/README.md`, `deploy/staging-runbook.md` | Modify | `0011` apply + inverse; note that **no payment provider secret exists** and absence is fail-closed, not fail-open. |
| `docs/architecture/synchronization.md` | Modify | Replace the unbacked "Online payment approval \| Payment gateway / cloud" row with a decided, ADR-011-backed pair: recorded payment authority = the node that recorded it (branch for POS cash, cloud for web orders); settlement state = cloud, derived. |
| `tests/Commerce.Domain/PaymentLedgerTests.cs`, `SettlementCalculatorTests.cs`, `OrderEnumStructuralTests.cs` | Create | Ledger invariants, fold, allocation, and the ADR-011 negative test. |
| `tests/Commerce.Integration/PaymentSyncIdempotencyTests.cs`, `PaymentEndpointTests.cs`, `MigrationRlsTests.cs` | Create/Modify | Replay, fail-closed 503, RLS, append-only grants. |
| `tests/Commerce.BranchNode/BranchPaymentOfflineTests.cs` | Create | ADR-002 constraint test. |

## Interfaces / Contracts

```csharp
// Append-only. There is no setter, no Remove, and no status field to flip.
public sealed record PaymentEntry(
    Guid EntryId, Guid OrganizationId, PaymentSubject Subject,
    PaymentEntryKind Kind, PaymentMethod Method, decimal Amount,
    PaymentApprovalState ApprovalState, Guid? ReversesEntryId,
    string? ProviderReference, Guid ActorId, DateTimeOffset RecordedAtUtc);

// The ONE place partial-payment arithmetic lives. Pure; no I/O; no persistence.
public static class SettlementCalculator
{
    public static Settlement Fold(decimal target, IReadOnlyList<PaymentEntry> entries);
    // Report-only. Sum(result) == amount exactly (largest remainder, ties by index).
    public static IReadOnlyList<decimal> AllocateForReport(
        decimal amount, IReadOnlyList<decimal> frozenLineTotals);
}

// Fail-closed: Unavailable is a first-class outcome, never coerced to Approved.
public enum PaymentApprovalOutcome { Approved, Declined, Unavailable }
```

```sql
-- 0011_payments.sql — RLS/policy/grants mirror 0010 exactly.
-- Append-only is a GRANT property, not a convention: app_runtime gets no UPDATE/DELETE.
CREATE TABLE IF NOT EXISTS payment_entries (
    entry_id            uuid PRIMARY KEY,
    organization_id     uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    operation_id        uuid NOT NULL UNIQUE,     -- the idempotency key (ADR-003)
    subject_kind        text NOT NULL CHECK (subject_kind IN ('Order','Sale')),
    subject_id          uuid NOT NULL,
    entry_kind          text NOT NULL CHECK (entry_kind IN ('Payment','Reversal')),
    method              text NOT NULL CHECK (method IN
                          ('Cash','AccountCredit','BankTransfer','Card','MercadoPago')),
    amount              numeric(12,2) NOT NULL CHECK (amount > 0),
    approval_state      text NOT NULL CHECK (approval_state IN
                          ('Approved','Declined','Unavailable')),
    reverses_entry_id   uuid NULL REFERENCES payment_entries (entry_id),
    provider_reference  text NULL,                -- opaque token/alias; never a PAN
    actor_id            uuid NOT NULL,
    recorded_at_utc     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payment_entries_reversal_requires_target
        CHECK ((entry_kind = 'Reversal') = (reverses_entry_id IS NOT NULL))
);
ALTER TABLE customers ADD COLUMN IF NOT EXISTS billing_instrument_reference text NULL;
ALTER TABLE customers ADD CONSTRAINT customers_instrument_not_pan_shaped
    CHECK (billing_instrument_reference IS NULL
           OR billing_instrument_reference !~ '^[0-9]{13,19}$');
```

```sql
-- BranchSyncStore constructor DDL, APPENDED to the existing block.
-- payment_outbox carries ONLY generic envelope columns — no sale_id, no
-- total_amount, no payment_id: this is the shape Phase F consolidates to.
CREATE TABLE IF NOT EXISTS payment_effects (
    entry_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, subject_kind TEXT NOT NULL,
    subject_id TEXT NOT NULL, method TEXT NOT NULL, amount TEXT NOT NULL,
    entry_kind TEXT NOT NULL, reverses_entry_id TEXT NULL, occurred_at_utc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS payment_outbox (
    operation_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, organization_id TEXT NOT NULL,
    aggregate_id TEXT NOT NULL, aggregate_version INTEGER NOT NULL, actor_id TEXT NOT NULL,
    correlation_id TEXT NOT NULL, occurred_at_utc TEXT NOT NULL, payload_kind TEXT NOT NULL,
    payload TEXT NOT NULL, status TEXT NOT NULL, acknowledged_at_utc TEXT NULL);
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | Two partial payments then a reversal yield the correct outstanding balance, and **every entry still exists** after the reversal | xUnit over `SettlementCalculator` |
| Unit | An order is simultaneously `DestinationConfirmed` and unsettled, and `PendingDestination` and fully settled | xUnit |
| Unit (structural, ADR-011) | `OrderDeliveryStatus` and `OrderPendingReason` contain **no** member whose name matches `paid\|payment\|settl` — reflection over the enum members, so a later contributor's addition fails the build | xUnit + reflection |
| Unit | `AllocateForReport` sums **exactly** to the payment amount for non-dividing cases (e.g. 100.00 over three lines), and is never consulted by `Fold` | xUnit, property-style cases |
| Unit | `PaymentEntry` rejects a non-positive amount and a `Reversal` without a target | xUnit |
| Unit | `Customer.PaymentTerms` behaviour is unchanged and a PAN-shaped instrument reference throws | xUnit |
| Unit | Missing configuration binds `UnavailablePaymentApproval` ⇒ `Unavailable`, and **no entry is appended** | xUnit |
| Integration | Replaying the same payment envelope under the same `operationId` ⇒ `DuplicateIgnored` and **exactly one** `payment_entries` row | `WebApplicationFactory` + live Postgres |
| Integration | `POST /payments` with the gateway unavailable ⇒ **503** with an explicit outcome body, never 200, never a zero-amount entry | same |
| Integration (RLS/grants) | `0011` applies twice cleanly; org B cannot read org A's entries; `app_runtime` has **no UPDATE and no DELETE** on `payment_entries`; `/health/ready` fails before and passes after | `MigrationRlsTests` |
| Integration (cross-tenant) | A payment envelope claiming another organization ⇒ `Denied` (the existing store behaviour, re-asserted for the new `payload_kind`) | same |
| BranchNode | A branch commits a POS cash payment with **no cloud reachability and no gateway** and returns; `payment_outbox` holds it as `Pending` (ADR-002) | xUnit + SQLite temp file |
| BranchNode | An interrupted payment commit rolls back both tables together; the existing `outbox`/`sale_effects` DDL and every sale test remain green | `SimulateInterrupted*` idiom |

## Threat Matrix

| Native row | Applicability |
|---|---|
| **Routing** | **Applicable** — a new authenticated endpoint group `/payments`. Safe behavior: `.RequireAuthorization()` + `TenantScopeEndpointFilter`, organization always from the resolved scope and never from the body (`OrderingEndpoints` shape); no anonymous route and no `/public/*` route is added; the settlement reads gate nothing. RED tests: an unauthenticated request ⇒ 401; a request whose body names another organization cannot affect the scope (no such member exists on the DTO); a cross-org `subjectId` resolves to no rows under RLS. |
| **Process integration** | **N/A — no provider integration ships** (explicit non-goal). `IPaymentApprovalGateway` has no network implementation in this phase; the fail-closed default is covered by the Routing row's tests, not by a process boundary. |
| Shell / subprocess | N/A — none introduced. |
| Executable-file classification | N/A — no upload, download or classification boundary. |
| Git repository selection / Commit / Push / PR commands | N/A — no product code runs Git or PR automation. |
| Documentation-like paths | N/A — no file-classification boundary. |

## Migration / Rollout

Forward-only, following `0010`'s convention. **Before** deploying the new image,
apply `0011_payments.sql` through the direct (non-pooled) connection;
`/health/ready` fails closed until `payment_entries` exists with
`FORCE ROW LEVEL SECURITY` and its policy, so ordering is enforced by the gate,
not by discipline.

**No data migration exists to perform** — answered question (d) confirms this is
the first formal settlement mechanism, so there is no legacy record to import and
no parallel-run window to manage. `payment_entries` starts empty; every order
that predates this change simply reads as fully outstanding, which is the honest
statement (no payment was ever recorded).

**Application upgrade vs. device replacement are separate.** The branch change is
an **application upgrade only**: two new `CREATE TABLE IF NOT EXISTS` statements
run against the existing `branch.db` on first start of the upgraded POS
executable, with no coordinated notebook replacement, no re-pairing, and no file
migration — the same additive pattern `EnsureSaleKindColumnExists` documents for
an older `branch.db`. A notebook replacement remains the separately-governed
procedure in `deployment-profiles.md` and is neither required by nor affected by
this change.

**Rollback**: revert the commit and run `0011`'s inverse block; `Order`,
`Customer.PaymentTerms`, `outbox`, `sale_effects`, `sale_lines` and `inbox` were
never modified, so nothing in the sale or order paths unwinds. A downgraded POS
executable simply ignores the two extra SQLite tables. **Narrowest rollback**:
stop mapping `MapPaymentEndpoints()` — the domain stays dormant and the branch
tables stay inert. Lossy after first real use (settlement history has no prior
model to fall back to); prefer forward-fix then.

## Review Workload Forecast

Decision needed before apply: Yes
Chained PRs recommended: Yes
400-line budget risk: High

Estimated ~1 800–2 000 authored lines against the 400-line default budget
(`delivery_strategy = ask-on-risk`), so this is flagged for an explicit decision
before apply. Five dependency-ordered units:

| Unit | Scope | Budget | Boundary | Rollback |
|---|---|---|---|---|
| 1 | **Domain ledger**: `PaymentMethod`, `PaymentSubject`, `PaymentEntry`, `Payment`, `Settlement`, `PaymentEffect` + invariant tests + the ADR-011 structural enum test | ~320 | Reversal preserves history; `Order` enums proven clean | Revert; nothing consumes it |
| 2 | **Settlement arithmetic**: `SettlementCalculator` fold + largest-remainder report allocation + tests | ~260 | Two partials + reversal correct; allocation sums exactly | Revert; pure, no callers |
| 3 | **Fail-closed approval**: gateway interface, manual and unavailable implementations, `PaymentRecordingService` + tests | ~280 | Absent config ⇒ `Unavailable`, no entry | Revert; no surface yet |
| 4 | **Cloud persistence + surface**: `0011` + `init-rls` parity + readiness, `PostgresPaymentStore`, `PaymentEffectApplier`, `Endpoints/Payments.cs`, `Program.cs`, RLS/idempotency/503 tests | ~640 | Replay applies once; RLS and append-only grants proven | Unmap the group; run the inverse block |
| 5 | **Branch parallel path + docs**: `BranchSyncStore` payment tables and methods, offline/atomicity tests, `synchronization.md` authority row, runbook | ~420 | Branch commits offline; sale DDL byte-identical | Revert; branch tables inert |

Units 1–3 are additive and inert until Unit 4 wires a surface. Unit 4 holds the
tenancy and idempotency risk; Unit 5 is the only one that edits an existing
shipped file (`BranchSyncStore.cs`), and it edits it additively.

## Open Questions

- [ ] Non-blocking, for `sdd-apply`: whether an `AccountCredit` entry should
      carry a due date. `Customer.PaymentTerms` is free text (Decision 4) and
      cannot be parsed into one, so this phase records the method without a
      derived due date; an explicit due-date column is an additive follow-up if
      reporting later asks for ageing buckets.
- [ ] Non-blocking: cloud `Order` state is still in-memory (`CloudOrderStore`),
      while `payment_entries` is durable — a restart can leave a durable payment
      whose order is gone. This is the already-accepted Phase B persistence gap,
      not a new one; the settlement read returns the entries with an unresolved
      target rather than fabricating a zero.
- [ ] Non-blocking: `provider_reference` is opaque free text with only the
      PAN-shape guard. A real provider's reference format is unknowable before
      the provider ADR that this change explicitly defers.
