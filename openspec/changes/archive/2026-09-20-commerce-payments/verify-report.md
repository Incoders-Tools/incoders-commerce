# Verification Report: commerce-payments (Phase E)

**Mode**: Independent re-verification by direct source inspection and real test execution, run after Phase F and Phase G landed on top of this change.

## Task Completeness

All 67 tasks in `tasks.md` (Units 1-5 + Phase 6 verification) are marked `[x]`. Spot-checked against actual files: `src/Commerce.Domain/Payments/*.cs` (7 files), `src/Commerce.Application/Payments/*.cs` (6 files), `src/Commerce.Cloud.Api/Endpoints/Payments.cs`, `src/Commerce.Cloud.Api/Payments/PaymentEffectApplier.cs`, `deploy/db/migrations/0011_payments.sql` all exist and match design.md's decisions.

## Design Fidelity

- **Append-only ledger**: `Payment.cs`/`PaymentEntry.cs` — no `Update`/`Remove` member exists on `Payment`; reversal is a new `PaymentEntry` with `Kind = Reversal`. Confirmed by reading the file.
- **Order-scoped settlement**: `SettlementCalculator.Fold` sums entries against a target amount; no per-line rounding in the money path.
- **Fail-closed approval**: `IPaymentApprovalGateway` has `ManuallyRecordedApproval` and `UnavailablePaymentApproval` implementations; absent config resolves to `UnavailablePaymentApproval` in `Program.cs`.
- **ADR-011 (Order enums untouched)**: `grep` of `OrderDeliveryStatus.cs`/`OrderPendingReason.cs` for `paid|payment|settl` returns zero matches. `OrderEnumStructuralTests` (reflection-based) enforces this as a regression tripwire.
- **BranchSyncStore.cs additive-only**: confirmed via `git log`/`git diff` at commit `5558216` — `payment_effects`/`payment_outbox` tables added to the existing `CREATE TABLE IF NOT EXISTS` block; zero deletions to `outbox`/`sale_effects`/`sale_lines`/`inbox`.
- **Payment methods**: `PaymentMethod.cs` enumerates `Cash, AccountCredit, BankTransfer, Card, MercadoPago` — Mercado Pago is a distinct member, not nested under Card, per the answered product question.
- **Customer.BillingInstrumentReference**: new nullable field, `Customer.PaymentTerms` unchanged (confirmed via `git diff` — only additive).

## Test Execution Evidence (re-run independently)

| Command | Result |
|---|---|
| `dotnet build Commerce.sln` | PASS, 0 errors |
| `dotnet test Commerce.sln` | PASS, 628/628 (current `main`, includes Phase F + Phase G + the two post-merge CI bugfixes on top) |

No regression: the payments-specific test files (`PaymentAggregateTests`, `SettlementCalculatorTests`, `PaymentRecordingServiceTests`, `PaymentEndpointTests`, `BranchPaymentOfflineTests`, `CustomerBillingInstrumentReferenceTests`, `OrderEnumStructuralTests`, etc.) all pass in the current full run.

## Spec Compliance

All 4 spec deltas (`order-payment-lifecycle` new capability, `branch-offline-sync`/`customer-registry`/`private-customer-ordering` modified) map to real implementation and passing tests, per the original apply-phase report and confirmed here by re-inspection of the source files named above.

### Issues Found

**CRITICAL**: None.

**WARNING**: None new. Carried forward from apply: `ResetRequest`-style clock-based flakiness does not apply here (unrelated to this change); the previously-noted shared-dev-Postgres contamination risk is now resolved (main's full suite is 628/628 clean).

### Verdict

**PASS** — All 67 tasks complete, design decisions verified against real source, ADR-011 enforced by both structural inspection and a regression test, `BranchSyncStore.cs` additivity independently confirmed via git history, and the full test suite is clean on current `main` (628/628) after two further phases landed on top with no regression. Ready for archiving.
