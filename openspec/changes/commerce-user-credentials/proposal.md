# Proposal: Commerce User Credentials

## Intent

`commerce-deployment-orchestration`'s verify-report flagged a WARNING: `Endpoints/Account.cs` sign-in trusts client-submitted `organizationId`/`userId` with **zero password verification**, because no persisted user/credential store exists. `Endpoints/Catalog.cs` has the same privilege-escalation shape — `RenameProductRequest` builds the `UserAccount` from body-supplied `ActorId`/`ActorBranchScope`/`ActorRoles` on the one endpoint that actually calls `TenantAuthorizationService.Authorize`. Anyone can claim any identity, roles, and branch scope. This change adds real Postgres-backed credential persistence and closes both holes.

## Scope

### In Scope
- `users` + `user_roles` (or equivalent) Postgres tables in `deploy/db/migrations/`, following `0001_init_rls.sql` exactly: `IF NOT EXISTS`, ENABLE+FORCE RLS, `app_runtime` REVOKE/GRANT, policy on `organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid`.
- Npgsql credential store matching `PostgresCloudInboxStore.cs`'s style: raw Npgsql, explicit transaction, parameterized `SELECT set_config(..., true)` as first statement. No EF Core.
- `PasswordHasher<TUser>` (`Microsoft.Extensions.Identity.Core`, in the shared framework) for hashing — consistent with ADR-002 without the EF Identity stack.
- Real credential-verifying `/account/sign-in` rewrite.
- `Catalog.cs`: load actor roles/branch scope from the store using the authenticated principal; drop `ActorId`/`ActorBranchScope`/`ActorRoles` from the request body.
- HTTP bootstrap endpoint gated by a one-time setup token (generated + logged at startup when an organization has no users) creating the first admin user. **User-fixed decision**, chosen over CLI-only for remote Railway bootstrap.

### Out of Scope
- Organization/branch persistence — separate, more foundational pre-existing gap. `organization_id`/`branch_id` stay bare `uuid`, no FK, mirroring `sync_inbox`'s existing precedent.
- A persisted Role/Permission catalog — roles stay code-defined `(string Name, Permission Permissions)`.
- User-management UI/CRUD (list, edit, deactivate).
- `TenantAuthorizationService`, `TenantScopeResolver`/`CloudTenantScope` (already correct), `Ordering.cs`, `Sync.cs`, `Commerce.Pos.Windows` (installation-bound identity).

## Capabilities

### New Capabilities
- `user-credentials`: persisted user accounts, password hashing/verification, sign-in, and first-admin bootstrap.

### Modified Capabilities
- `tenant-access-foundation`: actor identity, roles, and branch scope MUST derive from the authenticated principal and persisted store — never from request payloads.

## Approach

Additive persistence slice. One migration, one Npgsql store, hasher wiring, two endpoint rewrites, one bootstrap endpoint. Sign-in looks up by organization + username, verifies the hash, then stamps the existing `org_id` claim path unchanged. `Catalog.cs` swaps body-trust for a store lookup; `TenantAuthorizationService` is untouched.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `deploy/db/migrations/` | New | `users`/`user_roles` with RLS |
| `src/Commerce.Cloud.Api/Persistence/` | New | Npgsql credential store |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modified | Real verification + bootstrap |
| `src/Commerce.Cloud.Api/Endpoints/Catalog.cs` | Modified | Actor from store, not body |
| `src/Commerce.Cloud.Api/Program.cs` | Modified | Hasher + store DI |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Org-id typo at bootstrap creates an orphan user, undetectable | Medium | **Accepted, documented** — needs org persistence (out of scope) |
| Bootstrap endpoint widens attack surface | Medium | One-time use, expiry, only when org has zero users; user-accepted tradeoff |
| `PasswordHasher<TUser>` may need a package reference | Low | One-line build spike before design finalizes |
| Removing `RenameProductRequest` fields breaks SPA/tests | Medium | Update callers in the same unit |

## Rollback Plan

Revert the commit: endpoints return to their prior shape and the store/DI wiring disappears. Migration is forward-only and additive — `DROP TABLE user_roles, users;` removes it with no dependents (no FK points at them). Existing tests are untouched by the schema.

## Dependencies

- Reachable Postgres with the `app_runtime` role (already provisioned by `commerce-deployment-orchestration`).

## Success Criteria

- [ ] Sign-in with a wrong password returns 401; correct password issues the cookie.
- [ ] `Catalog.cs` rename cannot be escalated by editing the request body; actor comes from the store.
- [ ] Bootstrap endpoint creates the first admin only with a valid token and only when the org has zero users; the token cannot be reused.
- [ ] Cross-org user lookup returns nothing (RLS verified).
- [ ] All existing xUnit tests pass.

## Proposal question round

Fixed by the user: HTTP-token bootstrap over CLI-only. Open for review before design: (a) token lifetime and whether it is per-organization or per-host; (b) username vs. email as the sign-in identifier; (c) whether roles go in a join table or a `jsonb`/array column; (d) whether a deactivated-user flag belongs in this slice or in the deferred user-management work.
