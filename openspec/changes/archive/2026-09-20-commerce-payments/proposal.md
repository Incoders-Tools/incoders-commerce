# Proposal: Commerce Payments

## Intent

**There is no payment state anywhere in this product.** Verified: `src/Commerce.Domain/Ordering/Order.cs` carries `OrderDeliveryStatus` and `OrderPendingReason` only — no `Paid` value, no payment entity, no provider integration in the repository. ADR-003 deferred settlement entirely; ADR-011 (accepted) set the target shape but explicitly implements nothing.

Today money exists only as an already-collected total: a POS sale records `SaleEffect.TotalAmount`, and a submitted order freezes resolved prices (Phase C) with no record of whether anyone ever paid. Nothing in the system can answer "is this order settled?", so an order delivered-and-unpaid is operationally invisible.

**Why now.** Phase D (guest ordering) explicitly declined a payment gate and deferred it here (`commerce-guest-ordering/proposal.md`, Decision 2). ADR-011 is the last accepted ADR with no implementing change. `docs/architecture/synchronization.md:22` already asserts an authority split ("Online payment approval | Payment gateway / cloud") that no ADR backs — a stale placeholder that this phase must either substantiate or remove.

This change is **planning only** (`openspec/config.yaml` `approval_scope: planning-only`): it models the payment lifecycle and locks the boundaries; it selects no provider.

## Scope

### In Scope

- **Payment as a first-class aggregate** referencing `Order` (and a POS sale), never embedded in it. Partial payments, multiple attempts, refunds and reversals must be representable without mutating order history (ADR-011).
- **Payment lifecycle states** distinct from fulfilment. `Order`'s delivery status and pending reasons stay **verbatim unchanged**; adding a `Paid` value to any order enum contradicts ADR-011 and is forbidden.
- **Settlement query surface**: a server-side answer to "what is owed / what is settled" per order and per customer, given partial payments.
- **Payment method reference on `Customer`** — new surface, explicitly **not** an extension of `Customer.PaymentTerms`, which is free-text commercial terms confirmed as informational and deferred (`commerce-pricing-engine` Decision (e)). A tokenized/PCI-scoped instrument reference is a separate, new field with its own rules.
- **Offline-payment reconciliation under ADR-002** — how a cash/offline POS payment recorded at a branch reconciles with cloud payment state.
- **Sync envelope decision for payment effects** (see Decision 1) — a payment-affecting effect must be idempotent under a stable `operationId` with inbox de-duplication (ADR-011).
- **Reconciling `docs/architecture/synchronization.md:22`** so the payment-authority row reflects a decided ADR, not a placeholder.

### Out of Scope (non-goals)

- **Provider selection and integration.** No gateway SDK, no API keys, no webhook endpoint ships. ADR-011 requires a further ADR before an integration is built.
- **PCI certification work, card data storage, or a hosted-fields implementation.** The proposal defines the *boundary* (the platform stores a reference, never a PAN); it does not build it.
- **Fiscal/invoicing, AFIP, taxes on the line.** Out per ADR-003 and ADR-011, unchanged.
- **Payment-gated guest checkout.** Phase D resolved guest admission via DNI + verified contact; this change does not retrofit a payment gate onto it.
- **Multi-currency / FX**, chargeback and dispute workflows, payment-provider reconciliation reports, dunning/retry automation.
- **Any change to how price is computed.** Phase C's engine is untouched; payment consumes resolved totals.

## Decisions

### Locked here

1. **Payment effects do NOT extend the sale-shaped `outbox` table.** Verified: `BranchSyncStore.cs:77-92` hardcodes `sale_id` and `total_amount` on `outbox` — it is a sale envelope, not a generic one. Widening it with nullable payment columns makes every future effect type a schema migration. **Locked: a parallel payment-effect path that reuses only the `operation_id` idempotency + `inbox` de-duplication pattern**, leaving `outbox` semantics untouched. Rejected: (a) nullable-column widening (couples unrelated effect types); (b) a generic JSON-envelope rewrite of `outbox` (correct end state, but a Phase F sync-ownership change, not this one).
2. **Payment approval fails CLOSED.** The repo's only external-provider precedent — `RESEND_API_KEY` for email (`deploy/staging-runbook.md`) — fails **open** to a safe no-op. That is correct for a notification and wrong for money: an unreachable provider must never be treated as an approval. **Locked: absent/unreachable payment configuration yields an explicit unapproved/unavailable outcome, never a silent success and never a silent zero.**
3. **Offline branch sales never block on cloud or gateway payment confirmation** (ADR-002, hard constraint). A branch remains authoritative for its own sale; any payment-approval flow designed here must degrade to "recorded locally, settled later" rather than gate the sale path.
4. **`Customer.PaymentTerms` stays free text, unchanged.** Any billing-instrument reference is a new, separately-named field.

### Explicitly deferred to `sdd-design`

- Exact payment aggregate shape (state machine vs. append-only attempt log) and where partial-payment arithmetic lives.
- Storage shape of the parallel payment-effect path (new SQLite table vs. reusing `inbox` only).
- Whether a POS cash payment and a future online payment share one aggregate or two.
- Rounding/allocation policy when a partial payment does not divide evenly across order lines.

### Product questions — answered by the user 2026-09-20

- (a) **Payment situations to model**: cash, account/credit (`Customer.PaymentTerms`), bank transfer, card/online gateway, and Mercado Pago specifically. Mercado Pago is named as a concrete first-class provider candidate for the eventual gateway integration (still out of scope for this planning-only change, per ADR-011 — but the payment method taxonomy in `sdd-design` MUST include it as a distinct method, not lumped into a generic "card/gateway" bucket).
- (b) **Unpaid delivered order is reporting-only.** It does not block new orders, dispatch, or any other operational path. This removes "block subsequent orders while unpaid" from scope entirely — the settlement query surface is read/reporting-oriented, not a gate.
- (c) **Recording and reversing a payment uses the same role that manages the order** (seller/business-admin per existing role taxonomy) — no second-approver requirement for reversals. This simplifies the authorization model: no new dual-control workflow to design.
- (d) **No existing manual settlement process to replace or coexist with.** This is the first formal settlement tracking mechanism — no legacy spreadsheet/notebook migration or parallel-run concern.

## Capabilities

### New Capabilities

- `order-payment-lifecycle`: payment as a lifecycle separate from order; attempts, partial payments, refunds/reversals; settlement state per order; fail-closed approval semantics.

### Modified Capabilities

- `branch-offline-sync`: payment-affecting effects synchronize idempotently under a stable `operationId` with inbox de-duplication, via a payment path parallel to the sale-shaped `outbox`; the offline sale path stays non-blocking (ADR-002).
- `customer-registry`: a new, separately-named payment-instrument reference field; `PaymentTerms` unchanged.
- `private-customer-ordering`: an order gains a settlement view referencing payments; `OrderDeliveryStatus`/`OrderPendingReason` unchanged.

## Approach

Model the lifecycle, not the integration. The payment aggregate lands in the domain referencing an order/sale by id, with the provider boundary expressed as an abstraction that has exactly one implementation in this phase: a manually-recorded payment (the situation that already exists in the business). That keeps the aggregate honest — partial payments and reversals are exercised by a real path — while leaving provider selection to its own ADR.

Sync reuses the proven idempotency primitive (`operation_id` + `inbox`) without inheriting the sale envelope's columns, so Phase F can later generalize `outbox` without unwinding payment-specific columns from it.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Commerce.Domain/Payments/*` | New | Payment aggregate, attempt/reversal semantics, settlement value objects |
| `src/Commerce.Domain/Ordering/Order.cs` | **Unchanged** | ADR-011: no payment values on the fulfilment enums |
| `src/Commerce.Domain/Customers/Customer.cs` | Modified | New instrument-reference field; `PaymentTerms` untouched |
| `src/Commerce.Application/Payments/*` | New | Record/settle/reverse; fail-closed approval outcome |
| `src/Commerce.BranchNode/BranchSyncStore.cs` | Modified | Parallel payment-effect path reusing `operation_id`/`inbox`; `outbox` schema unchanged |
| `src/Commerce.Cloud.Api/Endpoints/*` | New | Payment recording + settlement read surface |
| `deploy/db/migrations/00NN_*.sql` | New | Payment tables |
| `docs/architecture/synchronization.md` | Modified | Reconcile the stale "Online payment approval" authority row |
| `docs/architecture/decisions/ADR-011-*.md` | Referenced | Implemented, not amended |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Payment state silently merged into order status by a later contributor | Medium | ADR-011 violation; a negative test asserts no payment value exists on the order enums |
| Fail-open creeping in by analogy with the email provider | Medium | Decision 2 locked; explicit unavailable outcome is a success criterion with a test |
| Offline sale path accidentally gated on payment confirmation | Medium | ADR-002 hard constraint; a test proves a branch sale commits with no cloud reachability |
| PCI scope expanding once a provider is discussed | Medium | Provider integration is an explicit non-goal requiring its own ADR; platform stores a reference only |
| Partial-payment arithmetic drift vs. frozen line totals | Medium | One server-side allocation/rounding policy defined in design; no client arithmetic |
| `outbox` parallel path becomes a second sync idiom to maintain | Medium | Accepted knowingly; scoped to reuse `operation_id`/`inbox` only, with Phase F named as the consolidator |
| Aggregate overbuilt beyond the answered product scope | Low | Product questions (a)–(d) answered 2026-09-20; design/specs stay within that scope, no further invention |

## Rollback Plan

Revert the commit: the payment domain, application services, endpoints and the parallel sync path disappear; `Order`, `Customer.PaymentTerms` and the `outbox` schema were never modified, so nothing in the existing sale or order paths needs unwinding. The migration ships inverse statements dropping the payment tables and the customer instrument-reference column. **Caveat**: once real payments are recorded, settlement history cannot be re-expressed in the prior model (there is none) — prefer forward-fix after first real use. Narrower rollback: disable the payment endpoints while keeping the domain model dormant.

## Dependencies

- ADR-011 (payment lifecycle separate from order), ADR-003 (pending order never implies settlement), ADR-002 (offline sale authority), ADR-008 (Customer) — accepted, not re-litigated.
- Phase C `commerce-pricing-engine` (shipped) — resolved order totals are the amount a payment settles.
- Phase D `commerce-guest-ordering` (shipped) — guest admission is non-payment-gated and stays that way.
- Product questions (a)–(d) answered 2026-09-20 (see Decisions); specs may proceed without inventing business rules.

## Success Criteria

- [ ] A payment can be recorded against an order without changing `OrderDeliveryStatus` or `OrderPendingReason`; both enums remain free of payment values (structural test).
- [ ] An order can be simultaneously delivered and unpaid, and paid and undelivered, and both are queryable.
- [ ] Two partial payments against one order produce a correct outstanding balance; a reversal restores the prior balance without deleting history.
- [ ] Replaying the same payment effect under the same `operationId` applies exactly once (inbox de-duplication), proven by test.
- [ ] Absent or unreachable payment configuration yields an explicit unapproved/unavailable outcome — never an approval, never a silent zero (fail-closed test).
- [ ] A branch sale commits with no cloud reachability and no gateway confirmation (ADR-002 constraint test).
- [ ] `Customer.PaymentTerms` is byte-identical after this change; the instrument reference is a distinct field storing no card data.
- [ ] `docs/architecture/synchronization.md` no longer carries an unbacked payment-authority claim.
- [ ] `dotnet test Commerce.sln` and `dotnet build Commerce.sln` pass.

## Proposal question round

Not yet answered — this proposal was produced without an interactive round. Questions (a)–(d) under "Unresolved product questions" are the round; the locked decisions 1–4 are engineering-grounded and correctable, and the deferrals are design-phase items. Specs MUST NOT proceed on (a)–(d) by assumption.
