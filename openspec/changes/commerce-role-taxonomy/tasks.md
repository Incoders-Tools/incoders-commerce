# Tasks: Commerce Role Taxonomy

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~1240 (design's own estimate: Unit 1 ~230, Unit 2 ~280, Unit 3 ~330, Unit 4 ~400) |
| Effective review budget (session default) | 1500 changed lines |
| 400-line budget risk (vs. skill default) | High |
| Budget risk vs. the 1500-line session budget | Medium — estimate lands under budget with modest headroom |
| Chained PRs recommended | No (delivery strategy is `single-pr`; design's own 4-PR chain recommendation is preserved as in-PR task/commit ordering instead) |
| Suggested split | Single PR, internally ordered so Unit 1 → Unit 2 → Unit 3 → Unit 4 land as sequential, individually-green commit groups |
| Delivery strategy | single-pr |
| Chain strategy | size-exception |

Decision needed before apply: Yes
Chained PRs recommended: No
Chain strategy: size-exception
400-line budget risk: High

**Note on the two budgets**: the skill's literal guard line reports risk against its own 400-line default (High — ~1240 lines exceeds it). The session set a 1500-line review budget, under which the estimate fits with `single-pr`. This breakdown does not diverge materially from the design's estimate — no basis to project past 1500; flag before merge if actual authored lines approach the ceiling.

**Hard security ordering preserved as task order, not PR order**: Phase 2 (audit path + platform/audit-table privilege tests) MUST be green before Phase 3 (org-scoped endpoints) begins, and Phase 4 (the platform-admin plane — second cookie scheme, `platform_readonly` login) MUST land last, after Phases 1–3 are merged-quality. This mirrors the design's Work Unit 1→2→3→4 dependency exactly: no slice may leave a security hole open across a commit, and the highest-risk reviewable (a second auth scheme and database login) must arrive against an already-audited, already-tested base.

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | `RoleCatalog` + `RoleGrantPolicy` + `0006` rename migration (Phase 1) | PR 1 (size:exception, single PR) | `dotnet test tests/Commerce.Cloud --filter FullyQualifiedName~RoleCatalog\|FullyQualifiedName~RoleGrantPolicy` and `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~MigrationRlsTests` | Apply `0006_role_taxonomy.sql` twice against `deploy/dev/compose.yaml`; seeded `"admin"` user renamed and still signs in | Revert; run `0006`'s inverse block (names swapped) |
| 2 | `0007` (`platform_admins` + `audit_log`) + `AuditLogWriter` + readiness check + RLS/privilege tests (Phase 2) — must be green before Unit 3 | PR 1 (same PR, later commits) | `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~MigrationRlsTests\|FullyQualifiedName~AuditLogWriter` | Apply `0007_platform_administration.sql` twice; run the privilege-error assertions against direct connections | Revert; `DROP TABLE platform_admins, audit_log` (no dependents yet) |
| 3 | `POST /account/users` + `PUT .../roles` + in-transaction audit (Phase 3) — depends on Unit 2 | PR 1 (same PR, later commits) | `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~RoleTaxonomyTests` | Two seeded orgs; a `business-admin` creates a `seller`, that seller signs in | Revert; tables become unused, not broken |
| 4 | Platform-admin plane: `PlatformAdmin.cs`, `PostgresPlatformAdminStore`, second cookie scheme, `platform_readonly` datasource, `Program.cs` wiring, genesis/sign-in/list/bootstrap endpoints (Phase 4) — lands last | PR 1 (same PR, final commits) | `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~PlatformAdminTests` | Genesis token pair via `railway logs`-equivalent stdout; sign in; list two orgs; bootstrap a third | Revert; drop the `platform_readonly` role and policy |

## Phase 1: Role Catalog, Grant Policy, Rename Migration (Unit 1)

- [x] 1.1 RED: `RoleCatalogTests` — every canonical name resolves to its exact `Permission` set; `provider` is `None`; unknown name fails `TryResolve`; `OrgAssignable` excludes `platform-admin`.
- [x] 1.2 GREEN: create `src/Commerce.Domain/Identity/RoleCatalog.cs` (`FrozenDictionary`, `TryResolve`, `OrgAssignable`).
- [x] 1.3 RED: `RoleGrantPolicyTests` — subset grant allowed; superset denied `ExceedsCallerPermissions`; `platform-admin` denied `ReservedRole` even for an all-flags caller; unknown name denied `UnknownRole`; empty list allowed.
- [x] 1.4 GREEN: create `src/Commerce.Domain/Identity/RoleGrantPolicy.cs` (`TryAuthorize`, pure, no I/O).
- [x] 1.5 GREEN: update `Account.cs` bootstrap literal `"admin"` → `RoleCatalog.BusinessAdmin`.
- [x] 1.6 RED: `MigrationRlsTests` case — a pre-seeded `"admin"` user is renamed to `"business-admin"` with a byte-identical permission set, still signs in, and a second `0006` run is a no-op (matches zero rows).
- [x] 1.7 GREEN: create `deploy/db/migrations/0006_role_taxonomy.sql` (`NO FORCE`/`FORCE` RLS toggle, jsonb rewrite, `DO $$ RAISE EXCEPTION` post-condition assertion, transactional).
- [x] 1.8 GREEN: append the same DDL verbatim to `deploy/dev/db/init-rls.sql`.
- [x] 1.9 Confirm 1.6 green; run `dotnet test tests/Commerce.Cloud tests/Commerce.Integration --filter FullyQualifiedName~RoleCatalog|FullyQualifiedName~RoleGrantPolicy|FullyQualifiedName~MigrationRlsTests`. (Deviation: `tests/Commerce.Cloud` does not exist in this repo; unit tests placed in `tests/Commerce.Integration`, the project's single existing test project, matching its established convention for all prior SDD changes.)

## Phase 2: Platform/Audit Tables, Audit Writer, Privilege Tests (Unit 2 — must be green before Phase 3)

- [x] 2.1 RED: `MigrationRlsTests` — `platform_admins` and `audit_log` exist with `FORCE ROW LEVEL SECURITY`; `0007` re-applies cleanly (idempotent `CREATE ... IF NOT EXISTS`).
- [x] 2.2 GREEN: create `deploy/db/migrations/0007_platform_administration.sql` (`platform_admins`, `audit_log`, indexes, RLS enable/force, revokes, asymmetric grants, genesis-only INSERT policy, append-only `audit_log` policy, `platform_readonly` role + column grant + `TO`-scoped `organizations` read policy, `__PLATFORM_READONLY_PASSWORD__` placeholder).
- [x] 2.3 GREEN: append the same DDL verbatim to `deploy/dev/db/init-rls.sql`.
- [x] 2.4 RED: privilege test — `platform_readonly` can `SELECT id, name, created_at` from `organizations` with no `app.current_org_id` set across two seeded orgs, but fails with a privilege error on `users`, `user_directory`, `branches`, `password_reset_tokens`, `device_credentials`, `sync_inbox`, `platform_admins`, `audit_log`, and on any `INSERT`/`UPDATE`/`DELETE` against `organizations`.
- [x] 2.5 RED: privilege test — `app_runtime` cannot `SELECT` from `audit_log`; cannot `UPDATE audit_log`; cannot insert an audit row for another organization; cannot `UPDATE platform_admins.password_hash`.
- [x] 2.6 Confirm 2.4/2.5 pass against the 2.2 DDL (no additional production code — Postgres-enforced).
- [x] 2.7 Create `src/Commerce.Cloud.Api/Auditing/UserManagementAuditEntry.cs` (persisted record shape).
- [x] 2.8 RED: `AuditLogWriterTests` — `InsertAsync` writes exactly one row inside the caller-owned transaction; never calls `Commit`/`Rollback`/`set_config`; a subsequent rollback of the outer transaction leaves no row.
- [x] 2.9 GREEN: create `src/Commerce.Cloud.Api/Auditing/AuditLogWriter.cs` (`InsertAsync`, no `RETURNING`).
- [x] 2.10 RED: extend `PostgresReadinessHealthCheck` tests — `/health/ready` fails when `platform_admins`/`audit_log` FORCE-RLS or any expected policy is missing.
- [x] 2.11 GREEN: extend `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` — add both tables + `relforcerowsecurity` + policy names to the single readiness query and both messages.
- [x] 2.12 Confirm Phase 2 green: `dotnet test Commerce.sln` before starting Phase 3. (220/220 passed.)

## Phase 3: Org-Scoped User Creation and Role Assignment (Unit 3 — depends on Phase 2 green)

- [x] 3.1 RED: `RoleTaxonomyTests POST /account/users` — a `business-admin` creates a `seller` in the same organization with exactly the catalog's `ViewSales`-only permission set and that branch scope; the created user signs in — spec: "Catalog permissions are used, not body-supplied ones" / "Business-admin creates a seller in the same organization".
- [x] 3.2 RED: extend — an unknown role name is rejected and no user is persisted — spec: "Unknown role name is rejected".
- [x] 3.3 RED: extend — a Spanish label (`"Vendedor"`) is rejected as unknown, never mapped to `seller` — spec: "Translated role name is rejected".
- [x] 3.4 RED: extend — `provider` is assignable and the created user's effective permissions are `Permission.None` — spec: "Reserved provider role grants no capability".
- [x] 3.5 RED: extend — a branch outside the caller's organization is rejected with 400 and nothing persisted — spec: "Cross-organization branch scope is rejected".
- [x] 3.6 RED: extend — a caller lacking a requested permission is denied `ExceedsCallerPermissions`, no user persisted — spec: "Grant-cap violation is rejected". (Deviation: the design's example permission `RecordSales` does not exist in `Permission`'s 4-flag enum; the test uses `ManageCatalog` instead — same shape, a permission the caller genuinely lacks.)
- [x] 3.7 RED: extend — a caller without `ManageUsers` is rejected 403.
- [x] 3.8 RED: extend — a successful create writes exactly one `audit_log` row (actor, entity, org, action, timestamp, new value) inside the same transaction — spec: "Audit row is written atomically with the action".
- [x] 3.9 GREEN: add `CreateUserRequest`/`CreateUserResponse` records and `POST /account/users` in `Account.cs` (`RoleGrantPolicy.TryAuthorize` before any I/O, branch-subset check, one tx: `set_config` → insert → `AuditLogWriter.InsertAsync` → commit).
- [x] 3.10 GREEN: add `PostgresUserAccountStore.CreateStaffUserAsync` (owns the transaction).
- [x] 3.11 RED: `RoleTaxonomyTests PUT /account/users/{userId}/roles` — a role change persists and the user's `EffectivePermissions` reflect it.
- [x] 3.12 RED: extend — a cross-org target resolves to 404, identical to a nonexistent id — spec: "Actor identity is loaded from the persisted store" (tenant-access-foundation).
- [x] 3.13 RED: extend — a `business-admin` holding every flag is still denied `platform-admin` grants (`ReservedRole`), checked before the subset math — spec: "Org-scoped caller cannot grant platform-admin".
- [x] 3.14 RED: extend — the audit row for a role change records the target's prior role names as old value and the requested names as new value — spec: "Audit row is written atomically with the action" (roles case).
- [x] 3.15 GREEN: add `AssignRolesRequest` record and `PUT /account/users/{userId}/roles` in `Account.cs`.
- [x] 3.16 GREEN: add `PostgresUserAccountStore.ReplaceRolesAsync` and `ListRoleNamesAsync` (one tx: update + audit).
- [x] 3.17 RED: force a unique-constraint violation mid-transaction to prove the mutation and its audit row roll back together, leaving neither persisted — spec: "Failed audit write rolls back the action" / "No orphaned audit row without a corresponding action". (Deviation: implemented as a direct RLS-policy violation on the audit insert — `audit_log_append`'s `WITH CHECK` — inside a manually-driven transaction, mirroring `AuditLogWriterTests`' pattern; a real Postgres unique-constraint violation on `users`/`user_directory` during `CreateStaffUserAsync` is caught BEFORE the audit insert is ever attempted, so it cannot exercise "audit write fails, mutation rolls back" — only the RLS-violation path can.)
- [x] 3.18 Confirm Phase 3 green: `dotnet test Commerce.sln` before starting Phase 4. (233/233 passed — 220 baseline + 13 new `RoleTaxonomyTests`, zero regressions.)

## Phase 4: Platform Administration Plane (Unit 4 — lands last, after Phases 1–3 are merged-quality)

- [x] 4.1 Create `src/Commerce.Domain/Identity/PlatformAdmin.cs` (`Id`, `Email`; no `OrganizationId`).
- [x] 4.2 RED: `PostgresPlatformAdminStoreTests` — `FindByEmailAsync` is unscoped; `TryCreateGenesisAsync` succeeds once and rejects a second admin (`NOT EXISTS` policy) even called directly against the store; `TouchLastSignInAsync` updates only `last_sign_in_at_utc`; `ListOrganizationsAsync` (via `platform_readonly` datasource) returns two seeded organizations.
- [x] 4.3 GREEN: create `src/Commerce.Cloud.Api/Persistence/PostgresPlatformAdminStore.cs`.
- [x] 4.4 GREEN: add `CloudAuthenticationSchemes.PlatformAdminCookie` const in `DeviceBearerAuthenticationHandler.cs`.
- [x] 4.5 RED: `PlatformAdminTests` — an org cookie on `/platform/*` gets 401; a platform cookie on `/account/users` gets 401; the platform cookie is never sent to `/account` (`Cookie.Path`); a platform identity carries no `org_id` claim — spec: "Platform-Admin Scheme Isolation" (both scenarios) / "Business-admin cannot reach a platform-admin endpoint".
- [x] 4.6 GREEN: wire `Program.cs` — second `AddCookie(PlatformAdminCookie, Cookie.Name="commerce.platform", Cookie.Path="/platform")`; `PlatformAdmin` policy (`AddAuthenticationSchemes(PlatformAdminCookie).RequireAuthenticatedUser()`); `PasswordHasher<PlatformAdmin>` registration; keyed platform-read `NpgsqlDataSource` conditional on `ConnectionStrings:CommercePlatformRead`; `PostgresPlatformAdminStore` DI. (Deviation, additive: `Events.OnRedirectToLogin`/`OnRedirectToAccessDenied` overridden on the new `PlatformAdminCookie` scheme ONLY, returning 401/403 status codes instead of the framework's default HTML-login-page 302 redirect — required for an API-only scheme with no login page to satisfy the spec's literal 401/403 scenarios; the pre-existing default org cookie scheme is untouched.)
- [x] 4.7 RED: extend — a known platform admin signs in and the issued cookie carries no `org_id` claim; an unknown email, wrong password, and an org-scoped `UserAccount`'s credentials all return one generic 401 with dummy-hash timing parity — spec: "Successful platform-admin sign-in" / "Org-scoped credentials do not authenticate as platform-admin".
- [x] 4.8 RED: extend — the genesis token pair (`request-token` + `bootstrap`) succeeds once, mirroring `/account/bootstrap`'s log-only flow; a second genesis attempt is rejected by the `NOT EXISTS` policy even when called directly.
- [x] 4.9 RED: extend — list-organizations returns both seeded orgs with no `app.current_org_id` set; a missing `CommercePlatformRead` connection string returns 503 and never falls back to `app_runtime` — spec: "Platform admin lists organizations across tenants".
- [x] 4.10 RED: extend — bootstrap-organization creates an organization, branch, and `business-admin` in one transaction, the created admin signs in; the audit row names the platform actor and the new org; the acted-upon `organizationId` is server-minted, never claim-derived — spec: "Platform admin bootstraps a new organization" / "Platform-admin action targets an explicit organization id" / "Bootstrap action is audited".
- [x] 4.11 GREEN: create `src/Commerce.Cloud.Api/Endpoints/PlatformAdmin.cs` (`/platform` group: bootstrap/request-token, bootstrap genesis, sign-in, sign-out, `GET /platform/organizations`, `POST /platform/organizations`).
- [x] 4.12 GREEN: extend `PostgresOrganizationStore.TryCreateBootstrapAsync` to accept an optional `UserManagementAuditEntry`, written inside its existing transaction; SQL otherwise unchanged.
- [x] 4.13 GREEN: register `MapPlatformAdminEndpoints` in `Program.cs`.
- [x] 4.14 Confirm all Phase 4 RED tests (4.5, 4.7–4.10) are green. (15/15 new Phase 4 tests green: 5 `PostgresPlatformAdminStoreTests` + 10 `PlatformAdminTests`.)
- [x] 4.15 Update `deploy/README.md` — `0006`/`0007` apply sections, `__PLATFORM_READONLY_PASSWORD__` handling, `ConnectionStrings__CommercePlatformRead`, platform-genesis runbook, `/account/bootstrap` deprecation note.
- [x] 4.16 Update `deploy/staging-runbook.md` with the new per-environment variable.

## Phase 5: Full Verification and Cleanup

- [x] 5.1 Apply `0006_role_taxonomy.sql` then `0007_platform_administration.sql` twice each against `deploy/dev/compose.yaml`, confirming idempotency. (Verified directly via `psql` against the running compose container: `0006`'s second run matches `UPDATE 0` — no `"admin"` entries remain — and does not raise the post-condition assertion; `0007`'s second run re-applies cleanly with no errors. Also exercised implicitly dozens of times across every new/existing test class's migration-apply helper in this session.)
- [x] 5.2 Run `dotnet test Commerce.sln`, confirming all existing tests plus `RoleCatalogTests`, `RoleGrantPolicyTests`, `RoleTaxonomyTests`, `PlatformAdminTests`, and extended `MigrationRlsTests`/`PostgresReadinessHealthCheckTests` pass. (248/248 passed: 1 Commerce.Bootstrap.Tests + 19 Commerce.Upgrade + 228 Commerce.Integration.)
- [x] 5.3 Confirm `src/Commerce.Domain/Ordering/CustomerOrderingAccess.cs` is byte-unchanged (regression guard — explicitly not a role). (Confirmed via `git status`/`git diff --stat`: zero changes to that file.)
- [x] 5.4 Confirm no web/SPA files were touched — proposal's Out of Scope excludes web UI for user management and platform-admin (API-only slice); no `npm run test` task is required for this change. (Confirmed via `git status`/`git diff --stat`: zero changes under `src/Commerce.Web`.)
