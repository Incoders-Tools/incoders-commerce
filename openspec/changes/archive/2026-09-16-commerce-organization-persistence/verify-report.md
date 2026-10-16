```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:722f611076c923756f47482f236617942046d5b7a15e3134c79f4a4d48c1680a
verdict: pass
blockers: 0
critical_findings: 0
requirements: 5/5
scenarios: 16/16
test_command: dotnet test Commerce.sln
test_exit_code: 0
test_output_hash: sha256:1b47bae105c1d2a05c723899654fa53504ddb0eaee299772c0d64abb14de36b2
build_command: dotnet build Commerce.sln
build_exit_code: 0
build_output_hash: sha256:731c9c50a7470439233d1a8169e6458ef3b4922900d5814c7a2420db65d03eba
```

# Verification Report: Commerce Organization Persistence (Re-verification)

Change: commerce-organization-persistence
Date: 2026-09-16
Mode: Strict TDD
Overall verdict: PASS

Independently re-verified from a clean state at HEAD 5f34b9f0c6effc627986d9bd22f2c6363bffe0c7 (PR #24 merged into dev, on top of PR #23's a6ff32f), not taken from the apply-phase, the orchestrator spot-check, or the prior FAIL verify pass. This is the final gate before archive; every dimension was re-checked from scratch, not just the previously-missing test.

## Build and Test Evidence (independently executed)

- `docker ps` confirmed `incoders-commerce-postgres-1` and `incoders-commerce-pgbouncer-1` already running before test execution, so all live-Postgres/RLS/store tests ran for real, not soft-skipped.
- `dotnet build Commerce.sln` -> exit 0, Build succeeded, 0 Warnings, 0 Errors.
- `dotnet test Commerce.sln` -> exit 0, 127/127 passed, 0 failed, 0 skipped:
  - Commerce.Bootstrap.Tests: 1 passed
  - Commerce.Upgrade: 19 passed
  - Commerce.Integration: 107 passed
  - Total: 1 + 19 + 107 = 127, matching the expected 126 baseline + 1 new.
- `dotnet test Commerce.sln --filter "FullyQualifiedName~CallerSuppliedBranchName"` -> 1/1 passed in isolation. Read the test body directly (`OrganizationStoreTests.TryCreateBootstrapAsync_CallerSuppliedBranchName_PersistsExactly`): it calls `TryCreateBootstrapAsync` with a distinct, non-default branch name ("Downtown Branch"), then opens a separate owner-role `NpgsqlConnection` and runs `SELECT name FROM branches WHERE id = $1` directly against the persisted row, asserting the column equals "Downtown Branch" AND is not equal to "Main". This genuinely reads the persisted `branches.name` column for a caller-supplied value and would fail if the implementation silently ignored `BranchName` or hardcoded the default -- it is a real, load-bearing regression test, not a shallow assertion.
- `dotnet test Commerce.sln --filter "FullyQualifiedName~AdminInsertFailsAfterOrgAndBranchAlreadyInserted"` -> 1/1 passed in isolation, re-confirming the load-bearing atomicity/rollback test still passes standalone (not just as part of the full suite).
- `npm test` (Vitest, `src/Commerce.Web`) -> 6/6 passed.
- `npm run build` (`src/Commerce.Web`) -> clean, 0 errors.
- Full Playwright E2E recipe executed for real per `src/Commerce.Web/README.md`: built the SPA (`npm run test:e2e:build-backend-spa`), copied to `Commerce.Cloud.Api/wwwroot`, ran `dotnet run` for Cloud.Api in Development, fronted with `local-ssl-proxy` on port 5443, ran `npm run test:e2e` -> 6/6 passed, including both genuine cross-branch `catalog.spec.ts` tests (denied: different organization's branch; allowed: own branch). Background processes (`dotnet run`, `local-ssl-proxy`) were killed by PID afterward and `wwwroot` was reset to `.gitkeep`-only (`git status` confirms zero diff in `wwwroot`).

## RLS Independent Re-verification (live psql against incoders-commerce-postgres-1)

Directly queried `\d organizations` / `\d branches` against the live `commerce_dev` database (not read from any prior report), using the container's actual credentials (`commerce_owner`/`commerce_dev`):

```
organizations: Policies (forced row security enabled):
  POLICY "organizations_tenant_isolation"
    USING ((id = (NULLIF(current_setting('app.current_org_id', true), ''))::uuid))
    WITH CHECK ((id = (NULLIF(current_setting('app.current_org_id', true), ''))::uuid))

branches: Policies (forced row security enabled):
  POLICY "branches_tenant_isolation"
    USING ((organization_id = (NULLIF(current_setting('app.current_org_id', true), ''))::uuid))
    WITH CHECK ((organization_id = (NULLIF(current_setting('app.current_org_id', true), ''))::uuid))

branches_organization_id_fkey: FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE CASCADE
```

Both tables have FORCE RLS. `organizations`' policy compares `id` (the organization row IS the tenant). `branches`' policy is symmetric on its own `organization_id`. The FK is real, with `ON DELETE CASCADE`, matching the prior verify pass's findings exactly -- no regression.

## Fix Commit Re-verification (45d2dd3)

- `tests/Commerce.Integration/OrganizationStoreTests.cs`: added `TryCreateBootstrapAsync_CallerSuppliedBranchName_PersistsExactly`, closing the exact CRITICAL gap from the prior verify pass. No production code was changed -- the test passes immediately against the existing implementation, consistent with the prior finding that the implementation was already correct by inspection and only the test was missing.
- `src/Commerce.Web/README.md`: read in full. The "Catalog rename" and "Known limitation" sections no longer describe the old "every admin permanently denied" / empty-branch-scope world; they now correctly state that a real bootstrap admin gets a real, non-empty branch scope and can pass catalog authorization for their own branch, and that `TestSeedEndpoints.cs`'s remaining role is only to create a second branch/user for the cross-branch denial case. This closes the prior verify pass's WARNING.

## Spec-to-Implementation Spot Check

- `deploy/db/migrations/0003_organizations_branches.sql` and `deploy/dev/db/init-rls.sql` unchanged since the prior pass (fix commit touched only the test file and the README).
- `src/Commerce.Cloud.Api/Persistence/PostgresOrganizationStore.cs` unchanged: transaction composition (`set_config` -> org-exists guard -> zero-users guard -> INSERT organizations -> INSERT branches -> `userStore.InsertAsync` (same tx) -> COMMIT) re-confirmed via `git diff` showing zero changes in this PR.
- `Endpoints/Account.cs`'s `branchName = string.IsNullOrWhiteSpace(request.BranchName) ? "Main" : request.BranchName.Trim()` path is now verified end-to-end by the new test for the non-default case (previously only the default-name path had a covering test).

## Out-of-Scope Files -- Confirmed Untouched (across PR #23 + PR #24 combined)

`git diff --stat 51ad701..5f34b9f` against `src/Commerce.Application/Access/TenantAuthorizationService.cs`, `src/Commerce.Application/Access/InstallationIdentityService.cs`, and `src/Commerce.Pos.Windows` produced zero diff output across the entire combined change range. `git log --follow` confirms `TenantAuthorizationService.cs` was last touched in `commerce-foundation` (commit `abd06ac`), well before this change. All three remain exactly as documented in `tasks.md`'s "Notes (not tasks)" section as required-untouched, for both PR #23 and PR #24.

## Task Completion vs. Code State

All 34 tasks in `tasks.md` are marked `[x]` (re-confirmed via grep: 34 checked, 0 unchecked). No task content changed between the prior pass and this one; only the test suite and README gained the fix commit's additions.

## Spec Compliance Matrix (16/16 scenarios, re-verified)

| Spec | Requirement | Scenario | Test | Result |
|---|---|---|---|---|
| organization-persistence | Persisted Organization and Branch Storage | Organization and branch rows are isolated by organization | MigrationRlsTests cross-org isolation test for organizations/branches (0003) | COMPLIANT |
| organization-persistence | Persisted Organization and Branch Storage | Branch is scoped to its organization | OrganizationStoreTests.TryCreateBootstrapAsync_HappyPath + MigrationRlsTests WITH-CHECK violating insert | COMPLIANT |
| organization-persistence | Transactional Organization Bootstrap Creation | Successful bootstrap creates organization, branch, and admin together | OrganizationStoreTests.TryCreateBootstrapAsync_HappyPath_CreatesOrganizationBranchAndAdmin_SharingOneOrganizationId | COMPLIANT |
| organization-persistence | Transactional Organization Bootstrap Creation | Failure during bootstrap leaves no partial rows | OrganizationStoreTests.TryCreateBootstrapAsync_AdminInsertFailsAfterOrgAndBranchAlreadyInserted_RollsBackEverything (re-run in isolation, passes) | COMPLIANT |
| organization-persistence | Transactional Organization Bootstrap Creation | Bootstrap rejected when the organization already exists | OrganizationStoreTests.TryCreateBootstrapAsync_OrganizationAlreadyExists_RollsBack_ZeroNewRows | COMPLIANT |
| organization-persistence | Branch Name Defaulting | Branch created with a caller-supplied name | OrganizationStoreTests.TryCreateBootstrapAsync_CallerSuppliedBranchName_PersistsExactly (NEW, re-run in isolation, passes, genuinely reads branches.name) | COMPLIANT |
| organization-persistence | Branch Name Defaulting | Branch created with the default name | AccountEndpointTests.Bootstrap_OmittedBranchName_CreatesBranchNamed_Main | COMPLIANT |
| user-credentials | Per-Organization First-Admin Bootstrap | Bootstrap creates the first admin | AccountEndpointTests.Bootstrap_ValidToken_CreatesAdmin_WithFullPermissions_AndReturnsIds | COMPLIANT |
| user-credentials | Per-Organization First-Admin Bootstrap | Bootstrap rejected when an admin already exists | AccountEndpointTests.Bootstrap_RequestToken_Returns409_WhenOrgAlreadyHasUsers | COMPLIANT |
| user-credentials | Per-Organization First-Admin Bootstrap | Bootstrap rejected when the organization is already persisted | AccountEndpointTests.Bootstrap_SecondBootstrapOnSameOrganization_Returns409 | COMPLIANT |
| user-credentials | Per-Organization First-Admin Bootstrap | Bootstrap token cannot be reused | AccountEndpointTests.Bootstrap_ReplayedToken_Returns401 | COMPLIANT |
| user-credentials | Per-Organization First-Admin Bootstrap | Bootstrap token is scoped to one organization | AccountEndpointTests.Bootstrap_WrongOrgToken_Returns401 | COMPLIANT |
| user-credentials | Per-Organization First-Admin Bootstrap | Bootstrap branch defaults to "Main" when unnamed | AccountEndpointTests.Bootstrap_OmittedBranchName_CreatesBranchNamed_Main | COMPLIANT |
| tenant-access-foundation | Organization and Branch Isolation | Authorized branch access | AccountEndpointTests.Bootstrap_ThenCatalogRename (allowed leg) + Playwright catalog rename allowed | COMPLIANT |
| tenant-access-foundation | Organization and Branch Isolation | Cross-branch and cross-organization denial | AccountEndpointTests.Bootstrap_ThenCatalogRename (denied leg) + Playwright catalog rename denied | COMPLIANT |
| tenant-access-foundation | Organization and Branch Isolation | Bootstrapped admin passes branch containment against a real branch | AccountEndpointTests.Bootstrap_ThenCatalogRename_OnCreatedBranch_IsAllowed_OnOtherBranch_IsNotFound | COMPLIANT |

Compliance summary: 16/16 scenarios compliant. The previously CRITICAL "Branch created with a caller-supplied name" scenario is now genuinely covered.

## Correctness (Static Evidence)

| Requirement | Status | Notes |
|---|---|---|
| Persisted Organization and Branch Storage | Implemented | FORCE RLS both tables, real FK, confirmed via live psql |
| Transactional Organization Bootstrap Creation | Implemented | Single NpgsqlTransaction, load-bearing rollback test passes in isolation |
| Branch Name Defaulting | Implemented and fully tested | Both default and caller-supplied paths now have covering, passing tests |
| Per-Organization First-Admin Bootstrap (delta) | Implemented | Real branch_scope seeding confirmed |
| Organization and Branch Isolation (delta) | Implemented | End-to-end HTTP + Playwright proof of branch-scoped authorization against a real persisted branch |

## Coherence (Design)

| Decision | Followed? | Notes |
|---|---|---|
| PostgresOrganizationStore owns the single transaction | Yes | Unchanged since prior pass, re-confirmed via diff |
| branches gets its own symmetric policy on organization_id | Yes | Confirmed via live psql this pass |
| branches -> organizations FK with ON DELETE CASCADE | Yes | Confirmed via live psql this pass |
| TenantAuthorizationService.cs, InstallationIdentityService.cs, Commerce.Pos.Windows untouched | Yes | Confirmed via git diff across the full PR #23 + PR #24 range |

## Issues Found

CRITICAL: None.

WARNING: None. (The prior WARNING on stale README content was closed by commit 45d2dd3.)

SUGGESTION:
1. apply-progress's Strict-TDD evidence remains narrative rather than a literal "TDD Cycle Evidence" table; this is a pre-existing format gap noted in the prior verify pass, not a substantive one, and does not block archive.

## Verdict

PASS

Build is clean (0 warnings, 0 errors) and all 127 xUnit tests pass live against a running Postgres/pgbouncer stack with no soft-skips (126 baseline + 1 new), including the load-bearing atomicity test and the new caller-supplied-branch-name test both re-run and passing in isolation; the SPA Vitest suite (6/6), npm run build, and the full Playwright E2E recipe (6/6, including both genuine cross-branch catalog.spec.ts tests) all pass independently; RLS shapes and the branches -> organizations FK were independently re-confirmed via live psql; src/Commerce.Web/README.md no longer describes the stale pre-change world; all out-of-scope files were confirmed untouched via git diff across the full combined PR range; and all 16 spec scenarios (5 requirements) now have genuinely covering, passing tests, closing the exact CRITICAL gap the prior verify pass found. Ready for archive.
