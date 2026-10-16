```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:independent-verify-2026-09-19
verdict: pass-with-warnings
blockers: 0
critical_findings: 0
requirements: 12/15
scenarios: 23/27
test_command: dotnet test Commerce.sln
test_exit_code: 0
test_output_hash: sha256:529-passed-0-failed-Bootstrap1-Upgrade19-Integration509
build_command: dotnet build Commerce.sln
build_exit_code: 0
build_output_hash: sha256:0-errors-24-NU1903-warnings
```

## Verification Report

**Change**: commerce-guest-ordering (Phase D: guest + registered customer ordering)
**Version**: N/A (OpenSpec change, unmerged branch feat/pricing-engine)
**Mode**: Standard (full artifact set: proposal, design, 5 spec deltas, tasks). Strict TDD Mode is active; apply-progress carries an explicit TDD Cycle Evidence table (Unit 6b) plus RED/GREEN annotations inline in tasks.md for every earlier unit.

### Completeness

| Metric | Value |
|--------|-------|
| Tasks total | 57 |
| Tasks complete | 56 |
| Tasks partial (documented gap) | 1 (task 6.7, E2E guest flow) |
| Tasks incomplete | 0 |
| Proposal success criteria | 10 total, independently re-derived below |

### Build and Tests Execution

Build: PASSED (independently reproduced)
```text
dotnet build Commerce.sln
Build succeeded.
0 Error(s)
24 Warning(s) -- all NU1903 (System.IO.Packaging 8.0.0 transitive advisory), pre-existing, unrelated to this change
```

Tests (.NET): PASSED (independently reproduced, live Postgres incoders-commerce-postgres-1 confirmed running)
```text
dotnet test Commerce.sln
Commerce.Bootstrap.Tests: Passed 1,  Failed 0
Commerce.Upgrade:         Passed 19, Failed 0
Commerce.Integration:     Passed 509, Failed 0
Total: 529/529 passed, 0 failed, 0 skipped. Duration ~2m29s.
```
This exactly matches the sessions self-reported 529/529 figure (apply-progress obs 2098), independently reproduced.

Tests (Web/Vitest): PASSED (independently reproduced)
```text
npm run test -- --run
Test Files  20 passed (20)
Tests       60 passed (60)
```
Matches the claimed 60/60 across 20 files exactly.

Build (Web): PASSED (independently reproduced)
```text
npm run build
tsc -b && vite build -> 0 errors, built in 282ms
```

Coverage: Not configured in this repo, skipped, consistent with prior changes.

### Spec Compliance Matrix (all 5 spec files, 15 requirements, 27 scenarios)

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Order Origin Classification | Guest order stamped Guest | OrderOriginTests.Constructor_ValidGuestOrder_Succeeds; GuestOrderingTests.SubmitGuestAsync_Admitted_CarriesGuestContactOnTheOrder_WithNoCustomerId | COMPLIANT |
| Order Origin Classification | Registered order stamped RegisteredCustomer | OrderOriginTests.Constructor_ValidRegisteredOrder_Succeeds; CloudOrderStore.cs line 54 single call site hardcodes OrderOrigin.RegisteredCustomer for every registered submission | COMPLIANT |
| Order Construction Invariant | Registered without CustomerId rejected | OrderOriginTests.Constructor_RegisteredCustomer_WithNoCustomerId_Throws / WithEmptyCustomerId_Throws | COMPLIANT |
| Order Construction Invariant | Guest with CustomerId rejected | OrderOriginTests.Constructor_Guest_WithNonNullCustomerId_Throws | COMPLIANT |
| Guest Identity Capture on the Order | Guest order carries identity fields | PublicRateLimitTests.FullGuestFlow_RequestConfirmSubmit_AdmitsAnOrder_WithGuestOriginAndNoCustomerId (asserts guestContact.contactAddress) | COMPLIANT |
| Guest Price Resolution | Guest receives list price | GuestOrderingTests.ADR010Divergence_RegisteredCustomerWithDiscount_PaysStrictlyLessThanGuest_ForTheSameItem | COMPLIANT |
| Guest Price Resolution | Registered strictly less than guest | same test, both prices asserted on the two submitted orders | COMPLIANT |
| Guest Order Non-Priority Ranking | Verified guest order dispatched normally | GuestOrderingTests / PublicRateLimitTests full-flow tests reach Accepted/OK through the identical AttemptDelivery path used by staff orders; no separate staff-acceptance gate exists in code | COMPLIANT |
| Guest Order Non-Priority Ranking | Registered orders rank above guest orders when prioritizing | OrderOriginTests.DispatchRank_RegisteredCustomer_IsZero / _Guest_IsOne (unit-level property only) | PARTIAL, see WARNING 3 below |
| Guest Order Non-Priority Ranking | Order listing distinguishes origin | none, no list endpoint exists | PARTIAL, see WARNING 3 below |
| Non-Staff Actor Representation | Guest order records a non-staff actor shape | CloudOrderSubmissionService.cs line 223 passes OrderActors.PublicGuest as SyncEnvelope.ActorId, verified by direct code read; OrderOriginTests.OrderActors_PublicGuest_IsNotEmpty / _IsStable | PARTIAL, see WARNING 4 below |
| Guest Verification Gate Before Admission | Verified guest order is admitted | PublicRateLimitTests.FullGuestFlow_RequestConfirmSubmit_AdmitsAnOrder_WithGuestOriginAndNoCustomerId | COMPLIANT |
| Guest Verification Gate Before Admission | Unverified guest submission is rejected | GuestOrderingTests.SubmitGuestAsync_WithNoPriorVerification_IsRejected_AndNoOrderIsStored | COMPLIANT |
| Guest Verification Gate Before Admission | Expired or incorrect verification code is rejected | GuestVerificationTests.ConfirmAsync_WrongCode_IncrementsAttemptCount, _ExpiredRow, _FifthWrongAttempt_BurnsTheRow; GuestOrderingTests.SubmitGuestAsync_WithExpiredVerification_IsRejected | COMPLIANT |
| Public Catalogue Read | Anonymous catalogue read | PublicRateLimitTests.AnonymousCatalogRead_ReturnsCatalogue_ScopedToGuestOrderTarget | COMPLIANT |
| Customer-Scoped Session Distinct From Staff Scheme (public-order-surface) | Customer session cannot reach a staff endpoint | CustomerSessionIsolationTests.CustomerCookie_ForciblyPresented_OnStaffEndpoint_Returns401 (Theory, multiple staff routes) | COMPLIANT |
| Customer-Scoped Session Distinct From Staff Scheme (public-order-surface) | Staff session cannot be substituted for the customer scheme | CustomerSessionIsolationTests.StaffCookie_ForciblyPresented_OnCustomerSignOut_Returns401; CustomerOrderSubmissionTests.PostCustomerOrders_StaffCookie_ForciblyPresented_CannotSubstitute_Returns401 | COMPLIANT |
| No Self-Registration Route | No self-registration endpoint exists | CustomerSessionIsolationTests.MappedRouteTable_ContainsNoRegisterShapedRoute; PublicRateLimitTests.WithGuestOrderingConfigured_MappedRouteTable_ContainsNoRegisterShapedRoute | COMPLIANT |
| Rate Limiting on Guest Submission | Abusive burst is throttled | PublicRateLimitTests.VerificationRequestBurst_BeyondFiveInWindow_Returns429_WithRetryAfter; GuestOrderSubmitBurst_BeyondTenInWindow_Returns429_AndNoAdditionalOrderIsAdmitted | COMPLIANT |
| Rate Limiting on Guest Submission | Rate limiting is isolated to the public group | PublicRateLimitTests.PublicGroupThrottled_StaffEndpoint_RemainsUnaffected_NoRateLimitPolicyApplied | COMPLIANT |
| Registered Order Origin Stamping (private-customer-ordering delta) | Credential-based order carries RegisteredCustomer origin | CloudOrderStore.cs line 54 single call site; OrderingTests / CustomerTests construct and assert OrderOrigin.RegisteredCustomer for this path | COMPLIANT |
| Registered Order Origin Stamping (private-customer-ordering delta) | Admin-provisioned login also stamps RegisteredCustomer origin | CustomerOrderSubmissionTests.PostCustomerOrders_ValidSession_SubmitsOrder_ReturnsAccepted (zero-line order, asserts Accepted status only) | PARTIAL, see WARNING 4 below |
| Customer-Scoped Session Distinct From Staff Scheme (user-credentials delta) | Registered customer sign-in issues a customer-scoped session | CustomerSessionIsolationTests.CustomerSignIn_RegisteredCustomer_Succeeds_IssuesCustomerScopedCookie | COMPLIANT |
| Customer-Scoped Session Distinct From Staff Scheme (user-credentials delta) | Customer-scoped session is rejected by the staff scheme | CustomerSessionIsolationTests.CustomerCookie_ForciblyPresented_OnStaffEndpoint_Returns401 | COMPLIANT |
| Admin-Provisioned Customer Login Only | No public self-registration endpoint exists | Same route-table assertion tests as above, plus CustomerSignIn_WithStaffAccount_Returns401_SameAsBadPassword | COMPLIANT |
| Anonymous Request Organization Resolution (tenant-access-foundation delta) | Anonymous guest request resolves to the fixed default org and branch | GuestOrderTargetTests (TryFromConfiguration success/failure cases); PublicScopeEndpointFilterTests; PublicRateLimitTests.WithGuestOrderingConfigAbsent_EveryPublicRoute_IsUnreachable_AndAppStillStarts / _NoPublicRouteIsMapped | COMPLIANT |
| Anonymous Request Organization Resolution (tenant-access-foundation delta) | Guest order carries organization and branch context despite no principal | PublicRateLimitTests.FullGuestFlow test (order constructed under CloudTenantScope derived from GuestOrderTarget, not a claim) | COMPLIANT |

Compliance summary: 23 of 27 scenarios COMPLIANT with a runtime-executed covering test; 4 of 27 PARTIAL, all with some form of passing evidence (unit-level or code-inspection-backed); 0 of 27 FAILING or fully UNTESTED. Per report-format rules, PARTIAL scenarios are a warning-level flag, not CRITICAL: none of these four represent a regression or a currently-observable violation of a MUST clause.

### Threat Matrix RED Test Confirmation (design.md two Applicable rows)

| Row | Applicability | RED tests confirmed passing | Result |
|---|---|---|---|
| Routing | Applicable | PublicRateLimitTests.WithGuestOrderingConfigAbsent_EveryPublicRoute_IsUnreachable_AndAppStillStarts, _NoPublicRouteIsMapped, WithGuestOrderingConfigured_MappedRouteTable_ContainsNoRegisterShapedRoute; CustomerSessionIsolationTests full negative-auth matrix | REAL, executed as part of the 509/509 Integration pass |
| Process integration | Applicable | PublicRateLimitTests.VerificationRequest_WithFailingEmailSender_StillReturns202_AndIssuesTheRow; GuestVerificationTests.RequestAsync_FailingEmailSender_StillReturnsVerificationId_AndStillIssuesRow | REAL, both executed and passing |

### Correctness (Static Evidence, independently read from source)

| Requirement | Status | Notes |
|---|---|---|
| Paired Order construction invariant | Implemented | Both inconsistent states (RegisteredCustomer without CustomerId, Guest with CustomerId, Guest without GuestContact) throw in the constructor, confirmed by direct read of Order.cs and by OrderOriginTests |
| Guest submission never resolves a discount | Implemented | CloudOrderSubmissionService.SubmitGuestAsync calls the shared ResolveLinesAsync(scope, lines, discountPercentage: null, ct), confirmed by source read |
| GuestOrderTarget config-gated mapping | Implemented | Program.cs lines 115-116 and 309-313: MapPublicOrderingEndpoints only called when TryFromConfiguration succeeds |
| guest_order_verifications RLS deviation reasoning | Verified, holds | USING(true) WITH CHECK(true) on UPDATE is intentional and bounded: SELECT/UPDATE are keyed by an unguessable random id GUID never enumerable cross-org, mirroring password_reset_tokens_lookup own unscoped-SELECT precedent; attempt_count <= 5 is a hard DB CHECK, not merely application logic; the 6-digit code is never stored in plaintext (SHA-256 hash only); the app layer still sets app.current_org_id before every scoped call even though RLS does not enforce it on UPDATE, so a future RLS tightening is additive, not a rewrite. This is a wider trust boundary than password_reset_tokens_consume single terminal WITH CHECK(consumed_at IS NOT NULL), but the header comments stated reason (three distinct pre-terminal UPDATE shapes reachable before any org scope is known) is accurate and the abuse surface is genuinely bounded by the CHECK constraint and hash lookup, not by RLS alone. Confirmed not a genuine RLS gap, consistent with the already-accepted device_credentials unscoped-revoke precedent this migration cites. |
| Rate limiter policy isolation | Implemented | Program.cs AddRateLimiter policies attached only to the public group; PublicGroupThrottled_StaffEndpoint_RemainsUnaffected_NoRateLimitPolicyApplied proves it at runtime |
| CustomerCookie scheme isolation | Implemented | Distinct Cookie.Name and Cookie.Path, Customer policy naming only that scheme, confirmed in Program.cs and proven by the full negative-auth Theory test |

### Coherence (Design)

| Decision | Followed | Notes |
|---|---|---|
| Guest verification channel is Email via IEmailSender | Yes | GuestVerificationService composes through the existing IEmailSender; no new package or secret added |
| Verification state shape (persisted, hashed, attempt-bounded, ticket) | Yes | 0010_guest_ordering.sql matches design Interfaces/Contracts SQL |
| GuestOrderTarget as the ONE org/branch resolution point | Partially, see Issue 1 below | Reused (not literally specified by design text) for /customer/orders branch resolution too, creating the coupling the user flagged |
| Domain shape for origin (OrderOrigin, Guid? CustomerId, GuestContact?) | Yes | Matches Order.cs exactly |
| Non-priority equals ranking via DispatchRank, never a gate | Yes as far as built | Property exists and is correct; no consumer wires it into a list yet, see PARTIAL scenarios above |
| Non-staff actor is OrderActors.PublicGuest sentinel | Yes | Confirmed at the one call site that needs it |
| Rate limiting: 4 named fixed-window policies, public group only | Yes | Matches design sizing (5/15m, 10/15m, 10/hour, 60/min) |
| One screen, guest and registered as peers, guest listed first | Yes, per passing Vitest suite | OrderScreen.test.tsx; StaffOrderScreen.tsx extraction for the pre-existing staff-only form |

### Issues Found

CRITICAL: None.

WARNING:

1. /customer/orders coupling with GuestOrdering__* config (user-flagged Issue 1): adjudicated as an acceptable, documented, non-blocking coupling, not a blocking defect. Verified in code: Program.cs line 304 passes guestOrderTargetConfigured into MapCustomerSessionEndpoints, which gates only the /customer/orders route (not /customer/sign-in, /sign-out, /me) behind the same config that gates the entire public guest surface. This does contradict the general principle (stated in this changes own framing of ADR-009) that order-origin channels should not structurally depend on one another. However: (a) there is currently exactly one organization and branch, and GuestOrderTarget is deliberately the only org/branch resolution mechanism this host has for a session-less/branch-less request, there is no persisted branch registry to fall back to, so the coupling is a consequence of a real, pre-existing infrastructure gap, not a new design flaw; (b) no operational scenario in this deployment disables guest ordering while keeping registered ordering enabled, both are gated by the same intent-to-launch decision today; (c) the gap is transparently self-documented in apply-progress (Deviation 1) rather than hidden, with a clear remediation path (split branch resolution from the guest-specific config name, for example a DefaultOrderTarget or per-org branch registry). Verdict: acceptable for now, not a blocker for this change; recommend filing a small, explicit follow-up to decouple /customer/orders branch resolution from GuestOrdering__* before or alongside the next change that introduces a second organization or a real branch registry, at that point the coupling stops being theoretical.
2. Task 6.7 (E2E guest flow) is PARTIAL (user-flagged Issue 2): adjudicated as an acceptable residual limitation, not a blocker. The full Playwright request-confirm-submit E2E cannot read the issued verification code back out of LogOnlyEmailSender because no development-only HTTP seam exists for it, unlike the precedent /internal/test-seed/user endpoint. Verified: everything up to and after the code-read step is covered, the exact same logical flow (request, confirm, submit) is proven server-side against real HTTP endpoints and live Postgres by PublicRateLimitTests.FullGuestFlow_RequestConfirmSubmit_AdmitsAnOrder_WithGuestOriginAndNoCustomerId, which is a stronger evidentiary bar than a browser E2E for this specific gap (it asserts on the actual persisted Order origin, customerId, and guestContact, not just a UI state). The repos own precedent (commerce-pricing-engine verify-report, POS scan UI scenario) already accepted a PARTIAL and manual-substitute classification for a comparable cannot-fully-automate-this-layer-yet gap without blocking that changes PASS verdict. Verdict: acceptable residual limitation. Recommend a small additive follow-up (a dev-only /internal/test-seed/guest-verification-code seam, mirroring the existing precedent) rather than blocking this change on it.
3. Guest Order Non-Priority Ranking, 2 of 3 scenarios lack a runtime/HTTP-level covering test (Registered orders rank above guest orders when prioritizing, Order listing distinguishes origin) because no pending-work list or priority-ordered-view endpoint exists anywhere in the shipped ordering surface (confirmed by direct read of Endpoints/Ordering.cs: only POST / and GET /{orderId} exist). DispatchRank itself is correctly implemented and unit-tested (0 for registered, 1 for guest). This is a pre-existing, repo-wide absence of any order-listing endpoint, not introduced by this change, and is self-documented as a deferred deviation in tasks.md task 1.7 and as open Gap 3 in apply-progress. Not a regression (nothing today violates the no-equal-weight-query rule because no such query exists), but also not proven by test evidence beyond the unit level. Recommend tracking as a fast follow-up alongside the already-accepted CloudOrderStore persistence gap, since a list endpoint is likely to land together with real order persistence.
4. Non-Staff Actor Representation and Admin-provisioned-login-also-stamps-RegisteredCustomer-origin scenarios are proven by code inspection plus a single shared call site, not by an explicit runtime read-back assertion in their respective HTTP tests. Order and OrderSubmissionOutcome expose no ActorId member on any read path (documented in the guest full-flow tests own comment), and CustomerOrderSubmissionTests happy-path test asserts Accepted status but not order.origin explicitly (unlike the guest equivalent, which does assert order.origin). Both are structurally guaranteed correct (single call sites in CloudOrderStore.cs and CloudOrderSubmissionService.cs, confirmed by direct read), so this is a test-thoroughness gap, not a functional one. Low-cost fix: add one assertion line to each existing test.

SUGGESTION:

1. guest_order_verifications UPDATE policy (USING(true) WITH CHECK(true)) is wider than any other RLS policy in this repos migrations reviewed so far; it is well-reasoned and bounded today, but if a future change adds a new UPDATE-capable column to this table, that column would inherit the same unscoped policy by default. Worth a one-line reminder comment (or a follow-up ADR note) that any future column addition to this table should re-examine whether the CHECK-constraint-based bounding still holds.
2. PostgresGuestVerificationStore RecordAttemptAsync, ConfirmAsync, and ConsumeAsync all call SetTenantScopeAsync before an UPDATE that the RLS layer does not actually scope, the set_config call is currently inert for these three methods (it matters only for future-proofing if the UPDATE policy is ever tightened to check organization_id). Not a bug, but worth a one-line code comment so a future reader does not assume the set_config call is load-bearing for these three specific methods today.

### Verdict
PASS WITH WARNINGS

Both builds are clean (0 errors) and both test suites were independently reproduced rather than trusted from the prior sessions self-reports, matching exactly: 529/529 for the .NET suite (Bootstrap 1, Upgrade 19, Integration 509) and 60/60 for the web suite across 20/20 files. All 57 tasks are accounted for (56 complete, 1 transparently documented PARTIAL). 23 of 27 spec scenarios across all 5 capability spec files have runtime-executed covering tests; the remaining 4 are PARTIAL (not UNTESTED or FAILING) with either unit-level or code-inspection-backed evidence, none representing a currently-observable regression. Both threat-matrix rows flagged Applicable by design.md (Routing, Process integration) have real, executed RED tests, not merely claimed ones. The guest_order_verifications_update RLS deviation is verified sound: bounded by a hard DB CHECK (attempt_count <= 5) and an unguessable GUID lookup, not by RLS scoping alone, consistent with the repos own device_credentials unscoped-revoke precedent. Both user-flagged issues were independently adjudicated as non-blocking: the /customer/orders coupling with GuestOrdering__* config is a real but currently-inert design compromise (single org/branch, no scenario disables one channel while keeping the other) with a documented remediation path; the Task 6.7 E2E gap is fully substituted by a stronger server-side integration test and matches this repos own precedent for accepting a PARTIAL classification on a hard-to-automate layer. Recommend proceeding to archive, carrying forward three tracked follow-ups: (1) decouple /customer/orders branch resolution from the guest-specific config name, (2) add the dev-only verification-code seam for a true browser E2E, (3) wire a pending-order list or priority-view endpoint that finally exercises DispatchRank end-to-end (naturally paired with the already-accepted CloudOrderStore persistence follow-up).

### Addendum: Follow-Up Closure (Phase 8)

The user asked to close all three tracked WARNING follow-ups in the same round rather than deferring them. All three are now implemented, per `tasks.md`'s Phase 8 section:

| Follow-up | Status | Evidence |
|---|---|---|
| 1. `/customer/orders` coupled to `GuestOrdering__*` config | CLOSED | `MapCustomerSessionEndpoints` no longer takes a config-gate parameter; `/customer/orders` is always mapped. `GuestOrderTarget` is now resolved defensively at request time and a missing target degrades to `503`, never a route-level 404/405. `CustomerOrderSubmissionWithoutGuestConfigTests` (2 tests) proves the route stays reachable with guest config absent. |
| 2. Task 6.7 E2E gap | CLOSED (written, not executed live) | `GET /internal/test-seed/guest-verification-code` (Development-only, mirrors `/internal/test-seed/user`) added and unit/integration-tested (`TestSeedGuestVerificationCodeTests`, 2 tests). `e2e/ordering.spec.ts` now has a full request→confirm→submit Playwright test using that seam, with an explicit `test.skip` guard if the seam is unavailable (non-Development target). This test was written and code-reviewed this session but **not executed against a live server** — running it requires the local HTTPS proxy + `dotnet run` Cloud.Api + Postgres per the web app's E2E setup, which was out of scope to boot in this session. |
| 3. `DispatchRank` never exercised end-to-end | CLOSED | `GET /orders/pending` added to the existing staff `/orders` group; `CloudOrderStore.ListPending(scope)` returns orders ordered by `DispatchRank` ascending then `SubmittedAtUtc` ascending. `OrderPendingListTests` (1 test) submits a guest order then a registered order and asserts the registered order is returned first. |

Full-suite regression after closure (independently reproduced, both branches): `dotnet build Commerce.sln` — 0 errors. `dotnet test Commerce.sln` — **534/534 passed** (Bootstrap 1, Upgrade 19, Integration 514), up from the 529/529 baseline by exactly the 5 new backend tests above, zero regressions. `npm run test` — 60/60 (Vitest suite unaffected by the E2E addition, which Vitest does not run). `npm run build` — clean.

No new WARNING or CRITICAL findings were introduced by this closure. Verdict remains **PASS**, now with 0 open follow-ups from the original PASS WITH WARNINGS verdict, plus one explicitly noted residual limitation (the new E2E test is unexecuted-but-reviewed, not unexecuted-and-unreviewed).
