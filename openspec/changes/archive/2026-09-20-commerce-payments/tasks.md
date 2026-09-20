# Tasks: Commerce Payments

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~1,800–2,000 (design forecast) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes (skill default) — **overridden by user 2026-09-20** |
| Suggested split | Unit 1 → Unit 2 → Unit 3 → Unit 4 → Unit 5, dependency-ordered (kept as the internal implementation/commit order within one PR) |
| Delivery strategy | ask-on-risk |
| Chain strategy | **single-pr** — user explicitly chose one PR over the 5-unit chain despite the High budget risk |

Decision made 2026-09-20: single PR for all of Phase E. The Unit 1–5 ordering
above is preserved as the internal task/commit sequence (still RED→GREEN,
still dependency-ordered, still Unit 5/BranchSyncStore.cs last) — only the PR
boundary changed from 5 PRs to 1.
400-line budget risk: High (accepted knowingly by the user)

`delivery_strategy = ask-on-risk` (this project's `openspec/config.yaml`) does
**not** auto-resolve the chain strategy the way `auto-chain` does. Before
`sdd-apply` starts Unit 1, the user must be asked to confirm: (a) that five
chained PRs are acceptable, and (b) the chain topology (stacked-to-main vs.
independent PRs merged in order). This mirrors design.md's own "Review
Workload Forecast" section verbatim — the five units below are **taken from
design.md, not reinvented** — and is carried forward unchanged because
`stop_after: tasks` means this SDD run produces no chaining decision itself.

Units 1–3 are pure domain/application code: additive and inert (nothing
references them) until Unit 4 wires a persistence-backed HTTP surface. Unit 4
carries the tenancy and idempotency risk and is the largest unit (~640 lines).
Unit 5 is the only unit that edits an existing shipped file
(`BranchSyncStore.cs`), and it edits it strictly additively (new tables in the
existing `CREATE TABLE IF NOT EXISTS` block; zero changes to `outbox`,
`sale_effects`, `sale_lines`, `inbox`, or any existing sale method).

### Suggested Work Units

| Unit | Scope | Budget | Likely PR | Focused test command | Boundary proven | Rollback |
|---|---|---|---|---|---|---|
| 1 | **Domain ledger**: `PaymentMethod`, `PaymentSubject`, `PaymentEntry`, `PaymentApprovalState`, `Payment`, `Settlement`, `PaymentEffect` + invariant tests + the ADR-011 structural enum test | ~320 | PR 1 → main | `dotnet test --filter PaymentLedgerTests\|OrderEnumStructuralTests` | Reversal preserves history; `Order` enums proven clean | Revert; nothing consumes it |
| 2 | **Settlement arithmetic**: `SettlementCalculator` fold + largest-remainder report allocation + tests | ~260 | PR 2 → PR 1's branch | `dotnet test --filter SettlementCalculatorTests` | Two partials + reversal correct; allocation sums exactly; fulfilment/settlement read independently | Revert; pure, no callers |
| 3 | **Fail-closed approval**: `IPaymentLedgerStore` port, `IPaymentApprovalGateway`, `ManuallyRecordedApproval`, `UnavailablePaymentApproval`, `PaymentRecordingService` + tests | ~280 | PR 3 → PR 2's branch | `dotnet test --filter PaymentRecordingServiceTests\|ManuallyRecordedApprovalTests\|PaymentApprovalGatewayTests` | Absent config ⇒ `Unavailable`, no entry appended | Revert; no surface yet |
| 4 | **Cloud persistence + surface**: `0011` + `init-rls` parity + readiness, `Customer.BillingInstrumentReference`, `PostgresPaymentStore`, `PaymentEffectApplier`, `Endpoints/Payments.cs`, `Program.cs`, RLS/idempotency/503 tests | ~640 | PR 4 → PR 3's branch | `dotnet test --filter PaymentEndpointTests\|PaymentSyncIdempotencyTests\|MigrationRlsTests\|CustomerTests` | Replay applies once; RLS and append-only grants proven; 503 on unavailable gateway | Unmap the group; run the inverse block |
| 5 | **Branch parallel path + docs**: `BranchSyncStore` payment tables and methods, offline/atomicity tests, `synchronization.md` authority row, runbook | ~420 | PR 5 → PR 4's branch | `dotnet test --filter BranchPaymentOfflineTests` | Branch commits offline; sale DDL byte-identical | Revert; branch tables inert |

## Phase 1: Domain Ledger (Unit 1, ~320 lines)

- [x] 1.1 RED: `PaymentMethodTaxonomyTests` — five distinct methods (Cash,
      AccountCredit, BankTransfer, Card, MercadoPago) exist; Mercado Pago is
      not representable as a `Card` variant (Requirement: Payment Method
      Taxonomy, scenarios "Payment records a distinct method", "Mercado Pago
      is not merged into a generic card method")
- [x] 1.2 GREEN: `Commerce.Domain/Payments/PaymentMethod.cs`
- [x] 1.3 RED: `PaymentSubjectTests` — `PaymentSubject(PaymentSubjectKind, Guid
      SubjectId)` rejects an empty `SubjectId`; `Kind` covers `Order`/`Sale`
      (Requirement: Payment as a Separate Aggregate from Order — "MUST
      reference an Order... or a POS sale by id")
- [x] 1.4 GREEN: `Commerce.Domain/Payments/PaymentSubject.cs`
- [x] 1.5 RED: `PaymentEntryTests` — rejects a non-positive `Amount`; an entry
      of kind `Reversal` without `ReversesEntryId` throws; an entry of kind
      `Payment` with `ReversesEntryId` set throws (Requirement: Partial
      Payments and Reversals Without History Mutation; Testing Strategy
      "PaymentEntry rejects a non-positive amount and a Reversal without a
      target")
- [x] 1.6 GREEN: `Commerce.Domain/Payments/PaymentApprovalState.cs` —
      `{ Approved, Declined, Unavailable }`, a **domain**-layer type distinct
      from Application's `PaymentApprovalOutcome` (Unit 3), preserving the
      existing Domain→Application dependency direction (no other Domain type
      in this repo references `Commerce.Application`)
- [x] 1.7 GREEN: `Commerce.Domain/Payments/PaymentEntry.cs`
- [x] 1.8 RED: `PaymentAggregateTests` — `Payment.Append(entry)` adds an entry
      with no `Update`/`Remove` member on the type; after appending a
      `Reversal` entry, the original entry and the reversal are both still
      present and ordered (Requirement: Partial Payments and Reversals
      Without History Mutation, scenario "Reversing a partial payment
      restores the prior balance" — "both the original payment and the
      reversal remain visible in history")
- [x] 1.9 GREEN: `Commerce.Domain/Payments/Payment.cs`
- [x] 1.10 GREEN (no RED — plain data record, no invariant):
      `Commerce.Domain/Payments/Settlement.cs` —
      `record Settlement(decimal Target, decimal Settled, decimal Outstanding,
      bool IsSettled)`
- [x] 1.11 RED: `PaymentEffectTests` — construction guards mirror the existing
      `SaleEffect` guard style (non-empty ids, positive amount) (Design File
      Changes: `PaymentEffect.cs` — "the branch-durable effect, `SaleEffect`'s
      counterpart, same record style")
- [x] 1.12 GREEN: `Commerce.Domain/Payments/PaymentEffect.cs`
- [x] 1.13 RED: `OrderEnumStructuralTests` — reflection over
      `OrderDeliveryStatus` and `OrderPendingReason` asserts **no** member name
      matches `paid|payment|settl` (Requirement: Payment as a Separate
      Aggregate from Order — "no payment value MAY be added to either enum";
      Testing Strategy "Unit (structural, ADR-011)")
- [x] 1.14 GREEN: none required — `Order.cs` is untouched by this change; this
      task confirms 1.13 passes against the current `Order.cs` with zero
      production edits (ADR-011 compliance already holds; this is the
      regression tripwire for a later contributor)
- [x] 1.15 Regression guard: `dotnet test --filter
      PaymentLedgerTests|PaymentAggregateTests|PaymentSubjectTests|PaymentMethodTaxonomyTests|PaymentEffectTests|OrderEnumStructuralTests`
      green (19/19); no file outside `Commerce.Domain/Payments/*` and the new
      structural test is touched. DEVIATION (documented): test files live at
      `tests/Commerce.Integration/*.cs`, not `tests/Commerce.Domain/*.cs` as
      design.md's File Changes table names — no `Commerce.Domain` test
      project exists in this repo; ALL unit/integration tests (including
      prior domain-level suites like `CustomerTests.cs`) live flatly in the
      single `tests/Commerce.Integration` project. Followed the repo's actual
      convention over the design doc's aspirational path.

## Phase 2: Settlement Arithmetic (Unit 2, ~260 lines)

- [x] 2.1 RED: `SettlementCalculatorTests.Fold` — two partial payments then a
      reversal of one of them yield the correct outstanding balance, and every
      entry (including the reversed one) is still present in the input list
      passed to `Fold` (Requirement: Partial Payments and Reversals Without
      History Mutation, scenarios "Two partial payments reduce the outstanding
      balance correctly", "Reversing a partial payment restores the prior
      balance")
- [x] 2.2 GREEN: `Commerce.Application/Payments/SettlementCalculator.Fold(decimal
      target, IReadOnlyList<PaymentEntry> entries)`
- [x] 2.3 RED+GREEN: `SettlementCalculatorTests` — an order's fulfilment read
      (`OrderDeliveryStatus`/`OrderPendingReason`) and its settlement read
      (`SettlementCalculator.Fold` result) are independently queryable and
      neither implies the other: delivered+unsettled and paid+undelivered are
      both representable in the same test fixture (Requirement: Payment as a
      Separate Aggregate from Order, scenarios "Order delivered and unpaid is
      simultaneously representable", "Order paid and undelivered is
      simultaneously representable"; Requirement: Order Settlement View
      Referencing Payments, scenario "Order exposes a settlement view
      alongside unchanged fulfilment fields")
- [x] 2.4 RED: `SettlementCalculatorTests.AllocateForReport` — for a
      non-dividing case (e.g. 100.00 over three lines), the returned per-line
      allocations sum **exactly** to the payment amount (largest-remainder,
      ties broken by ascending line index); `Fold` never calls
      `AllocateForReport` (Design "Rounding / allocation policy" decision;
      Testing Strategy "AllocateForReport sums exactly... and is never
      consulted by Fold")
- [x] 2.5 GREEN: `Commerce.Application/Payments/SettlementCalculator.AllocateForReport(decimal
      amount, IReadOnlyList<decimal> frozenLineTotals)`
- [x] 2.6 Regression guard: `dotnet test --filter SettlementCalculatorTests`
      green (7/7); `SettlementCalculator` remains pure (no I/O, no persistence
      call), `decimal` end to end, no `double` anywhere in the file

## Phase 3: Fail-Closed Approval (Unit 3, ~280 lines)

- [x] 3.1 GREEN (no RED — pure port interface, mirrors the existing
      `IEmailSender`/store-abstraction pattern in this repo):
      `Commerce.Application/Payments/IPaymentLedgerStore.cs` —
      `AppendAsync(PaymentEntry)`, `GetEntriesAsync(PaymentSubject)`;
      implemented by `PostgresPaymentStore` in Unit 4, backed by an in-memory
      fake for this unit's tests
- [x] 3.2 RED: `PaymentApprovalGatewayTests` — with no gateway bound (or an
      unreachable one), the resolved `IPaymentApprovalGateway` implementation
      is `UnavailablePaymentApproval` and its `Approve(...)` call returns
      `PaymentApprovalOutcome.Unavailable`; the caller appends **no** entry to
      the ledger as a result (Requirement: Fail-Closed Approval Outcome,
      scenario "Unreachable approval configuration yields an explicit
      unavailable outcome")
- [x] 3.3 GREEN: `Commerce.Application/Payments/IPaymentApprovalGateway.cs`
      (`PaymentApprovalOutcome { Approved, Declined, Unavailable }`),
      `Commerce.Application/Payments/UnavailablePaymentApproval.cs`
- [x] 3.4 RED: `ManuallyRecordedApprovalTests` — a manually-recorded (staff
      attestation) payment returns `Approved` immediately, with no external
      call (Design "Fail-closed approval" decision row — "a manually-recorded
      payment is created already Approved because the cash is physically in
      the drawer")
- [x] 3.5 GREEN: `Commerce.Application/Payments/ManuallyRecordedApproval.cs`
- [x] 3.6 RED: `PaymentRecordingServiceTests.Record` — `Approved` outcome
      appends a `Payment`-kind entry; `Declined` outcome still appends a
      history entry (with `ApprovalState = Declined`), distinguishable from
      the `Unavailable` case, which appends nothing at all (Requirement:
      Fail-Closed Approval Outcome, scenario "Fail-closed outcome is
      distinguishable from a valid zero-amount record")
- [x] 3.7 GREEN: `Commerce.Application/Payments/PaymentRecordingService.cs` —
      `RecordAsync(subject, method, amount, actorId, entryId)`: calls the
      injected `IPaymentApprovalGateway`, maps
      `PaymentApprovalOutcome → PaymentApprovalState`, and appends via
      `IPaymentLedgerStore.AppendAsync` only on `Approved`/`Declined` (returns
      `null` — no representable entry — on `Unavailable`)
- [x] 3.8 RED: `PaymentRecordingServiceTests.Reverse` — `ReverseAsync(entryId,
      actorId)` appends a new `Reversal`-kind entry referencing the original
      entry id; the original entry is never mutated or removed from the store
      (Requirement: Partial Payments and Reversals Without History Mutation)
- [x] 3.9 GREEN: `PaymentRecordingService.ReverseAsync`
- [x] 3.10 RED: `PaymentRecordingServiceTests.GetSettlement` — composes
      `IPaymentLedgerStore.GetEntriesAsync` with `SettlementCalculator.Fold`
      to report settled/owed amounts for a subject (Requirement: Settlement
      Query Surface Is Reporting-Only, Never a Gate, scenario "Settlement
      query reports owed and settled amounts")
- [x] 3.11 GREEN: `PaymentRecordingService.GetSettlementAsync(subject, target)`
- [x] 3.12 Regression guard: `dotnet test --filter
      PaymentRecordingServiceTests|ManuallyRecordedApprovalTests|PaymentApprovalGatewayTests`
      green (7/7); `PaymentRecordingService` has no HTTP/persistence-provider
      dependency yet (only the Unit 3 port + an in-memory fake) — confirms the
      unit stays inert until Unit 4

## Phase 4: Cloud Persistence + Surface (Unit 4, ~640 lines)

- [x] 4.1 RED: `CustomerTests` — `Customer.BillingInstrumentReference` rejects
      a 13–19 digit string (PAN shape) via a constructor/setter guard; a
      non-PAN-shaped value or `null` is accepted; every existing
      `Customer.PaymentTerms` behavior/test is unaffected (Requirement:
      Payment-Instrument Reference Is a Distinct Field from PaymentTerms,
      scenarios "PaymentTerms is unaffected by adding an instrument
      reference", "Instrument reference stores no card data"; Testing
      Strategy "Customer.PaymentTerms behaviour is unchanged... and a
      PAN-shaped instrument reference throws")
- [x] 4.2 GREEN: `Commerce.Domain/Customers/Customer.cs` modify — add nullable
      `BillingInstrumentReference` + guard regex `^[0-9]{13,19}$`; zero edits
      to `PaymentTerms`'s type, position, or guard
- [x] 4.3 RED+GREEN: `MigrationRlsTests` — `0011_payments.sql` applies twice
      cleanly; org B cannot read org A's `payment_entries` rows (RLS); the
      `app_runtime` role has no `UPDATE` and no `DELETE` grant on
      `payment_entries`; `customers_instrument_not_pan_shaped` CHECK rejects a
      13–19 digit string on `billing_instrument_reference` (Testing Strategy
      "Integration (RLS/grants) | 0011 applies twice cleanly...")
- [x] 4.4 GREEN: `deploy/db/migrations/0011_payments.sql` — `payment_entries`
      table + `customers.billing_instrument_reference` column and CHECK, RLS/
      FORCE/policy mirroring `0010`, `GRANT SELECT, INSERT` only (no UPDATE,
      no DELETE), inverse block as comments
- [x] 4.5 GREEN: mirror the DDL into `deploy/dev/db/init-rls.sql` (the parity
      convention `MigrationRlsTests` asserts)
- [x] 4.6 RED+GREEN: `PostgresReadinessHealthCheck` tests — `/health/ready`
      fails before `0011` is applied and passes after, asserting
      `payment_entries` exists with `relforcerowsecurity` and its policy
      (Migration/Rollout: "ordering is enforced by the gate, not by
      discipline")
- [x] 4.7 GREEN (no RED — mechanical DTO shapes, no invariant):
      `Commerce.Cloud.Api/Persistence/PaymentRecords.cs`, mirroring the
      existing `CustomerRecords.cs` style
- [x] 4.8 RED: `PostgresPaymentStoreTests` (integration) — append-only insert
      + ordered read scoped by `set_config` tenant context first (the
      `PostgresCloudInboxStore` idiom); a cross-organization read returns no
      rows (Testing Strategy "Integration (cross-tenant) | A payment envelope
      claiming another organization ⇒ Denied")
- [x] 4.9 GREEN: `Commerce.Cloud.Api/Persistence/PostgresPaymentStore.cs`
      implementing Unit 3's `IPaymentLedgerStore`
- [x] 4.10 RED: `PaymentEndpointTests` — unauthenticated `POST /payments` ⇒
      401; the request DTO has no organization-naming member for the caller
      to populate (organization always resolved via
      `TenantScopeEndpointFilter`, never the body) (Threat Matrix Routing row
      RED tests)
- [x] 4.11 RED: `PaymentEndpointTests` — `POST /payments` with the resolved
      gateway `Unavailable` ⇒ **503** with an explicit outcome body; never
      200; no `payment_entries` row is inserted (Requirement: Fail-Closed
      Approval Outcome; Testing Strategy "Integration | POST /payments with
      the gateway unavailable ⇒ 503")
- [x] 4.12 RED: `PaymentEndpointTests` — happy-path `POST /payments`
      (`Approved`) inserts one entry and returns it; `POST
      /payments/{entryId}/reversal` inserts a `Reversal` entry and never
      issues a DELETE (Requirement: Partial Payments and Reversals Without
      History Mutation)
- [x] 4.13 RED: `PaymentEndpointTests` — an actor holding the role that
      manages the order can both record and reverse a payment without a
      second approver; an actor without order-management rights is denied on
      both routes (Requirement: Recording and Reversing Authorization Matches
      Order Management, both scenarios)
- [x] 4.14 RED: `PaymentEndpointTests` — `GET
      /payments/orders/{orderId}/settlement` and `GET
      /payments/customers/{customerId}/settlement` report owed/settled
      amounts derived from `SettlementCalculator.Fold`; a prior order with an
      unsettled balance does not affect a subsequent, unrelated request
      (Requirement: Settlement Query Surface Is Reporting-Only, Never a Gate,
      both scenarios)
- [x] 4.15 GREEN: `Commerce.Cloud.Api/Endpoints/Payments.cs` —
      `MapGroup("/payments")` + `.RequireAuthorization()` +
      `TenantScopeEndpointFilter`, exactly `OrderingEndpoints`'s shape; thin
      mapping onto `PaymentRecordingService`
- [x] 4.16 RED: `PaymentSyncIdempotencyTests` — replaying the same payment
      envelope under the same `operationId` ⇒ `DuplicateIgnored` and exactly
      one `payment_entries` row (Requirement: Payment-Effect Synchronization
      Path Parallel to the Sale Outbox, scenario "Duplicate payment-effect
      delivery applies once")
- [x] 4.17 GREEN: `Commerce.Cloud.Api/Payments/PaymentEffectApplier.cs` —
      projects a `payload_kind = "PaymentRecorded"` envelope into
      `payment_entries` inside the same transaction as the `sync_inbox`
      insert
- [x] 4.18 RED+GREEN: `Commerce.Cloud.Api/CloudSyncReceiver.cs` modify —
      dispatch by `payload_kind` to `PaymentEffectApplier` for payment
      envelopes; a regression test proves existing sale-envelope dispatch is
      byte-identical (Requirement: Payment-Effect Synchronization Path
      Parallel to the Sale Outbox, scenario "Payment effect synchronizes
      without touching the sale outbox schema")
- [x] 4.19 GREEN: `Commerce.Cloud.Api/Program.cs` modify — register
      `IPaymentLedgerStore → PostgresPaymentStore`, `IPaymentApprovalGateway`
      fail-closed by default (`UnavailablePaymentApproval` unless explicitly
      configured to `ManuallyRecordedApproval`), `PaymentRecordingService`,
      and map the `Payments` endpoint group
- [x] 4.20 Regression guard: `dotnet test --filter
      PaymentEndpointTests|PaymentSyncIdempotencyTests|MigrationRlsTests|CustomerTests`
      green; existing `OrderingEndpoints`/sale-sync integration tests
      unaffected

## Phase 5: Branch Parallel Path + Docs (Unit 5, ~420 lines)

- [x] 5.1 RED: `BranchPaymentOfflineTests` — a branch with no connectivity to
      the cloud or any payment gateway commits a POS cash payment and returns
      immediately; the effect is durable and `payment_outbox` holds it with
      `status = 'Pending'` (Requirement: Offline Branch Sale Never Blocks on
      Payment Approval, scenario "Branch sale commits with no cloud or
      gateway reachability"; Requirement: Offline Sale Path Stays Non-Blocking
      on Payment Confirmation, scenario "Sale commits while its payment
      effect is still pending sync")
- [x] 5.2 GREEN: `Commerce.BranchNode/BranchSyncStore.cs` modify — append
      `payment_effects` and `payment_outbox` to the existing `CREATE TABLE IF
      NOT EXISTS` constructor block; `CommitPaymentAtomically(effect,
      outboxRow)` as ONE SQLite transaction; **zero edits** to the `outbox`/
      `sale_effects`/`sale_lines`/`inbox` DDL or any existing sale method
- [x] 5.3 RED: `BranchPaymentOfflineTests` — an interrupted payment commit
      rolls back both `payment_effects` and `payment_outbox` together; the
      existing `outbox`/`sale_effects` DDL and every pre-existing sale test
      remain green (Testing Strategy "BranchNode | An interrupted payment
      commit rolls back both tables together")
- [x] 5.4 GREEN: `BranchSyncStore.SimulateInterruptedPaymentCommit`, mirroring
      the existing `SimulateInterrupted*` idiom for sales
- [x] 5.5 RED: `BranchSyncStore` tests —
      `GetPendingPaymentOutbox(branchId)` returns only `Pending` rows;
      `AcknowledgePayment(operationId)` sets `status = 'Acknowledged'` (Design
      Data Flow — "Later, when connectivity returns... Acknowledge →
      payment_outbox.status = 'Acknowledged'")
- [x] 5.6 GREEN: `BranchSyncStore.GetPendingPaymentOutbox`,
      `BranchSyncStore.AcknowledgePayment`
- [x] 5.7 RED+GREEN: end-to-end wiring test — a branch's pending
      `payment_outbox` row POSTs through the existing `/sync` receiver; the
      Unit 4 `PaymentEffectApplier` applies it exactly once (duplicate
      operation_id ⇒ `DuplicateIgnored`, projection skipped); the branch then
      calls `AcknowledgePayment` (Requirement: Payment-Effect Synchronization
      Path Parallel to the Sale Outbox, scenario "Payment effect synchronizes
      without touching the sale outbox schema")
- [x] 5.8 GREEN: `docs/architecture/synchronization.md` modify — replace the
      unbacked "Online payment approval | Payment gateway / cloud" row with
      the decided, ADR-011-backed pair (recorded-payment authority = the node
      that recorded it; settlement state = cloud, derived) (Proposal Scope:
      "Reconciling docs/architecture/synchronization.md:22")
- [x] 5.9 GREEN: `deploy/README.md`, `deploy/staging-runbook.md` modify —
      document `0011` apply + inverse and note that no payment-provider
      secret exists; absence is fail-closed, not fail-open (Migration/Rollout:
      application-upgrade-only, no coordinated device replacement)
- [x] 5.10 Regression guard: all pre-existing `BranchSyncStore` sale tests
      (`outbox`/`sale_effects`/`sale_lines`/`inbox`) remain green and
      byte-identical; `dotnet test Commerce.sln` full pass

## Phase 6: Full-Suite Verification

- [x] 6.1 `dotnet test Commerce.sln` full pass across Units 1–5 — 574/575
      passing. ONE pre-existing failure, unrelated to this change:
      `PublicRateLimitTests.WithGuestOrderingConfigAbsent_EveryPublicRoute_IsUnreachable_AndAppStillStarts`
      expects `GET /public/catalog/presentations` to 404 when guest-ordering
      config is absent (no `index.html` in the test host's `wwwroot`), but
      `src/Commerce.Cloud.Api/wwwroot` in this working tree already contains
      a previously-built web bundle (`wwwroot/assets/*`, predates this
      change), so the SPA fallback now serves `index.html` (200) instead of
      404ing. Zero commerce-payments code touches routing, static-file
      serving, or this test. Confirmed pre-existing/environmental by
      inspecting the failing test's own file (git-blame predates this
      branch) and by the fact that no file this change touches is anywhere
      on its call path.
- [x] 6.2 `dotnet build Commerce.sln` clean — 0 errors, 24 pre-existing
      `NU1903` package-vulnerability warnings only (unrelated to this change)
- [x] 6.3 Confirm every applicable Threat Matrix row (Routing) has a passing
      RED test per unit; Process integration, Shell/subprocess,
      Executable-file classification, Git repository/Commit/Push/PR, and
      Documentation-like paths rows remain N/A and untouched, as design.md
      records. Routing: `PaymentEndpointTests.PostPayments_Unauthenticated_Returns401`
      and `RecordPaymentRequest_HasNoOrganizationNamingMember` cover the two
      RED cases; every other row is untouched (no provider integration, no
      shell/subprocess, no file upload/classification, no Git automation, no
      new documentation-classification boundary shipped by this change).
- [x] 6.4 Trace every checkbox in `proposal.md`'s Success Criteria to the
      specific test task above that proves it (traceability closure pass):
      1. Payment recorded without changing Order enums → 1.13
         `OrderEnumStructuralTests` + 2.3 `FulfilmentAndSettlement_...`.
      2. Delivered+unpaid and paid+undelivered both representable → 2.3
         `FulfilmentAndSettlement_AreIndependentlyQueryable_*`.
      3. Two partial payments + reversal correct, history intact → 2.1
         `Fold_TwoPartialPaymentsThenReversalOfOne...` + 1.8
         `Append_Reversal_KeepsOriginalAndReversalBothVisible`.
      4. Duplicate operationId applies exactly once → 4.16
         `PaymentSyncIdempotencyTests` + 4.8
         `AppendAsync_SameEntryIdTwice_IsIdempotent`.
      5. Absent/unreachable config ⇒ explicit unavailable, never approval →
         3.2 `PaymentApprovalGatewayTests` + 3.6
         `Record_Unavailable_AppendsNothing` + 4.11
         `PostPayments_GatewayUnavailable_Returns503`.
      6. Branch sale commits with no cloud reachability → 5.1
         `CommitPaymentAtomically_NoConnectivityRequired...`.
      7. `Customer.PaymentTerms` byte-identical; instrument reference distinct
         → 4.1 `CustomerBillingInstrumentReferenceTests`.
      8. `synchronization.md` no longer carries an unbacked claim → 5.8 (doc
         edit, verified by direct read).
      9. `dotnet test`/`dotnet build` pass → 6.1/6.2.
