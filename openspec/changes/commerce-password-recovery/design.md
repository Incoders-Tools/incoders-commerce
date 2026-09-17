# Design: Commerce password recovery

## Technical Approach

Additive slice on the existing credentials domain. One append-only migration
(`deploy/db/migrations/0005_password_recovery.sql`) adds `password_reset_tokens`
plus a `users.session_version` column; the same DDL is mirrored verbatim into
`deploy/dev/db/init-rls.sql` (the hand-kept convention `0002`–`0004` already
follow). `Persistence/PostgresPasswordRecoveryStore.cs` is a new store in
`PostgresUserAccountStore`'s exact shape (injected `NpgsqlDataSource`, one
`NpgsqlTransaction` per method, `SELECT set_config('app.current_org_id', $1, true)`
as the first statement — except the token lookup, which is deliberately unscoped
exactly as `FindDirectoryEntryAsync` and `PostgresDeviceCredentialStore` are).
`Endpoints/Account.cs` gains four routes; `Authentication/` gains a session-version
validator and a request throttle; a new `Email/` folder holds a two-line
`IEmailSender` seam with a Resend HTTP implementation. `TenantScopeResolver`,
`TenantAuthorizationService`, `Sync.cs`, `Ordering.cs`, and `Commerce.Pos.Windows`
are untouched.

**Spec vs. proposal divergence (resolved here)**: the proposal's Approach
sentence says admin-forced reset "reuses the same token/email path"; the locked
spec's scenario says the admin "submits a new password for the target user". The
**spec wins** — it is the testable contract. Admin-forced reset sets the target's
hash directly and bumps their `session_version`; no token, no email. Recorded so
the apply phase does not re-litigate it.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| Reset-token storage | Postgres table `password_reset_tokens`, PK = `token_hash` (sha256 hex of a `RandomNumberGenerator.GetBytes(32)` base64url token), row carries `user_id`, `organization_id`, `requested_at`, `expires_at`, `consumed_at`. Same crypto shape as `BootstrapTokenRegistry`/`device_credentials`, but **persisted**: a 1-hour window must survive a container restart and any future second replica, which the in-memory registry explicitly cannot (`BootstrapTokenRegistry` remarks). Plain SHA-256 is correct and not an oversight — the input is 256 bits of uniform randomness, so a slow KDF would add latency for zero gain (`0004`'s exact argument). | Extending `BootstrapTokenRegistry` — org-keyed and in-memory; a restart during the 1-hour window silently strands every pending reset. |
| Token-table RLS | Asymmetric, the `device_credentials` precedent verbatim: `FOR SELECT USING (true)` (the raw token must resolve before the org is known), `FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)`, `FOR UPDATE USING (true) WITH CHECK (consumed_at IS NOT NULL)`. The unscoped UPDATE is structurally limited to *consuming* — an unscoped un-consume or field rewrite is unrepresentable, not merely untested. `ENABLE` + `FORCE ROW LEVEL SECURITY`, `REVOKE ALL FROM PUBLIC`, `GRANT` to `app_runtime`. | A symmetric org-scoped policy — impossible: confirm has no org until it has read the row. No RLS — breaks the repo's every-table-forced convention. |
| Token cleanup | `FOR DELETE USING (expires_at < now() - interval '7 days')` + `GRANT DELETE`, swept opportunistically inside the issue transaction. Bounded growth without a cron job or a background service. | No cleanup (unbounded, slow leak); a hosted sweeper service (new lifetime, new failure mode). |
| Session invalidation | `users.session_version integer NOT NULL DEFAULT 0`. Sign-in stamps it into a `session_ver` cookie claim. `CookieAuthenticationEvents.OnValidatePrincipal` compares the claim against the current value served by a singleton `SessionVersionCache` (`ConcurrentDictionary<Guid,(int, DateTimeOffset)>`, 60-second TTL, org-scoped DB read on miss). Every password-change path increments the column **and write-throughs the new value into the cache in the same request**, so on today's single replica invalidation is immediate; a future second replica degrades to a documented ≤60s staleness bound, never to "never". Mismatch ⇒ `RejectPrincipal()` + `SignOutAsync`. | (a) DB read on every authenticated request — an extra round-trip on the hot path the proposal's risk row explicitly asks to avoid; (b) purely in-memory revocation set — dies on restart, so a restart silently *restores* the invalidated session; (c) rotating the data-protection key — nukes every user's session, not the one that changed. |
| Anti-abuse mechanism | Singleton `Authentication/ResetRequestThrottle` — **in-memory**, fixed-window counters keyed by normalized email and by `HttpContext.Connection.RemoteIpAddress`, opportunistic sweep of expired windows, hard entry cap (10 000 per key space, oldest-window eviction) so IP-cycling cannot grow it unboundedly. Railway runs one replica (`railway.json` declares no replica count; the deploy block sets only start command, healthcheck, and restart policy), the same accepted assumption `BootstrapTokenRegistry` already documents. | **DB-derived cooldown from the token table's timestamps** (the proposal's parenthetical): structurally *cannot* throttle an unknown email, because an unknown email never writes a row — which turns the throttle itself into the enumeration oracle the rest of this design spends its budget closing. Rejected on security grounds, not cost. Redis — a new external dependency, explicitly excluded. |
| Throttle thresholds | Per email: 1 request / 5 minutes, max 3 / hour. Per IP: 10 / hour. Rationale: token lifetime is 1 hour, so a legitimate retry (mail in spam, link mistyped) needs at most 2–3 attempts inside one token's life; 3/hour caps mailbox-bombing at 3 messages per address per hour, below any provider's complaint threshold. 10/hour per IP lets a shared office NAT serve several real users while capping blind enumeration at 240 addresses/day per IP — useless against an 8-digit address space. | A single global limit (either starves the NAT case or leaves enumeration cheap). |
| Throttled response | Still an **empty-body 202**, identical to every other request outcome. The token is simply not issued and no mail is sent. | 429 — leaks that this address/IP crossed a threshold, and a per-*email* 429 is a direct existence oracle. |
| Email seam | `Email/IEmailSender.cs` (`Task<bool> SendAsync(EmailMessage message, CancellationToken ct)`), implemented by `ResendEmailSender` over a typed `AddHttpClient<ResendEmailSender>` client (`https://api.resend.com`, `POST /emails`, `Authorization: Bearer <RESEND_API_KEY>`). No SMTP, no SDK package. Tests substitute a fake sender; no test ever reaches the network. | An SMTP client (the proposal excludes it); calling `HttpClient` inline from the endpoint (untestable, socket-exhaustion prone). |
| Missing `RESEND_API_KEY` | `Program.cs` registers `LogOnlyEmailSender` instead — it writes the reset link to stdout at `LogInformation` and logs a startup warning. This is the *exact* bootstrap-token delivery precedent (plaintext to `railway logs`, never to HTTP) and keeps `deploy/dev/compose.yaml` and integration tests usable without a Resend account. Production readiness is a runbook item, not a crash. | Throwing at startup (breaks local dev and CI for an optional secret); silently dropping mail (an unrecoverable, invisible failure). |
| Send failure | Logged server-side; the caller still gets the same empty-body 202. Leaking "delivery failed" would confirm the address exists. | Surfacing a 5xx — an existence oracle. |
| Reset link, no router | `{PUBLIC_BASE_URL}/reset-password?token=<token>`. `Program.cs`'s existing `MapFallbackToFile("index.html")` already serves that path, so **no server change is needed**. The SPA reads the token once at mount via `new URLSearchParams(window.location.search).get('token')` in a `useResetToken()` hook; `Root` renders `ResetPasswordScreen` when it is present. On success the screen calls `window.history.replaceState({}, '', '/')`, which scrubs the token from the URL/history and drops back to `SignInScreen` — no router dependency added. | Adding `react-router` for three screens (a dependency and a bundle for one query parameter); a hash fragment (never reaches the server, but no cleaner and breaks mail-client link rewriting). |
| Admin authorization shape | `Catalog.cs`'s exact pattern: group with `.RequireAuthorization().AddEndpointFilter<TenantScopeEndpointFilter>()`, scope from `TenantScopeEndpointFilter.GetScope`, actor from `store.LoadActorAsync(scope, NameIdentifier)`, `null || IsRevoked ⇒ Results.Forbid()`, then `actor.EffectivePermissions.HasFlag(Permission.ManageUsers)`. Same-organization scope needs **no explicit check**: the target is loaded with the *caller's* `CloudTenantScope`, so `users_tenant_isolation` RLS returns zero rows for a cross-org target and the handler sees `null` — identical to "no such user", which is exactly the non-disclosure the spec demands. | A `TenantAuthorizationService` action definition — that service is branch/action-oriented and is shared with POS; a two-line permission flag check at the endpoint matches how every other Cloud.Api endpoint already does it. An explicit `organization_id` comparison in C# — weaker than the RLS guarantee and duplicates it. |
| Weak-input ordering | Validate the request body (blank token / blank new password) **before** consuming the token, mirroring `/account/bootstrap`'s explicit comment: a caller mistake must not burn a single-use token. Password-strength policy is unchanged (out of scope) — non-empty is the only rule, as bootstrap has today. | Consume-then-validate — burns the token on a typo. |

## Data Flow

```text
Reset request (anonymous)
  POST /account/reset-password/request {email}
    -> blank email -> ValidationProblem 400
    -> throttle.TryAcquire(emailNormalized, remoteIp)
       -> denied: return 202 (empty body). No token, no mail, no log of the address.
    -> store.FindDirectoryEntryAsync(email)                      [unscoped tx]
       -> miss: dummy VerifyHashedPassword (timing parity) -> 202
    -> CloudTenantScope(directory.OrganizationId)
    -> FindByEmailAsync(scope, email); is_revoked -> 202 (no token)
    -> RandomNumberGenerator.GetBytes(32) -> base64url token
    -> IssueTokenAsync(scope, userId, sha256(token), now+1h)     [set_config tx]
       (same tx: consume all prior unconsumed tokens for this user; purge >7d expired)
    -> IEmailSender.SendAsync(to=email, link={PUBLIC_BASE_URL}/reset-password?token=...)
       -> failure: LogError, continue
    -> 202, empty body   <- every path above returns exactly this

Reset confirm (anonymous)
  POST /account/reset-password/confirm {token, newPassword}
    -> blank token/newPassword -> ValidationProblem 400   (BEFORE any consumption)
    -> store.FindTokenAsync(sha256(token))                       [unscoped tx]
       -> null | consumed_at != null | expires_at <= now -> 401 (generic, undifferentiated)
    -> CloudTenantScope(row.OrganizationId)
    -> one tx: set_config
               -> UPDATE users SET password_hash=$new,
                      session_version = session_version + 1 WHERE id = $user  RETURNING session_version
               -> UPDATE password_reset_tokens SET consumed_at = now()
                      WHERE user_id = $user AND consumed_at IS NULL
               -> COMMIT
    -> sessionVersionCache.Set(userId, newVersion)   // immediate, same process
    -> 204

Renew (authenticated)
  POST /account/renew-password {currentPassword, newPassword}
    -> TenantScopeEndpointFilter -> scope; NameIdentifier -> userId
    -> LoadActorAsync -> null | IsRevoked -> 403
    -> FindByEmailAsync(scope, Name claim) -> VerifyHashedPassword(current)
       -> Failed -> 401 (generic)
    -> same password+session_version tx as confirm
    -> cache.Set; SignOutAsync; SignInAsync with claims carrying the NEW session_ver
       (the acting browser stays signed in; every OTHER cookie is now stale)
    -> 204

Admin-forced reset (authenticated, Permission.ManageUsers)
  POST /account/users/{userId:guid}/reset-password {newPassword}
    -> scope from filter; caller actor from LoadActorAsync
    -> !EffectivePermissions.HasFlag(ManageUsers) -> 403
    -> LoadActorAsync(scope, targetUserId) -> null -> 404
       (cross-org target is null by RLS -- indistinguishable from "no such user")
    -> same password+session_version tx (target's hash + version)
    -> cache.Set(targetUserId, newVersion); caller's own session untouched
    -> 204

Every authenticated request
  OnValidatePrincipal
    -> session_ver claim missing -> RejectPrincipal (pre-change cookies fail closed)
    -> sessionVersionCache.GetAsync(userId, scope)   // 60s TTL, DB read on miss
    -> claim != current -> RejectPrincipal + SignOutAsync
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `deploy/db/migrations/0005_password_recovery.sql` | Create | `password_reset_tokens` (+ indexes, forced RLS, 4 policies, grants) and `ALTER TABLE users ADD COLUMN IF NOT EXISTS session_version`. `0001`–`0004` NOT touched. |
| `deploy/dev/db/init-rls.sql` | Modify | Same DDL appended verbatim (hand-kept sync convention; `MigrationRlsTests` asserts parity). |
| `deploy/README.md` | Modify | New `### commerce-password-recovery — 0005_password_recovery.sql` section after the `0003` section, in the existing shape: direct-connection `psql -f`, migrate-before-deploy ordering (readiness fails closed), idempotency-confirmed note. Plus `RESEND_API_KEY`, `EMAIL_FROM_ADDRESS`, `PUBLIC_BASE_URL` in the required-Railway-variables list. |
| `deploy/staging-runbook.md` | Modify | Same three variables in the per-environment provisioning checklist. |
| `src/Commerce.Cloud.Api/Persistence/PostgresPasswordRecoveryStore.cs` | Create | `IssueTokenAsync`, `FindTokenAsync` (unscoped), `ConsumeAndSetPasswordAsync`, `SetPasswordAsync`, `GetSessionVersionAsync`. |
| `src/Commerce.Cloud.Api/Persistence/PasswordRecoveryRecords.cs` | Create | `PasswordResetTokenRecord(string TokenHash, Guid UserId, Guid OrganizationId, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt, DateTimeOffset? ConsumedAt)`. |
| `src/Commerce.Cloud.Api/Authentication/ResetRequestThrottle.cs` | Create | Fixed-window per-email/per-IP counters, injected clock, capped dictionaries. |
| `src/Commerce.Cloud.Api/Authentication/SessionVersionCache.cs` | Create | 60s-TTL cache + write-through `Set`. |
| `src/Commerce.Cloud.Api/Authentication/SessionVersionValidator.cs` | Create | `CookieAuthenticationEvents.OnValidatePrincipal` implementation. |
| `src/Commerce.Cloud.Api/Email/IEmailSender.cs` | Create | `EmailMessage(string To, string Subject, string HtmlBody, string TextBody)` + seam. |
| `src/Commerce.Cloud.Api/Email/ResendEmailSender.cs` | Create | Typed-client POST `/emails`; non-2xx ⇒ `LogError` + `false`. |
| `src/Commerce.Cloud.Api/Email/LogOnlyEmailSender.cs` | Create | stdout fallback when `RESEND_API_KEY` is absent. |
| `src/Commerce.Cloud.Api/Email/EmailOptions.cs` | Create | `ResendApiKey`, `FromAddress`, `PublicBaseUrl` — flat config keys `RESEND_API_KEY` / `EMAIL_FROM_ADDRESS` / `PUBLIC_BASE_URL`. |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modify | Four routes; `session_ver` claim added to the sign-in claim set; new request records. |
| `src/Commerce.Cloud.Api/Persistence/PostgresUserAccountStore.cs` | Modify | `FindByEmailAsync`/`LoadActorAsync` also project `session_version`; `UserCredentialRecord` gains `SessionVersion`. |
| `src/Commerce.Cloud.Api/Program.cs` | Modify | DI for the new store, throttle, cache, `AddHttpClient<ResendEmailSender>`, conditional `IEmailSender` registration, and `options.Events.OnValidatePrincipal` on the existing `AddCookie` block. |
| `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` | Modify | Add `password_reset_tokens` table + `relforcerowsecurity` + the four policy names to the single readiness query, its 21 → 26 ordinal reads, the `allHealthy` conjunction, and both result messages (including the trailing "Apply …" migration list). |
| `src/Commerce.Web/src/api/account.ts` | Modify | Add `requestPasswordReset`, `confirmPasswordReset`, `renewPassword`, `adminResetPassword` — same `apiFetch<T>(path, {method:'POST', body: JSON.stringify(...)})` shape as `signIn`. |
| `src/Commerce.Web/src/api/types.ts` | Modify | The four request records. |
| `src/Commerce.Web/src/auth/useResetToken.ts` | Create | Reads `?token=` once at mount; exposes `clear()` wrapping `history.replaceState`. |
| `src/Commerce.Web/src/screens/ForgotPasswordScreen.tsx` | Create | Email field; always renders the same "if that address exists, check your inbox" confirmation. |
| `src/Commerce.Web/src/screens/ResetPasswordScreen.tsx` | Create | New-password field; on success clears the token and returns to sign-in. |
| `src/Commerce.Web/src/screens/RenewPasswordScreen.tsx` | Create | Current + new password, reachable from `AuthenticatedApp`'s header. |
| `src/Commerce.Web/src/App.tsx` | Modify | `Root` extends the manual switch: reset token present ⇒ `ResetPasswordScreen`; else signed-out view toggles `SignInScreen`/`ForgotPasswordScreen`; `AuthenticatedApp` adds a `renew` tab. |
| `src/Commerce.Web/src/screens/*.test.tsx` | Create | Vitest coverage for the three screens. |
| `tests/Commerce.Integration/PasswordRecoveryTests.cs` | Create | Endpoint + store coverage (live Postgres, `PostgresTestFixture` skip convention). |
| `tests/Commerce.Integration/MigrationRlsTests.cs` | Modify | Apply `0005` too; idempotent re-apply; cross-org token INSERT blocked; unscoped UPDATE that does not consume is blocked. |
| `tests/Commerce.Cloud/ResetRequestThrottleTests.cs` | Create | Window/threshold/eviction unit tests with an injected clock. |

## Interfaces / Contracts

```sql
CREATE TABLE IF NOT EXISTS password_reset_tokens (
    token_hash      text PRIMARY KEY,                       -- sha256(token) hex; plaintext never stored
    user_id         uuid NOT NULL,                          -- no FK: matches users' existing no-FK status
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    requested_at    timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,                   -- requested_at + 1 hour, set by the app
    consumed_at     timestamptz NULL
);
CREATE INDEX IF NOT EXISTS password_reset_tokens_user_active_idx
    ON password_reset_tokens (user_id) WHERE consumed_at IS NULL;
CREATE INDEX IF NOT EXISTS password_reset_tokens_expires_idx ON password_reset_tokens (expires_at);

ALTER TABLE password_reset_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE password_reset_tokens FORCE ROW LEVEL SECURITY;
REVOKE ALL ON password_reset_tokens FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE, DELETE ON password_reset_tokens TO app_runtime;

-- Asymmetric, the device_credentials precedent: confirm resolves the token
-- BEFORE any tenant scope exists.
DROP POLICY IF EXISTS password_reset_tokens_lookup ON password_reset_tokens;
CREATE POLICY password_reset_tokens_lookup ON password_reset_tokens FOR SELECT USING (true);

DROP POLICY IF EXISTS password_reset_tokens_issue ON password_reset_tokens;
CREATE POLICY password_reset_tokens_issue ON password_reset_tokens
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Unscoped UPDATE is permitted ONLY when the resulting row is consumed, making
-- an unscoped un-consume or field rewrite structurally unrepresentable.
DROP POLICY IF EXISTS password_reset_tokens_consume ON password_reset_tokens;
CREATE POLICY password_reset_tokens_consume ON password_reset_tokens
    FOR UPDATE USING (true) WITH CHECK (consumed_at IS NOT NULL);

DROP POLICY IF EXISTS password_reset_tokens_purge ON password_reset_tokens;
CREATE POLICY password_reset_tokens_purge ON password_reset_tokens
    FOR DELETE USING (expires_at < now() - interval '7 days');

ALTER TABLE users ADD COLUMN IF NOT EXISTS session_version integer NOT NULL DEFAULT 0;
```

```csharp
// Persistence/PostgresPasswordRecoveryStore.cs
Task IssueTokenAsync(CloudTenantScope scope, Guid userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct);
Task<PasswordResetTokenRecord?> FindTokenAsync(string tokenHash, CancellationToken ct);      // UNSCOPED, by design
Task<int> ConsumeAndSetPasswordAsync(CloudTenantScope scope, Guid userId, string tokenHash, string passwordHash, CancellationToken ct); // -> new session_version
Task<int> SetPasswordAsync(CloudTenantScope scope, Guid userId, string passwordHash, CancellationToken ct);                             // -> new session_version
Task<int?> GetSessionVersionAsync(CloudTenantScope scope, Guid userId, CancellationToken ct);

// Authentication/ResetRequestThrottle.cs
public bool TryAcquire(string emailNormalized, IPAddress? remoteIp);   // false => silently drop, still 202

// Email/IEmailSender.cs
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);
public interface IEmailSender { Task<bool> SendAsync(EmailMessage message, CancellationToken ct); }

// Endpoints/Account.cs
public sealed record ResetPasswordRequest(string Email);
public sealed record ConfirmResetPasswordRequest(string Token, string NewPassword);
public sealed record RenewPasswordRequest(string CurrentPassword, string NewPassword);
public sealed record AdminResetPasswordRequest(string NewPassword);
```

New sign-in claim: `new Claim("session_ver", credential.SessionVersion.ToString())`, using the
value read from the `users` row — never from client input, exactly as `org_id` already is.

Reset email body: subject "Reset your Commerce password"; one sentence, one link
(`{PUBLIC_BASE_URL}/reset-password?token=<token>`), an explicit "this link expires
in 1 hour and can be used once", and an "if you did not request this, ignore this
email" line. No user name, no organization name, no other account detail — the
mail must not enrich a mis-addressed recipient.

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | `ResetRequestThrottle`: within-window email denial, window rollover, per-IP threshold, independence of the two key spaces, eviction at cap | xUnit, injected clock |
| Unit | `SessionVersionCache`: TTL expiry triggers a reload; write-through `Set` is visible immediately | xUnit, injected clock + fake loader |
| Unit | `ResendEmailSender`: builds the exact `POST /emails` body and bearer header; non-2xx ⇒ `false`, never throws | xUnit + stub `HttpMessageHandler` (no network) |
| Integration | Request: known email issues exactly one token and calls the sender; unknown email issues none and sends none; both return an empty-body 202; revoked user issues none | `WebApplicationFactory` + live Postgres + fake `IEmailSender` |
| Integration | Throttle: 2nd request inside the window issues no token, sends no mail, and is byte-identical to the 1st response | same |
| Integration | Confirm: valid token sets the hash and the new password signs in; replay ⇒ 401; expired (`expires_at` backdated) ⇒ 401; unknown token ⇒ 401 — all three responses identical; blank body ⇒ 400 **and the token still works afterwards** | same |
| Integration | Renew: correct current password ⇒ 204 and the caller's refreshed cookie still works; wrong current ⇒ 401 and the hash is unchanged | same |
| Integration | Admin: `ManageUsers` holder resets a same-org user ⇒ 204; no `ManageUsers` ⇒ 403; cross-org target ⇒ 404 identical to a nonexistent id; the target's pre-change cookie stops authenticating | same, two seeded orgs |
| Integration | Session invalidation: a cookie captured before any of the three change paths is rejected on the next request; a cookie with no `session_ver` claim is rejected | same |
| Integration | RLS: cross-org token INSERT rejected; unscoped UPDATE that does not set `consumed_at` rejected; `0005` idempotent on re-apply | Extend `MigrationRlsTests` |
| Frontend | Forgot screen renders the same confirmation for any input; reset screen reads `?token=`, posts it, and scrubs the URL on success; renew screen posts both fields | Vitest + Testing Library, existing `*.test.tsx` convention |

## Threat Matrix

| Native row | Applicability |
|---|---|
| Documentation-like paths | N/A — no file-classification boundary |
| Git repository selection | N/A — no product code runs Git |
| Commit state | N/A — no commit automation added |
| Push state | N/A — `railway.json` untouched |
| PR commands | N/A — no PR automation added |
| Routing | **Applicable** — two new anonymous routes (`/account/reset-password/request`, `/account/reset-password/confirm`) and two new authenticated ones. Safe behavior: the anonymous pair returns a uniform empty-body 202 / generic 401 on every branch, issues single-use 1-hour tokens hashed at rest, throttles by email and IP, and validates input before consuming a token; the authenticated pair goes through `RequireAuthorization` + `TenantScopeEndpointFilter` + a store-loaded actor, never a body-supplied one. RED tests: replay, expiry, unknown token, unknown email, throttle, cross-org admin target, missing permission, stale cookie (see Testing Strategy). |
| Process integration | **Applicable** — one new outbound HTTP integration (Resend). Safe behavior: typed `HttpClient` with a timeout, the API key read from config and never logged or echoed, delivery failure logged and swallowed so it can never change the HTTP response shape, and a log-only sender when the key is absent. RED tests: non-2xx ⇒ `false` and a still-202 endpoint response; no key ⇒ `LogOnlyEmailSender` is what gets resolved. |

No shell or subprocess boundary is introduced.

## Migration / Rollout

Forward-only and additive. Per environment, **before** deploying the new image:
apply `deploy/db/migrations/0005_password_recovery.sql` via `psql` against the
direct (non-pooled) connection, exactly as `deploy/README.md` documents for
`0001`–`0003`. `/health/ready` fails closed until `password_reset_tokens` exists,
so ordering is enforced by the readiness gate rather than by discipline. Then set
`RESEND_API_KEY`, `EMAIL_FROM_ADDRESS` (a Resend-verified sender domain), and
`PUBLIC_BASE_URL` as Railway variables, deploy, and smoke-test one real reset.

**Existing sessions**: every cookie issued before this change lacks the
`session_ver` claim and is rejected at validation — i.e. deploying this signs
everyone out exactly once. That is the correct fail-closed default (the
alternative, treating a missing claim as valid, leaves a permanent bypass) and is
a one-time, self-healing inconvenience. Call it out in the deploy note.

Rollback: revert the commit; the four routes and three screens disappear, sign-in
returns to its prior claim set, and pre-existing cookies work again.
`DROP TABLE password_reset_tokens; ALTER TABLE users DROP COLUMN session_version;`
is optional and has no dependents.

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | `0005` migration + dev init sync + README/runbook + `PostgresPasswordRecoveryStore` + records + `session_version` projection + readiness check + `MigrationRlsTests` | ~230 | `0005` applies twice cleanly; RLS tests green; no behavior change | Revert; drop table/column |
| 2 | `SessionVersionCache` + `SessionVersionValidator` + `session_ver` claim at sign-in + `/account/renew-password` + tests | ~260 | Renew changes the hash; pre-change cookie rejected | Revert (sessions reset once more) |
| 3 | `IEmailSender`/`ResendEmailSender`/`LogOnlyEmailSender`/`EmailOptions` + `ResetRequestThrottle` + request/confirm routes + tests | ~340 | Request/confirm integration + throttle unit tests green | Revert; the table becomes unused, not broken |
| 4 | Admin-forced reset route + SPA (`useResetToken`, three screens, `App.tsx`, `api/account.ts`, `types.ts`) + Vitest | ~320 | `npm run build` + Vitest green; admin integration tests green | Revert |

Decision needed before apply: Yes
Chained PRs recommended: Yes
400-line budget risk: High

**Why chained**: ~1 150 authored lines total, far past the 400-line budget, and
the slices are genuinely independent — Unit 1 is dead schema plus a store nobody
calls yet, Unit 2 proves the session-versioning mechanism through the simplest
password-change path before any anonymous surface exists, Unit 3 adds the
anonymous surface onto an already-proven invalidation mechanism, and Unit 4 is
the admin route plus the SPA. Feature Branch Chain: PR #1 targets the feature
branch; #2 targets #1; #3 targets #2; #4 targets #3. No slice leaves a security
hole open across a merge — the anonymous reset surface (Unit 3) cannot land
before session invalidation (Unit 2) exists.

## Open Questions

- [ ] None blocking. Two accepted assumptions, both inherited from existing
      documented precedent: Cloud.Api runs a single Railway replica (in-memory
      throttle + immediate cache write-through); and `EMAIL_FROM_ADDRESS` must be
      a domain verified in Resend before production mail will deliver — a runbook
      step, not a code decision.
