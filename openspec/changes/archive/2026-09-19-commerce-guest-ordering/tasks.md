# Tasks: Commerce Guest Ordering

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2,700–3,000 (design forecast) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 → PR 2 → PR 3 → PR 4 → PR 5 → PR 6, stacked to main |
| Delivery strategy | auto-chain |
| Chain strategy | stacked-to-main |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

`delivery_strategy = auto-chain` resolves this without further input. Each PR
targets the previous PR's branch in sequence (stacked-to-main, not a
tracker-branch chain): PR 2 targets PR 1's branch, PR 3 targets PR 2's branch,
etc.; every PR eventually merges to main in order. Loading the `chained-pr`
skill (registry skill `gentle-ai-chained-pr`, resolved by the skill-resolver
convention, not hardcoded) is required for `sdd-apply` on this change.

Units 1–5 form a complete, shippable server-side guest surface (~2,130 lines):
the public surface stays dark (404) behind unset `GuestOrdering__*` config
until Unit 5's PR lands and the variables are set, so no intermediate PR
exposes a half-built public path. Unit 6 (web) is the only PR that reworks a
working screen and depends on all five backend PRs being mergeable.

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|---|---|---|---|---|---|
| 1 | Domain origin (`OrderOrigin`, `GuestContact`, `OrderActors`, `Order` invariant) | PR 1 → main | `dotnet test --filter OrderOriginTests` | N/A (pure domain, no I/O) | Revert PR 1; nothing consumes the new field yet |
| 2 | Verification substrate (`0010` migration, `PostgresGuestVerificationStore`, `GuestVerificationService`, `GuestVerificationThrottle`) | PR 2 → PR 1's branch | `dotnet test --filter GuestVerificationTests\|GuestVerificationThrottleTests\|MigrationRlsTests` | psql `0010` apply + `/health/ready` | Revert PR 2; run `0010`'s inverse block |
| 3 | Customer session (`CustomerCookie` scheme, `"Customer"` policy, `/customer/sign-in`, negative-auth matrix) | PR 3 → PR 2's branch | `dotnet test --filter CustomerSessionIsolationTests` | `WebApplicationFactory` + Postgres | Revert PR 3; staff auth untouched throughout |
| 4 | Guest submission branch (`GuestOrderTarget`, `SubmitGuestAsync`, extracted `ResolveLinesAsync`, ADR-010 divergence test) | PR 4 → PR 3's branch | `dotnet test --filter GuestOrderingTests\|ADR010` | `WebApplicationFactory` + Postgres | Revert PR 4; registered path is byte-identical |
| 5 | Public surface + rate limiting (`PublicOrdering.cs`, `AddRateLimiter`, config-gated mapping, 429/404 tests) | PR 5 → PR 4's branch | `dotnet test --filter PublicRateLimitTests\|GuestOrderingTests` | `WebApplicationFactory` + Postgres; manual: unset `GuestOrdering__*` and confirm 404 | Unset the two env vars — narrowest rollback; full revert also available |
| 6 | Web rework (`OrderScreen.tsx` peer branch, `OrderLinesEditor`, `publicOrdering.ts`/`customerSession.ts`, E2E) | PR 6 → PR 5's branch | `npm run test -- OrderScreen` | `npm run build`; Playwright `ordering.spec.ts` against dev `LogOnlyEmailSender` | Revert PR 6 (web slice only); backend unaffected |

## Phase 1: Domain Origin (Unit 1, ~200 lines)
- [x] 1.1 RED: `OrderOriginTests` — constructing `Order` with `RegisteredCustomer` and no `CustomerId` throws (Requirement: Order Construction Invariant, scenario "Registered order without a CustomerId is rejected")
- [x] 1.2 RED: `OrderOriginTests` — constructing `Order` with `Guest` and a non-null `CustomerId` throws (scenario "Guest order with a CustomerId is rejected")
- [x] 1.3 GREEN: `Commerce.Domain/Ordering/OrderOrigin.cs` (`Guest | RegisteredCustomer`); `Commerce.Domain/Ordering/GuestContact.cs` (`DocumentId`, `Channel`, `ContactAddress`, `DisplayName`, `DeliveryNotes?`, blank-field guards); `Commerce.Domain/Ordering/OrderActors.cs` (`PublicGuest` non-empty sentinel)
- [x] 1.4 GREEN: rework `Order.cs` — `CustomerId → Guid?`, required `Origin`, `GuestContact?`, paired invariant replacing the old non-empty `CustomerId` guard
- [x] 1.5 RED+GREEN: `DispatchRank` — `0` for `RegisteredCustomer`, `1` for `Guest` (Requirement: Guest Order Non-Priority Ranking, scenario "Registered orders rank above guest orders when prioritizing")
- [x] 1.6 GREEN: `CloudOrderStore.Submit`'s internal `new Order(...)` call updated mechanically to pass `OrderOrigin.RegisteredCustomer`/`guestContact: null` so it keeps compiling against the new `Order` constructor; `AttemptDelivery` untouched. NOTE (session-scope deviation, documented): the public `Submit` signature itself was deliberately NOT changed to accept `OrderOrigin`/`GuestContact` parameters — that is Unit 4's `SubmitGuestAsync`/guest submission-branch work (`CloudOrderSubmissionService.cs` is explicitly out of scope for this unit per the apply session's scope directive). Revisit when Unit 4 lands.
- [x] 1.7 N/A for this unit (documented deviation): `Endpoints/Ordering.cs` has no existing pending-list/priority-ordered read endpoint to wire `DispatchRank` into yet, and it does not call `Order`'s constructor or `CloudOrderStore.Submit` with an origin argument (`CloudOrderSubmissionService.Submit` is the only caller, out of scope here). `DispatchRank` is implemented and unit-tested on `Order` (task 1.5); wiring a pending-list query by `DispatchRank` then `SubmittedAtUtc` is deferred to whichever unit introduces that read path.
- [x] 1.8 Regression guard: existing `Order` construction tests in `CustomerTests.cs`/`OrderingTests.cs` updated mechanically for the new constructor shape and pass unchanged in behavior (`Order_Constructor_EmptyCustomerId_Throws`, `Order_Constructor_NonEmptyCustomerId_Succeeds`, `OrderLines_RemainUnchanged_AfterLaterCatalogueRename`)

## Phase 2: Verification Substrate (Unit 2, ~600 lines)
- [x] 2.1 RED+GREEN: `deploy/db/migrations/0010_guest_ordering.sql` — `guest_order_verifications` table/index + RLS/policies/grants mirroring `password_reset_tokens`; `MigrationRlsTests` applies twice cleanly, org B cannot read org A's rows, `app_runtime` has no `DELETE`
- [x] 2.2 Mirror DDL into `deploy/dev/db/init-rls.sql`; update `deploy/README.md`/`staging-runbook.md` with apply + inverse + the two new `GuestOrdering__*` env vars
- [x] 2.3 Add `guest_order_verifications` (+ `relforcerowsecurity` + policy name) to `PostgresReadinessHealthCheck`; `/health/ready` fails before, passes after
- [x] 2.4 RED: `GuestVerificationTests` — wrong code increments `attempt_count`; the 5th attempt burns the row (Requirement: Guest Verification Gate Before Admission, scenario "Expired or incorrect verification code is rejected")
- [x] 2.5 RED: expired / consumed / unknown verification all return the same failure; code is never persisted in plaintext
- [x] 2.6 GREEN: `PostgresGuestVerificationStore` (issue / find / record-attempt / confirm / consume), `GuestVerificationService` (code generation via `RandomNumberGenerator`, SHA-256 hashing, 10-min expiry, supersede-prior-row transaction, `IEmailSender` composition)
- [x] 2.7 RED+GREEN: `GuestVerificationThrottleTests` — per-address and per-IP key spaces, capped/evicting, injected clock (`ResetRequestThrottle` shape)
- [x] 2.8 RED+GREEN: a failing `IEmailSender` send still returns 202 and still issues the row (threat-matrix Process integration row); no test reaches the network with `LogOnlyEmailSender`

## Phase 3: Customer Session (Unit 3, ~380 lines)
- [x] 3.1 GREEN: `CustomerCookie` constant in `CloudAuthenticationSchemes.cs`
- [x] 3.2 GREEN: `Program.cs` — register `CustomerCookie` scheme (`Cookie.Name = "commerce.customer"`, `Cookie.Path = "/customer"`, API-only 401/403 redirects) and `"Customer"` policy naming only that scheme
- [x] 3.3 RED: `/customer/sign-in` with a staff account (`customer_id IS NULL`) returns the same generic 401 as a bad password (Requirement: Admin-Provisioned Customer Login Only is unaffected; user-credentials Requirement: Customer-Scoped Session, scenario "Registered customer sign-in issues a customer-scoped session")
- [x] 3.4 GREEN: `Endpoints/CustomerSession.cs` — `/customer/sign-in`, `/customer/sign-out`, `/customer/me`
- [x] 3.5 RED+GREEN: `CustomerSessionIsolationTests` — a customer cookie → 401/403 on `/orders`, `/catalog`, `/customers`, `/pricing`, `/account/users`, `/platform/*` (public-order-surface Requirement: Customer-Scoped Session, scenario "Customer session cannot reach a staff endpoint")
- [x] 3.6 RED+GREEN: `CustomerSessionIsolationTests` — a staff cookie → 401 on `/customer/*` (scenario "Staff session cannot be substituted for the customer scheme")
- [x] 3.7 RED+GREEN: route-table assertion — no `/register`-shaped route exists anywhere in the mapped endpoint list (user-credentials Requirement: Admin-Provisioned Customer Login Only, scenario "No public self-registration endpoint exists")

## Phase 4: Guest Submission Branch (Unit 4, ~430 lines)
- [x] 4.1 GREEN: `Commerce.Cloud.Api/Tenancy/GuestOrderTarget.cs` — `sealed record GuestOrderTarget(Guid OrganizationId, Guid DestinationBranchId)`, `TryFromConfiguration`
- [x] 4.2 RED+GREEN: `GuestOrderTarget.TryFromConfiguration` fails on missing/unparseable `GuestOrdering:OrganizationId`/`GuestOrdering:BranchId` (tenant-access-foundation Requirement: Anonymous Request Organization Resolution)
- [x] 4.3 GREEN: `Commerce.Cloud.Api/Tenancy/PublicScopeEndpointFilter.cs` — stamps `CloudTenantScope` from `GuestOrderTarget` for claim-less requests
- [x] 4.4 RED: `CloudOrderSubmissionService` — extracting `ResolveLinesAsync(scope, lines, discountPercentage, ct)` keeps the existing registered-path denial checks verbatim and in order (regression guard)
- [x] 4.5 GREEN: extract shared private `ResolveLinesAsync`; wire `SubmitAsync` (registered) through it unchanged
- [x] 4.6 RED: guest submission calls `ResolveAsync(discountPercentage: null)` — guest receives undiscounted list price (guest-ordering Requirement: Guest Price Resolution, scenario "Guest receives list price")
- [x] 4.7 GREEN: `CloudOrderSubmissionService.SubmitGuestAsync` — no `CustomerOrderingAccess`/`Customer` resolution, consumes verification immediately before `CloudOrderStore.Submit`
- [x] 4.8 RED+GREEN: ADR-010 divergence — same presentation submitted guest vs. discounted registered customer yields a strictly lower registered price, asserted on the two submitted orders (scenario "Registered customer with a discount pays strictly less than a guest")
- [x] 4.9 RED+GREEN: a `no-effective-price` denial on any line denies the whole guest order and leaves the verification **unconsumed**
- [x] 4.10 RED+GREEN: an unconfirmed / expired / already-consumed / mismatched-contact verification is rejected and no order is stored (public-order-surface Requirement: Guest Verification Gate Before Admission, scenarios "Unverified guest submission is rejected" and "Expired or incorrect verification code is rejected"). Mismatched-contact is covered at the `GuestVerificationService.TryConsumeAsync` layer by Unit 2's `TryConsumeAsync_MismatchedContact_Fails_AndLeavesRowUnconsumed` — `SubmitGuestAsync` denies identically on any `TryConsumeAsync` failure, so no separate mismatched-contact case was duplicated here.
- [x] 4.11 Regression guard: existing access/binding/customer-enabled denial tests for `SubmitAsync` unchanged
- [x] 4.12 GAP-CLOSING FOLLOW-UP (Unit 6b, not in the original Phase 1-6 breakdown — found during Unit 6's independent verification: design.md's "Registered customer order" data flow and Unit 6's already-written `customerSession.ts` `submitCustomerOrder` both target `POST /customer/orders`, but no Phase 1-5 task ever created it, confirmed by grep before starting). RED+GREEN: `CloudOrderSubmissionService.SubmitForCustomerSessionAsync` (customer resolved from session, no access-credential check — the session IS the authorization; same "not-found"/"customer-disabled" denial checks and ordering as `SubmitAsync`, minus the credential-specific checks which do not apply) + `POST /customer/orders` in `Endpoints/CustomerSession.cs` (policy `"Customer"`, gated by `guestOrderTargetConfigured` for its destination-branch resolution, mirroring `MapPublicOrderingEndpoints`'s config gate) + `CustomerOrderSubmissionTests.cs` (happy path 200/Accepted, unauthenticated 401, staff-cookie-forcibly-presented cannot substitute). Closes the gap documented in Unit 6's apply-progress ("No `/customer/orders` backend endpoint exists").

## Phase 5: Public Surface + Rate Limiting (Unit 5, ~520 lines)
- [x] 5.1 GREEN: `Commerce.Cloud.Api/Endpoints/PublicOrdering.cs` — `GET /public/catalog/presentations`, `POST /public/guest-orders/verification`, `POST /public/guest-orders/verification/confirm`, `POST /public/guest-orders`
- [x] 5.2 RED+GREEN: anonymous catalogue read returns the catalogue scoped to `GuestOrderTarget` (public-order-surface Requirement: Public Catalogue Read, scenario "Anonymous catalogue read")
- [x] 5.3 GREEN: `Program.cs` — `AddRateLimiter` with four named fixed-window policies (verification-request 5/15m, confirm 10/15m, submit 10/hour, catalog-read 60/min), partitioned by remote IP, `UseRateLimiter()`, attached only to the public group
- [x] 5.4 RED+GREEN: `PublicRateLimitTests` — an abusive burst on each public route → 429 + `Retry-After`, no order admitted (Requirement: Rate Limiting on Guest Submission, scenario "Abusive burst is throttled")
- [x] 5.5 RED+GREEN: `PublicRateLimitTests` — a concurrent staff/customer request during that burst is unaffected (scenario "Rate limiting is isolated to the public group")
- [x] 5.6 GREEN: `Program.cs` — register `GuestOrderTarget` singleton, store/service/throttle DI, `MapCustomerEndpoints()`, `MapPublicOrderingEndpoints()` **only when `GuestOrderTarget.TryFromConfiguration` succeeds**
- [x] 5.7 RED+GREEN: threat-matrix Routing row — with `GuestOrdering__*` absent, every `/public/*` route is 404 (never mapped) and the app still starts (public-order-surface Requirement: Anonymous Request Organization Resolution is satisfied only when configured; design's "narrowest rollback")
- [x] 5.8 RED+GREEN: threat-matrix Routing row — a guest DTO has no org/branch member to populate; enumerating the mapped route table shows no self-registration route (public-order-surface Requirement: No Self-Registration Route, scenario "No self-registration endpoint exists")
- [x] 5.9 RED+GREEN: threat-matrix Process-integration row — a failing `IEmailSender` still returns 202 and still issues the verification row over the real `/public/*` endpoint (end-to-end confirmation of Phase 2's unit-level guard)
- [x] 5.10 GREEN: full guest flow integration test — request → confirm → submit ⇒ `Origin = Guest`, `CustomerId = null`, `GuestContact` persisted, `ActorId = OrderActors.PublicGuest` (guest-ordering Requirement: Guest Identity Capture on the Order, scenario "Guest order carries identity fields")

## Phase 6: Web Rework (Unit 6, ~600 lines)
- [x] 6.1 RED (Vitest): `OrderScreen.test.tsx` — one screen renders both peer options with guest listed first and no login-pressure copy
- [x] 6.2 GREEN: rework `OrderScreen.tsx` — single route, two-option segmented control (`Order as guest` / `Sign in to order`), guest first
- [x] 6.3 RED+GREEN: `OrderLinesEditor.test.tsx` — shared component renders catalog-picker lines with no raw GUID input fields on the guest path
- [x] 6.4 GREEN: `src/Commerce.Web/src/components/OrderLinesEditor.tsx` — shared presentation picker + quantity rows for both branches
- [x] 6.5 GREEN: `src/Commerce.Web/src/api/publicOrdering.ts`, `customerSession.ts`, `types.ts` — public + customer clients; `OrderOrigin`, `GuestContact` DTOs
- [x] 6.6 GREEN: wire guest branch through `publicOrdering.ts` (catalog read, verification request/confirm, submit) end to end in `OrderScreen.tsx`
- [x] 6.7 CLOSED via Phase 8.2 below: `src/Commerce.Web/e2e/ordering.spec.ts` now has a full request→confirm→submit guest-flow test using the Phase 8.2 dev-only seam to read back the real verification code, plus the original defense-in-depth (pre-confirmation submit block) test kept as its own regression guard. Written and code-reviewed this session; **not executed against a live server in this session** (requires the local HTTPS proxy + `dotnet run` Cloud.Api + Postgres per `README.md`'s "E2E tests" section — out of scope to boot here). `npm run test`/`npm run build` (Vitest unit suite + tsc/vite) are green.
- [x] 6.8 Regression guard: the pre-existing staff-operated submission path (raw customerId/accessCredential/destinationBranchId/actorId fields, `POST /orders/`) is extracted verbatim to `StaffOrderScreen.tsx`, still mounted at `/app/orders` behind `RequireAuth`, unchanged behaviorally — `StaffOrderScreen.test.tsx` reuses the former `OrderScreen.test.tsx` assertions verbatim and passes; `e2e/ordering.spec.ts`'s two pre-existing staff-path tests are untouched and target the same selectors/route.
- [x] 6.9 `npm run test` and `npm run build` full pass (60/60 Vitest tests passing, `tsc -b && vite build` clean)

## Phase 7: Full-Suite Verification
- [x] 7.1 `dotnet test Commerce.sln` full pass across Units 1–5 — 526/526 passed, 0 failed, 0 skipped (unchanged from Unit 5's baseline; Unit 6 touched no backend files)
- [x] 7.2 `npm run test` and `npm run build` full pass in `src/Commerce.Web` — 60/60 Vitest tests passing (20 files), `tsc -b && vite build` clean
- [x] 7.3 Confirm every applicable threat-matrix row (Routing, Process integration) has a passing RED test per unit; `N/A` rows remain untouched — confirmed via Units 2/5's existing RED tests (`GuestVerificationTests`, `PublicRateLimitTests`'s routing/config/process-integration cases); no new threat-matrix row applies to Unit 6 (web has no routing/process-integration boundary of its own)

## Phase 8: Follow-Up Closure

Closes the 3 non-blocking follow-ups tracked by `verify-report.md`'s PASS WITH
WARNINGS verdict (WARNING 1, 2, 3). Backend portions (8.1, 8.3) landed on
`feat/commerce-guest-ordering-01-backend` (PR #39); the E2E wiring (8.2) and
this paperwork land on `feat/commerce-guest-ordering-02-web` (PR #40, rebased
onto #39's Phase 8 commit).

- [x] 8.1 RED: `CustomerOrderSubmissionWithoutGuestConfigTests` — with `GuestOrdering:*` absent, a signed-in customer's `POST /customer/orders` is `405 MethodNotAllowed` (route never mapped) and an anonymous caller also gets `405` instead of `401` (Requirement: order-origin channel independence, ADR-009). GREEN: `MapCustomerSessionEndpoints` no longer takes a `guestOrderTargetConfigured` gate — `/customer/orders` is ALWAYS mapped alongside `/sign-in`/`/sign-out`/`/me`; `GuestOrderTarget` is resolved defensively via `httpContext.RequestServices.GetService<GuestOrderTarget>()` instead of as a DI parameter, and a missing target degrades to an explicit `503 ordering-destination-not-configured` AFTER auth runs, never a route-level 404/405. Files: `Endpoints/CustomerSession.cs`, `Program.cs` (call-site), `tests/Commerce.Integration/CustomerOrderSubmissionTests.cs`.
- [x] 8.2 RED+GREEN: dev-only verification-code read-back seam, mirroring the existing `/internal/test-seed/user` precedent. `LogOnlyEmailSender` retains the last `EmailMessage` per recipient; `GET /internal/test-seed/guest-verification-code?contactAddress={email}` (Development-only) extracts the 6-digit code and returns it. Wired into the full guest-flow Playwright E2E (closes 6.7 above). Files: `Email/LogOnlyEmailSender.cs`, `Endpoints/TestSeedEndpoints.cs`, `tests/Commerce.Integration/TestSeedGuestVerificationCodeTests.cs`, `e2e/ordering.spec.ts`.
- [x] 8.3 RED+GREEN: `GET /orders/pending` — the first consumer of `Order.DispatchRank` as a sort key end to end. `CloudOrderStore.ListPending(scope)` returns orders ordered by `DispatchRank` ascending then `SubmittedAtUtc` ascending. Files: `Ordering/CloudOrderStore.cs`, `Endpoints/Ordering.cs`, `tests/Commerce.Integration/OrderPendingListTests.cs`.
- [x] 8.4 Full-suite regression: `dotnet build Commerce.sln` 0 errors, `dotnet test Commerce.sln` 534/534 (verified independently on both branches this session). `npm run test` 60/60, `npm run build` clean on the web branch. The new full-cycle E2E test (8.2) was written and reviewed but not executed against a live server this session.
