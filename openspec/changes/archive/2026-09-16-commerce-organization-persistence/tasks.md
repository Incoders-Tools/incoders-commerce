# Tasks: Organization and Branch Persistence

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~750 (design estimate: ~90 SQL, ~130 org store, ~40 user-store refactor, ~55 endpoint, ~25 readiness, ~60 seam+E2E, ~350 tests) |
| 400-line budget risk | High |
| Chained PRs recommended | No |
| Suggested split | Single PR (`size:exception`) — DDL and the transaction that writes it must review together |
| Delivery strategy | exception-ok |
| Chain strategy | size-exception |

Decision needed before apply: No
Chained PRs recommended: No
Chain strategy: size-exception
400-line budget risk: High

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Schema + store + endpoint + seam + all tests (single reviewable transaction slice) | PR 1 | `dotnet test tests/Commerce.Integration --filter "FullyQualifiedName~OrganizationStoreTests|FullyQualifiedName~AccountEndpointTests|FullyQualifiedName~MigrationRlsTests"` | `deploy/dev/compose.yaml` live Postgres + `npx playwright test catalog.spec.ts` | Revert the single commit; `DROP TABLE branches, organizations;` (no other table references them) |

## Phase 1: Schema (Foundation)

- [x] 1.1 Create `deploy/db/migrations/0003_organizations_branches.sql`: `organizations`(id, name, created_at) and `branches`(id, organization_id FK→organizations.id ON DELETE CASCADE, name, created_at), unique index on `(organization_id, name)`, FORCE RLS + `app_runtime` GRANTs on both tables per design's exact DDL.
- [x] 1.2 Add `organizations_tenant_isolation` policy comparing `id` (not `organization_id`) and `branches_tenant_isolation` policy on `organization_id`, both using the `NULLIF(current_setting('app.current_org_id', true), '')::uuid` pooler-safety shape from `users_tenant_isolation`.
- [x] 1.3 Confirm (record the check, don't skip it) that `0003` contains zero statements touching `users`, `user_directory`, or `sync_inbox` and is additive-only against existing dev DB state.
- [x] 1.4 Hand-sync the same DDL verbatim into `deploy/dev/db/init-rls.sql` per existing convention.

## Phase 2: Persistence Layer

- [x] 2.1 RED: extend `tests/Commerce.Integration/MigrationRlsTests.cs` with cross-org read denial and unscoped fail-closed cases for `organizations`/`branches`, plus a `WITH CHECK`-violating branch insert throwing, and a "`0003` applies twice cleanly" idempotency case.
- [x] 2.2 GREEN: apply `0003` in the test fixture (`ResolveOrganizationsMigrationPath`/`ApplyOrganizationsMigration` mirroring the `0002` helpers) until 2.1 passes.
- [x] 2.3 Refactor `PostgresUserAccountStore.cs`: extract `internal Task InsertAsync(NpgsqlConnection, NpgsqlTransaction, CloudTenantScope, NewUserAccount, CancellationToken)` carrying the exact `users` + `user_directory` insert bodies from `TryCreateAsync`, with zero `set_config`/tx-lifecycle logic. `TryCreateAsync` becomes a thin wrapper (open → begin → `SetTenantScopeAsync` → zero-users guard → `InsertAsync` → commit).
- [x] 2.4 Verify `tests/Commerce.Integration/UserAccountStoreTests.cs` (or equivalent existing suite) passes unchanged — proof the refactor is behavior-preserving.
- [x] 2.5 Create `src/Commerce.Cloud.Api/Persistence/OrganizationRecords.cs`: `NewOrganization(Guid Id, string Name)`, `NewBranch(Guid Id, string Name)`, `BootstrapOutcome` enum (`Created`, `OrganizationAlreadyExists`, `OrganizationAlreadyHasUsers`, `EmailAlreadyRegistered`).
- [x] 2.6 RED: create `tests/Commerce.Integration/OrganizationStoreTests.cs` — happy path (org+branch+admin created, single organization id shared across rows).
- [x] 2.7 RED: add the org-already-exists rejection case to 2.6 — asserts zero new rows.
- [x] 2.8 RED: add the zero-users-guard rejection case (org already has a user) — asserts zero new rows.
- [x] 2.9 RED (load-bearing atomicity test — do not write a shallow guard-only version): add the case where org+branch inserts have already run and only the admin insert fails (force via a `user_directory` email already registered to a *different* org); assert NO orphaned `organizations`/`branches` row and zero `users`/`user_directory` rows remain.
- [x] 2.10 GREEN: create `src/Commerce.Cloud.Api/Persistence/PostgresOrganizationStore.cs` with `TryCreateBootstrapAsync(CloudTenantScope, NewOrganization, NewBranch, NewUserAccount, CancellationToken)` owning one connection + one `NpgsqlTransaction`: `set_config` → org-exists guard → zero-users guard → `INSERT organizations` → `INSERT branches` → `userStore.InsertAsync(conn, tx, scope, admin, ct)` (same transaction) → `COMMIT`. No explicit rollback code beyond letting exceptions propagate and disposing the undisposed transaction.
- [x] 2.11 Run 2.6–2.9 to GREEN against `PostgresOrganizationStore`.
- [x] 2.12 REFACTOR: review `PostgresOrganizationStore.cs` and `PostgresUserAccountStore.cs` for duplication/clarity while keeping all Phase 2 tests green.

## Phase 3: Endpoint Wiring

- [x] 3.1 Modify `Endpoints/Account.cs`: `BootstrapRequest` gains required `OrganizationName` and optional `BranchName` (defaults to `"Main"`, trimmed); add `BootstrapResponse(Guid OrganizationId, Guid BranchId, Guid UserId)`.
- [x] 3.2 Validate `OrganizationName` non-blank → 400 `ValidationProblem` **before** `registry.TryConsume`, so a validation mistake does not burn the token.
- [x] 3.3 Keep `registry.TryConsume` before the transaction (per design's explicit risk #1); add a code comment documenting that a failed bootstrap after this point burns the token by design.
- [x] 3.4 Delegate the write to `PostgresOrganizationStore.TryCreateBootstrapAsync`, seeding the admin's `branch_scope` with `[branchId]`; delete the stale "empty branch scope" comment block.
- [x] 3.5 Register `PostgresOrganizationStore` in `Program.cs` (DI, injecting `PostgresUserAccountStore`).
- [x] 3.6 Extend `HealthChecks/PostgresReadinessHealthCheck.cs` to verify `organizations`/`branches` table existence and FORCE-RLS/policy state; update the remediation message to name `0003`.
- [x] 3.7 RED: extend `tests/Commerce.Integration/AccountEndpointTests.cs` — bootstrap returns `{organizationId, branchId, userId}`; second bootstrap on same org → 409; blank `organizationName` → 400 with token still consumable; omitted `branchName` creates branch `"Main"`.
- [x] 3.8 RED: add the end-to-end authorization proof to `AccountEndpointTests.cs` — bootstrap → sign in → `LoadActorAsync` → `actor.BranchScope` contains the created `branchId` → `TenantAuthorizationService.Authorize` for a catalog-rename request targeting that branch returns allowed; a different branch id returns not-found.
- [x] 3.9 GREEN: run 3.7–3.8 against the Phase 3 endpoint changes until passing.
- [x] 3.10 Verify `src/Commerce.Web/src/api/types.ts` — confirm whether any SPA type mirrors `BootstrapRequest`; update only if found (SPA does not call bootstrap today, expected no-op).

## Phase 4: Test Seam Narrowing

- [x] 4.1 Modify `Endpoints/TestSeedEndpoints.cs`: remove `branchScope` from `TestSeedUserRequest`; route seeding through `PostgresOrganizationStore.TryCreateBootstrapAsync` (the same path the real endpoint uses); return the real generated `branchId` in `TestSeedUserResponse`. Rewrite the docblock to drop claims this change makes false — the seam's only remaining privilege is skipping the stdout token hop.
- [x] 4.2 Modify `src/Commerce.Web/e2e/helpers.ts`: `seedUser` drops `branchScope`, returns `branchId`; rewrite its comment.
- [x] 4.3 Modify `src/Commerce.Web/e2e/catalog.spec.ts`: "allowed" test uses the real `branchId`; convert the "denied" test to a genuine cross-branch denial (two seeded users/branches) instead of relying on an empty-scope artifact; rewrite both doc comments.
- [x] 4.4 Run the full Playwright suite (`npx playwright test`) and confirm `catalog.spec.ts` passes against the narrowed seam.

## Phase 5: Full Regression and Documentation

- [x] 5.1 Run the full xUnit suite (existing 114 tests + new tests) and confirm all green.
- [x] 5.2 Run `npm run build` and Vitest in `src/Commerce.Web` and confirm green.
- [x] 5.3 Update `deploy/README.md` if it enumerates migrations by number, to list `0003`.
- [x] 5.4 Add a tasks-artifact note (not a task): `InstallationIdentityService`/`LocalInstallationStore` random-Guid self-mint and `TenantAuthorizationService.cs` remain explicitly untouched/out of scope, flagged as a known follow-up, not a defect to fix here.

## Notes (not tasks)

- `TenantAuthorizationService.cs`, `InstallationIdentityService.cs`, `Commerce.Pos.Windows`/`LocalInstallationStore.cs` require zero code change and must stay untouched.
- POS `LocalInstallationStore` still self-mints random org/branch ids — explicit documented follow-up, out of scope here.
- No FK retrofit onto `users`/`sync_inbox`/`user_directory` in this change (deliberate scope boundary per design).
