# Proposal: Commerce Password Recovery

## Intent

A user who forgets their password is permanently locked out. `user-credentials` persists hashed passwords and verifies sign-in, but nothing can ever change a password after creation — confirmed: `Account.cs` exposes only sign-in, sign-out, `/me`, and the two bootstrap endpoints. There is no self-service reset, no authenticated "change my password", and no admin escape hatch (`ManageUsers` exists in `Permission.cs` but no user-management endpoint consumes it). Today the only recovery is manual database surgery.

## Scope

### In Scope
- **Forgot-password reset** (anonymous): request endpoint + confirm endpoint, single-use, per-user token, **1-hour expiry**.
- **Renew password** (authenticated): change a *known* password by supplying current + new password. Distinct flow, no token, no enumeration surface.
- **Real transactional email delivery via Resend** for the reset link — new external dependency, new secret (`RESEND_API_KEY`), new runbook entry.
- **Admin-forced reset**: a `ManageUsers` holder can force-reset another user's password within the same organization. New minimal endpoint; does not attempt the wider user-management CRUD surface.
- **Session invalidation on password change**: both reset-confirm and renew (and admin-forced reset) invalidate the user's existing Identity cookie session(s), forcing re-authentication.
- Persisted per-user reset tokens (hash-at-rest, single-use, expiry), replacing no existing construct — `BootstrapTokenRegistry` is org-keyed and in-memory, so it is a pattern to mirror, not to extend.
- Anti-abuse on the request endpoint: per-email **and** per-IP cooldown derived from the token table's own timestamps (default judgment call, not separately asked — flagging here for visibility). No precedent exists anywhere in the repo, so this is designed fresh.
- Web screens for both flows, plugged into `App.tsx`'s manual view switch (no router exists).
- Delta requirements added to `openspec/specs/user-credentials/spec.md`.

### Out of Scope
- Generic API-wide rate limiting (only this endpoint's cooldown is in scope), self-service registration, MFA, password-strength policy changes, wider user-management CRUD (admin-forced reset is the one narrow exception pulled in above).

## Decisions (confirmed by the user)

| Decision | Answer |
|---|---|
| Reset delivery channel | Real transactional email in this change, via **Resend** |
| Admin-forced reset | In this slice |
| Token lifetime | 1 hour |
| Session behaviour after reset/renewal | Invalidate existing sessions |

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `user-credentials`: add forgot-password reset (request/confirm, single-use expiring token, no account enumeration, real email delivery), authenticated password renewal, admin-forced reset, and session invalidation on any password change.

## Approach

Additive slice on the existing credentials domain. Postgres-backed reset-token table following the `user_directory` asymmetric-RLS shape (lookup happens before the org is known), migration `0005_*`, `/health/ready` extended. Endpoints mirror the confirmed three-tier response discipline: `ValidationProblem` for malformed input, empty-body 202 for "never reveal existence", generic 401 for any security-sensitive failure, with dummy-hash timing parity as sign-in already does. Email sent via Resend's HTTP API (no SMTP), triggered from the confirm-token issuance path; admin-forced reset reuses the same token/email path rather than a separate mechanism. Session invalidation needs a session-versioning or cookie-generation stamp on `UserAccount`, checked at cookie-auth validation time, since no revocation mechanism exists today.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modified | Reset request/confirm, renew, admin-forced-reset endpoints |
| `src/Commerce.Cloud.Api/Authentication/` | New | Per-user reset token store; session-invalidation check on cookie validation |
| `src/Commerce.Cloud.Api/Email/` | New | Resend client wrapper for the reset email |
| `deploy/db/migrations/0005_*.sql` | New | Reset-token table with RLS; session-version column on user table |
| `deploy/README.md`, `Program.cs` | Modified | Migration section, readiness check, `RESEND_API_KEY` secret |
| `src/Commerce.Web/src/screens/`, `api/account.ts` | New/Modified | Forgot/reset/renew screens, client calls |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Reset endpoint enables enumeration or spam | Medium | 202 + timing parity + per-email/per-IP cooldown; verified by scenario |
| No rate-limiting precedent to copy | Medium | Design phase owns it explicitly |
| Resend delivery failure leaves the user stuck with no fallback | Medium | Log the failure server-side; surface a generic "try again later" to the caller, never leak whether the email exists |
| Session-invalidation stamp touches the sign-in/cookie-validation hot path | Medium | Design phase specifies the exact check to avoid an extra DB round-trip per request if avoidable |
| Admin-forced reset without full user-management CRUD may feel like a narrow, easy-to-miss surface | Low | Scoped explicitly to reset-only, documented as such, not a general admin-users endpoint |

## Rollback Plan

Revert the commit: endpoints and screens disappear, sign-in is untouched. The migration is additive and forward-only — `DROP TABLE`/dropped column removes it with no dependents.

## Dependencies

- Resend account + `RESEND_API_KEY` secret (user-provided, added to deploy runbook).

## Success Criteria

- [ ] A user with a valid token sets a new password and signs in with it, via a link received by real email (Resend).
- [ ] The same token cannot be used twice, and a token older than 1 hour is rejected.
- [ ] Requesting a reset for an unknown email is indistinguishable from a known one.
- [ ] An authenticated user renews their password only with the correct current password.
- [ ] A `ManageUsers` holder can force-reset another user's password within their own organization; cannot target another organization's user.
- [ ] After a reset, renewal, or admin-forced reset, the user's prior session cookie no longer authenticates.
- [ ] Repeated reset requests for one email or from one IP are throttled.
- [ ] `dotnet test Commerce.sln` passes.
