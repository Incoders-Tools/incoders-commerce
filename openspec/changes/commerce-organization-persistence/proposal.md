# Proposal: Organization and Branch Persistence

## Intent

`Organization` and `Branch` exist as domain types with zero persistence — no code anywhere constructs them. Consequence: `/account/bootstrap` creates the first admin with `branch_scope = []`, so a freshly bootstrapped admin signs in but can never pass `TenantAuthorizationService`'s branch-containment check for catalog management. `commerce-user-credentials` documented this as a known limitation; `0002_users.sql` carries the inline comment "no FK: organizations are not persisted (non-goal)". This change fulfills that deferred promise and the standing ADR-002 commitment that tenancy roots live in Postgres under RLS.

## Scope

### In Scope
- `deploy/db/migrations/0003_organizations_branches.sql`: `organizations` + `branches` tables (branches FK to organizations), FORCE RLS, `app_runtime` role, `current_setting('app.current_org_id')` policy pattern — identical to `users`/`sync_inbox`.
- New Postgres store in `src/Commerce.Cloud.Api/Persistence/` (raw Npgsql, explicit transaction, `set_config` first statement) mirroring `PostgresUserAccountStore`/`PostgresCloudInboxStore`. No EF.
- `Endpoints/Account.cs` bootstrap: transactionally create the organization + one default branch alongside the first admin, seeding `branch_scope` with that real branch id.
- Hand-sync `deploy/dev/db/init-rls.sql` per existing convention.

### Out of Scope
- Organization/branch listing, editing, or management endpoints/UI.
- Creating a second branch for an organization (one branch, at bootstrap, only).
- `InstallationIdentityService` / `LocalInstallationStore` random-Guid self-mint (next logical follow-up).
- Any superadmin/ops-actor concept.
- Retrofitting FKs onto `users` / `sync_inbox` / `user_directory`.

## Capabilities

### New Capabilities
- `organization-persistence`: persisted, RLS-scoped organizations and branches as tenancy roots.

### Modified Capabilities
- `user-credentials`: bootstrap now creates the organization and its first branch transactionally and seeds a real `branch_scope`.
- `tenant-access-foundation`: branch-containment checks now resolve against a persisted branch.

## Approach

Authorization model is fixed: whoever redeems the existing stdout-delivered bootstrap token implicitly creates the organization. No new trust concept. `TenantAuthorizationService` is unchanged — its existing check works once `branch_scope` holds a real id.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `deploy/db/migrations/0003_organizations_branches.sql` | New | Tables + RLS policies |
| `deploy/dev/db/init-rls.sql` | Modified | Hand-synced mirror |
| `src/Commerce.Cloud.Api/Persistence/` | New | Org/branch Npgsql store |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modified | Transactional org+branch+admin |
| `src/Commerce.Domain/Tenancy/{Organization,Branch}.cs` | Unchanged/minor | Read models |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Future FK retrofit onto `users`/`sync_inbox` breaks on non-matching legacy rows | Low now (dev-only data) | Document for the retrofit owner; consider `NOT VALID` + `VALIDATE` then |
| POS `LocalInstallationStore` still self-mints random org/branch ids | Certain | Explicit documented follow-up; cloud-side journey unaffected |
| Identity source for `organizations.id` undecided (fresh Guid vs. slug) | Med | Design phase decides; default is fresh Guid, matching the rest of the system |
| Partial bootstrap leaving an org without admin | Low | Single transaction covering org, branch, and user |

## Rollback Plan

Revert the `Account.cs` and store commits; drop `organizations`/`branches` (no other table references them). Pre-existing rows are unaffected because no FK is added to existing tables. Reverted bootstrap returns to `branch_scope = []`.

## Dependencies

- `commerce-user-credentials` (archived) — bootstrap flow and `PostgresUserAccountStore` transaction pattern.

## Success Criteria

- [ ] Bootstrap persists an organization row and one branch row.
- [ ] Bootstrapped admin's `branch_scope` contains the created branch id.
- [ ] That admin passes `TenantAuthorizationService` for catalog management on its branch.
- [ ] Cross-organization reads of `organizations`/`branches` are blocked by RLS.
- [ ] Failed bootstrap leaves no partial org/branch/user rows.
