# Tasks: Commerce Password Recovery

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~1150-1300 (design's own estimate: ~230 migration/store, ~260 session-versioning+renew, ~340 email/throttle/reset routes, ~320 admin+SPA, all inclusive of tests) |
| Effective review budget (session override) | 1500 changed lines (session-configured; overrides the skill's 400-line default) |
| 400-line budget risk (vs. skill default) | High |
| Budget risk vs. session's 1500-line override | Medium — estimate lands under budget but leaves limited headroom |
| Chained PRs recommended | No (delivery strategy is `single-pr`; design's own chain recommendation is preserved as in-PR task ordering instead) |
| Suggested split | Single PR, internally ordered so session-versioning/renewal lands (and is tested) before the anonymous reset-request/confirm routes go live |
| Delivery strategy | single-pr |
| Chain strategy | size-exception |

Decision needed before apply: Yes
Chained PRs recommended: No
Chain strategy: size-exception
400-line budget risk: High

**Note on the two budgets**: the skill's literal guard line above reports risk against the skill's own 400-line default (High, since estimated ~1150-1300 lines far exceeds 400). The session explicitly configured a 1500-line review budget, under which the estimate fits with the delivery strategy set to `single-pr`. My own task-level breakdown below does not materially diverge from the design's ~1150-line estimate — no basis to project past 1500. If actual authored lines during apply approach the 1500 ceiling, flag before merging rather than silently exceeding it.

**Security ordering preserved as task order, not PR order**: Phase 2 (session-versioning + renew) MUST be implemented and green before Phase 3 (anonymous reset-request/confirm) begins, exactly as the design's Work Unit 2 → Unit 3 dependency requires — the anonymous reset surface must never be exercisable before session invalidation exists, even within one PR's commit history.

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Migration + store + readiness check foundation (Phase 1) | PR 1 (size:exception, single PR) | `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~MigrationRlsTests\|FullyQualifiedName~PasswordRecovery` | Apply `0005_password_recovery.sql` twice against `deploy/dev/compose.yaml` | Revert; `DROP TABLE password_reset_tokens; ALTER TABLE users DROP COLUMN session_version;` (no dependents) |
| 2 | Session-versioning + renew (Phase 2) — must be green before Unit 3 begins | PR 1 (same PR, later commits) | `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~RenewPassword\|FullyQualifiedName~SessionVersion` | Sign in, renew, confirm stale cookie rejected | Revert commits; sessions reset once more |
| 3 | Email/throttle/reset-request/confirm (Phase 3) | PR 1 (same PR, later commits) | `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~PasswordRecoveryTests` and `dotnet test tests/Commerce.Cloud --filter FullyQualifiedName~ResetRequestThrottle` | Trigger a real reset via `LogOnlyEmailSender` (no `RESEND_API_KEY` in dev) | Revert commits; table becomes unused, not broken |
| 4 | Admin-forced reset + SPA (Phase 4) | PR 1 (same PR, final commits) | `npm run build && npx vitest run` in `src/Commerce.Web`; `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~AdminReset` | `npm run build` + manual `/reset-password?token=` flow | Revert commits |

## Phase 1: Migration and Persistence Foundation

- [x] 1.1 RED: add `MigrationRlsTests` cases in `tests/Commerce.Integration/MigrationRlsTests.cs` for `password_reset_tokens` (cross-org INSERT rejected; unscoped UPDATE that does not set `consumed_at` rejected; `0005` idempotent on re-apply) — design: Token-table RLS.
- [x] 1.2 GREEN: create `deploy/db/migrations/0005_password_recovery.sql` (`password_reset_tokens` table, indexes, forced RLS, 4 policies, grants, `users.session_version` column) per design's Interfaces/Contracts SQL.
- [x] 1.3 GREEN: append the same DDL verbatim to `deploy/dev/db/init-rls.sql`. Re-run 1.1 to confirm green.
- [x] 1.4 Create `src/Commerce.Cloud.Api/Persistence/PasswordRecoveryRecords.cs` (`PasswordResetTokenRecord`).
- [x] 1.5 RED: add store tests (new or extended integration file) for `IssueTokenAsync`, `FindTokenAsync` (unscoped), `ConsumeAndSetPasswordAsync`, `SetPasswordAsync`, `GetSessionVersionAsync`.
- [x] 1.6 GREEN: create `src/Commerce.Cloud.Api/Persistence/PostgresPasswordRecoveryStore.cs` implementing all five methods, mirroring `PostgresUserAccountStore`'s transaction shape.
- [x] 1.7 Modify `PostgresUserAccountStore.cs`: `FindByEmailAsync`/`LoadActorAsync` project `session_version`; `UserCredentialRecord` gains `SessionVersion`.
- [x] 1.8 RED: extend `PostgresReadinessHealthCheck` tests asserting `/health/ready` fails when `password_reset_tokens` FORCE-RLS or its policies are missing.
- [x] 1.9 GREEN: extend `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` — add the table + `relforcerowsecurity` + 4 policy names to the readiness query, 21→26 ordinal reads, `allHealthy` conjunction, both result messages.
- [x] 1.10 Update `deploy/README.md` with a `0005_password_recovery.sql` section (direct-connection `psql -f`, migrate-before-deploy ordering) and add `RESEND_API_KEY`/`EMAIL_FROM_ADDRESS`/`PUBLIC_BASE_URL` to the required-Railway-variables list; mirror the three variables in `deploy/staging-runbook.md`.

## Phase 2: Session Versioning and Renewal (must be green before Phase 3)

- [x] 2.1 RED: unit tests for `SessionVersionCache` (TTL expiry triggers reload; write-through `Set` visible immediately) with an injected clock and fake loader.
- [x] 2.2 GREEN: create `src/Commerce.Cloud.Api/Authentication/SessionVersionCache.cs` (60s-TTL `ConcurrentDictionary<Guid,(int, DateTimeOffset)>`, org-scoped DB read on miss).
- [x] 2.3 RED: integration test — a cookie with no `session_ver` claim is rejected on the next request (fail-closed).
- [x] 2.4 GREEN: create `src/Commerce.Cloud.Api/Authentication/SessionVersionValidator.cs` implementing `OnValidatePrincipal` (missing claim ⇒ `RejectPrincipal`; mismatch vs. cache ⇒ `RejectPrincipal` + `SignOutAsync`).
- [x] 2.5 Wire `session_ver` claim into the sign-in claim set and `options.Events.OnValidatePrincipal` into the existing `AddCookie` block in `Program.cs`; register `SessionVersionCache` in DI.
- [x] 2.6 RED: `AccountEndpointTests` — renew: correct current password ⇒ 204 and the refreshed cookie still authenticates; wrong current password ⇒ 401 and the hash is unchanged — spec: "Correct current password renews the password" / "Wrong current password is rejected".
- [x] 2.7 GREEN: add `RenewPasswordRequest` record and `POST /account/renew-password` in `Account.cs` (verify current password, `SetPasswordAsync`, `cache.Set`, `SignOutAsync` + `SignInAsync` carrying the new `session_ver`).
- [x] 2.8 RED: extend session-invalidation coverage — a cookie captured before renewal is rejected on the next request — spec: "Prior session cookie stops authenticating after a change" (renewal leg). Confirm green before starting Phase 3.

**Phase 2 confirmed green**: `dotnet test Commerce.sln` — 158/158 passed in Commerce.Integration (plus 19/19 Commerce.Upgrade, 1/1 Commerce.Bootstrap.Tests), including the new renew/session-invalidation/cache/validator tests, before Phase 3 began.

## Phase 3: Email, Throttle, Reset-Request/Confirm (depends on Phase 2 being green)

- [x] 3.1 Create `src/Commerce.Cloud.Api/Email/EmailOptions.cs` (`ResendApiKey`, `FromAddress`, `PublicBaseUrl`).
- [x] 3.2 Create `src/Commerce.Cloud.Api/Email/IEmailSender.cs` (`EmailMessage` record + `IEmailSender` seam).
- [x] 3.3 RED: `ResendEmailSenderTests` — builds the exact `POST /emails` body and bearer header; non-2xx ⇒ `false`, never throws (stub `HttpMessageHandler`, no network).
- [x] 3.4 GREEN: create `src/Commerce.Cloud.Api/Email/ResendEmailSender.cs` over a typed `AddHttpClient<ResendEmailSender>` client.
- [x] 3.5 GREEN: create `src/Commerce.Cloud.Api/Email/LogOnlyEmailSender.cs` (stdout link at `LogInformation` + startup warning).
- [x] 3.6 GREEN: register `AddHttpClient<ResendEmailSender>` and conditional `IEmailSender` registration (Resend when `RESEND_API_KEY` present, else `LogOnlyEmailSender`) in `Program.cs`.
- [x] 3.7 RED: `ResetRequestThrottleTests` — within-window email denial, window rollover, per-IP threshold, independence of the two key spaces, eviction at cap, using an injected clock.
- [x] 3.8 GREEN: create `src/Commerce.Cloud.Api/Authentication/ResetRequestThrottle.cs` (fixed-window counters, capped dictionaries, `TryAcquire`).
- [x] 3.9 RED: `AccountEndpointTests` — reset-request: known email issues exactly one token and calls the sender; unknown email issues none and sends none; both return an empty-body 202; revoked user issues none — spec: "Known email receives a reset token and email" / "Unknown email looks identical to a known one".
- [x] 3.10 RED: extend — repeated request within the throttle window issues no token/mail and is byte-identical to the first 202 — spec: "Repeated requests are throttled".
- [x] 3.11 GREEN: add `ResetPasswordRequest` record and `POST /account/reset-password/request` in `Account.cs` (blank email ⇒ `ValidationProblem`; throttle check; unscoped `FindDirectoryEntryAsync` with dummy-hash timing parity; `IssueTokenAsync`; `IEmailSender.SendAsync`; always 202).
- [x] 3.12 RED: `AccountEndpointTests` — confirm: valid token sets the hash and the new password signs in; replay ⇒ 401; expired (backdated `expires_at`) ⇒ 401; unknown token ⇒ 401, all three identical; blank body ⇒ 400 and the token still works afterwards — spec: "Valid token sets a new password" / "Token cannot be reused" / "Expired token is rejected".
- [x] 3.13 GREEN: add `ConfirmResetPasswordRequest` record and `POST /account/reset-password/confirm` in `Account.cs` (validate before consuming, unscoped `FindTokenAsync`, single-tx `ConsumeAndSetPasswordAsync`, `cache.Set`, 204/401).
- [x] 3.14 GREEN: register `PostgresPasswordRecoveryStore` and `ResetRequestThrottle` in DI (`Program.cs`).

## Phase 4: Admin-Forced Reset and Web Screens

- [x] 4.1 RED: `AccountEndpointTests` (two seeded orgs) — `ManageUsers` holder resets a same-org user ⇒ 204 and the target's pre-change cookie stops authenticating; no `ManageUsers` ⇒ 403; cross-org target ⇒ 404 identical to a nonexistent id — spec: "Admin resets a same-organization user's password" / "Cross-organization target is rejected".
- [x] 4.2 GREEN: add `AdminResetPasswordRequest` record and `POST /account/users/{userId:guid}/reset-password` (`RequireAuthorization` + `TenantScopeEndpointFilter`, caller `LoadActorAsync` + `ManageUsers` check, target `LoadActorAsync` scoped to caller's org ⇒ null on cross-org, `SetPasswordAsync`, `cache.Set`).
- [x] 4.3 Update `src/Commerce.Web/src/api/types.ts` with the four new request records.
- [x] 4.4 Update `src/Commerce.Web/src/api/account.ts`: `requestPasswordReset`, `confirmPasswordReset`, `renewPassword`, `adminResetPassword`.
- [x] 4.5 Create `src/Commerce.Web/src/auth/useResetToken.ts` (reads `?token=` once at mount, exposes `clear()`).
- [x] 4.6 Create `src/Commerce.Web/src/screens/ForgotPasswordScreen.tsx` (email field, uniform confirmation message).
- [x] 4.7 Create `src/Commerce.Web/src/screens/ResetPasswordScreen.tsx` (new-password field, clears token + returns to sign-in on success).
- [x] 4.8 Create `src/Commerce.Web/src/screens/RenewPasswordScreen.tsx` (current + new password, reachable from `AuthenticatedApp`'s header).
- [x] 4.9 Modify `src/Commerce.Web/src/App.tsx`: `Root` renders `ResetPasswordScreen` when a token is present; signed-out view toggles `SignInScreen`/`ForgotPasswordScreen`; `AuthenticatedApp` adds a `renew` tab.
- [x] 4.10 RED→GREEN: create/extend Vitest coverage (`*.test.tsx`) — forgot screen renders the same confirmation for any input; reset screen reads `?token=`, posts it, scrubs the URL on success; renew screen posts both fields.

## Phase 5: Verification and Cleanup

- [x] 5.1 Apply `0005_password_recovery.sql` twice against `deploy/dev/compose.yaml` to confirm idempotency.
- [x] 5.2 Run `dotnet test Commerce.sln`, confirming all existing tests plus new `PasswordRecoveryTests`, `ResetRequestThrottleTests`, and extended `MigrationRlsTests`/`AccountEndpointTests` pass.
- [x] 5.3 Run `npm run build` and Vitest in `src/Commerce.Web`, confirming the SPA changes compile and pass.
- [x] 5.4 Confirm `TenantScopeResolver`, `TenantAuthorizationService`, `Sync.cs`, `Ordering.cs`, and `Commerce.Pos.Windows` remain untouched — out of scope per design.
- [x] 5.5 Document in `deploy/README.md` that deploying this change signs out every existing session once (missing `session_ver` claim fails closed), and confirm `RESEND_API_KEY`/`EMAIL_FROM_ADDRESS`/`PUBLIC_BASE_URL` runbook entries are present before smoke-testing one real reset.
