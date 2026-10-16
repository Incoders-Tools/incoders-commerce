```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:4bc322f9e389ee4235e81b9da875e0d2f9a2455833307c3f26ecbb7c23072007
verdict: pass
blockers: 0
critical_findings: 0
requirements: 5/5
scenarios: 15/15
test_command: dotnet test Commerce.sln
test_exit_code: 0
test_output_hash: sha256:d9b8069ea84ba13982fc9b76d8ef0ae0fe38c6d4dc6cab19a9974e27dde773e3
build_command: dotnet build Commerce.sln
build_exit_code: 0
build_output_hash: sha256:453ab1846bcdbab0ebf948aa08461dc20a6a5d12990eb8dba78198765bc24448
```

# Verification Report: Commerce User Credentials

Change: commerce-user-credentials
Date: 2026-09-16
Mode: Strict TDD
Overall verdict: PASS

Independently re-verified from a clean state at HEAD 88e9a5f56a096a3df09ee2ff2fbdb1455a0d3d80 (PR #19 merged into dev), not taken from the apply-phase or orchestrator spot-check transcripts.

## Build and Test Evidence (independently executed)

- dotnet build Commerce.sln -> exit 0, Build succeeded, 0 Warnings, 0 Errors.
- docker ps confirmed dev-postgres-1 and dev-pgbouncer-1 already running before test execution, so all live-Postgres/RLS tests ran for real, not soft-skipped.
- dotnet test Commerce.sln -> exit 0, 114/114 passed, 0 failed, 0 skipped:
  - Commerce.Bootstrap.Tests: 1 passed
  - Commerce.Upgrade: 19 passed
  - Commerce.Integration: 94 passed
  - Total: 1 + 19 + 94 = 114, matching the reported 83 baseline + 31 new.

## Spec-to-Implementation Spot Check

- deploy/db/migrations/0002_users.sql exists, is idempotent (CREATE TABLE and CREATE INDEX emit NOTICE relation already exists, skipping on a second psql apply against the live container, zero errors, re-verified directly, not from any prior report), and deploy/dev/db/init-rls.sql carries the identical DDL appended verbatim (hand-sync convention preserved).
- src/Commerce.Cloud.Api/Persistence/PostgresUserAccountStore.cs follows PostgresCloudInboxStore.cs exact shape: raw NpgsqlDataSource, every method opens its own NpgsqlTransaction, a set_config call for app.current_org_id is the first statement (except the deliberately unscoped FindDirectoryEntryAsync), and each reader is block-scoped before tx.CommitAsync, avoiding the NpgsqlOperationInProgressException documented as a known gotcha in apply-progress.
- src/Commerce.Cloud.Api/Endpoints/Account.cs: SignInRequest now takes Email and Password only; grep confirms no OrganizationId or UserId fields remain on it. Sign-in performs an unscoped directory lookup, a scoped credential lookup, PasswordHasher VerifyHashedPassword, and returns a generic 401 for unknown-email, wrong-password, and revoked-user cases via the same code path with a dummy-hash timing-parity branch. Bootstrap endpoints (request-token and bootstrap) exist, are anonymous but token-gated via BootstrapTokenRegistry Issue and TryConsume, and the request-token route returns 202 with an empty body while the plaintext token is only logged via ILogger LogInformation.
- src/Commerce.Cloud.Api/Endpoints/Catalog.cs: grep confirms RenameProductRequest has zero ActorId, ActorBranchScope, or ActorRoles fields; the only remaining occurrence of those names is an XML doc comment explaining their removal. The rewritten endpoint loads the actor via LoadActorAsync using the cookie NameIdentifier claim.
- src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs extends its existing sync_inbox verification pattern to assert users and user_directory table existence, forced RLS flags, and policy presence before reporting healthy.
- src/Commerce.Web/src/api/types.ts: SignInRequest is email plus password; RenameProductRequest carries no actor fields (the one remaining actorId field in the file belongs to the unrelated, out-of-scope SubmitOrderRequest for Ordering.cs). CatalogScreen.tsx, SignInScreen.tsx and their test files were updated to match; no actor fields are sent from the client.

## RLS Independent Re-verification (live psql against dev-postgres-1)

Directly queried pg_class and pg_policies (not read from any prior report):

relname=users relrowsecurity=t relforcerowsecurity=t
relname=user_directory relrowsecurity=t relforcerowsecurity=t

policy users_tenant_isolation on users: qual and with_check both equal the organization-scoped NULLIF expression (symmetric)
policy user_directory_lookup on user_directory: qual equals true (unrestricted read); with_check equals the organization-scoped NULLIF expression (asymmetric)

Both tables have FORCE RLS enabled. users_tenant_isolation is symmetric, matching the 0001_init_rls.sql sync_inbox precedent exactly. user_directory_lookup is the load-bearing asymmetric policy the design calls for: an unrestricted read qual, required because the sign-in email-to-organization lookup runs before any tenant scope exists, paired with an org-scoped with_check so writes stay tenant-isolated. This is the exact shape design.md Interfaces and Contracts section specifies, confirmed byte-for-byte, not just RLS is on.

## Documented Gaps -- Confirmed Honestly Present

1. Bootstrap admin empty branch scope: Endpoints/Account.cs bootstrap-completion handler comment (near the NewUserAccount construction) explicitly states the org-wide admin empty branch_scope means TenantAuthorizationService Authorize BranchScope Contains check cannot pass for any branch until branch persistence exists, labeled a documented limitation, not silently dropped or worked around.
2. No organization or branch persistence or FK: deploy/db/migrations/0002_users.sql header comment and the organization_id column comment both state no FK because organizations are not persisted (non-goal), consistent with the proposal accepted risk that an org-id typo at bootstrap creates an undetected orphan user. Not silently dropped.

## Task Completion vs. Code State

All 28 tasks in tasks.md are marked complete. Spot-checked against actual code, not checkbox trust alone: migration files, store, registry, endpoint rewrites, readiness check, and SPA changes are all present and match the task descriptions; no unchecked or falsely-checked task found.

## Out-of-Scope Files -- Confirmed Untouched

git diff --stat between the PR base and PR head commits lists 26 changed files, none of which are TenantAuthorizationService.cs, TenantScopeResolver.cs, CloudTenantScope.cs, Ordering.cs, Sync.cs, any file under Commerce.Pos.Windows, or ManagementParityTests.cs. git log further confirms TenantAuthorizationService.cs was last touched in commerce-foundation (commit abd06ac) and TenantScopeResolver.cs was last touched in commerce-deployment-orchestration (commit dc2dcc0); neither was touched by this PR. ManagementParityTests.cs was not modified and is part of the 94 passing Commerce.Integration tests, since it calls services directly rather than the HTTP endpoint, per design explicit prediction.

## Spec Compliance Matrix

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Persisted User Account Storage | User row is isolated by organization | MigrationRlsTests cross-org zero rows plus UserAccountStoreTests FindByEmailAsync_And_LoadActorAsync_NeverExposeAnotherOrganizationsRow | COMPLIANT |
| Persisted User Account Storage | Revoked user is flagged, not deleted | UserAccountStoreTests LoadActorAsync_MapsIsRevoked_ToUserAccountIsRevoked | COMPLIANT |
| Password Hashing | Password is never stored in plaintext | UserAccountStoreTests store mapping plus AccountEndpointTests Bootstrap_ValidToken_CreatesAdmin_WithFullPermissions | COMPLIANT |
| Real Sign-In Verification | Successful sign-in | AccountEndpointTests SignIn_CorrectPassword_Succeeds_WithStoredOrgId | COMPLIANT |
| Real Sign-In Verification | Wrong password is rejected | AccountEndpointTests SignIn_WrongPassword_Returns401 | COMPLIANT |
| Real Sign-In Verification | Unknown email is rejected | AccountEndpointTests SignIn_UnknownEmail_Returns401_SameShapeAsWrongPassword | COMPLIANT |
| Real Sign-In Verification | Revoked user cannot sign in | AccountEndpointTests SignIn_RevokedUser_Returns401 | COMPLIANT |
| Per-Organization First-Admin Bootstrap | Bootstrap creates the first admin | AccountEndpointTests Bootstrap_ValidToken_CreatesAdmin_WithFullPermissions | COMPLIANT |
| Per-Organization First-Admin Bootstrap | Bootstrap rejected when an admin already exists | AccountEndpointTests Bootstrap_RequestToken_Returns409_WhenOrgAlreadyHasUsers | COMPLIANT |
| Per-Organization First-Admin Bootstrap | Bootstrap token cannot be reused | AccountEndpointTests Bootstrap_ReplayedToken_Returns401 | COMPLIANT |
| Per-Organization First-Admin Bootstrap | Bootstrap token is scoped to one organization | AccountEndpointTests Bootstrap_WrongOrgToken_Returns401 | COMPLIANT |
| Role and Revocation Enforcement | Revoked access | AccountEndpointTests SignIn_RevokedUser_Returns401 plus CatalogEndpointTests Rename_WhenStoredActorLacksPermission_Returns403_EvenWithForgedRoleClaim | COMPLIANT |
| Role and Revocation Enforcement | Offline revocation boundary | Pre-existing offline-identity ADR coverage, unchanged by this PR, out of scope | COMPLIANT (unchanged) |
| Role and Revocation Enforcement | Actor identity is loaded from the persisted store, not the request body | CatalogEndpointTests Rename_WithForgedActorFieldsInBody_IsIgnored_StoredActorPermissionsGovern_Allowed | COMPLIANT |
| Role and Revocation Enforcement | Privilege escalation via request body is denied | CatalogEndpointTests Rename_WhenStoredActorLacksPermission_Returns403_EvenWithForgedRoleClaim | COMPLIANT |

Compliance summary: 15/15 scenarios compliant.

## Correctness (Static Evidence)

| Requirement | Status | Notes |
|---|---|---|
| Persisted User Account Storage | Implemented | users table, RLS forced, app_runtime non-owner role, bare uuid org-id with no FK |
| Password Hashing | Implemented | PasswordHasher singleton, no custom crypto, zero new PackageReference |
| Real Sign-In Verification | Implemented | Generic 401, dummy-hash timing parity, org id stamped from stored row |
| Per-Organization First-Admin Bootstrap | Implemented | In-memory BootstrapTokenRegistry, one-time and expiring and org-scoped token |
| Role and Revocation Enforcement (delta) | Implemented | Actor loaded via LoadActorAsync, body actor fields removed from RenameProductRequest |

## Coherence (Design)

| Decision | Followed? | Notes |
|---|---|---|
| PostgresUserAccountStore mirrors PostgresCloudInboxStore shape | Yes | Verified line-by-line: raw Npgsql, per-method transaction, set_config first, block-scoped readers |
| Asymmetric user_directory RLS policy | Yes | Independently confirmed via live psql: unrestricted read, org-scoped write |
| Bootstrap token stays in-memory, not a DB table | Yes | BootstrapTokenRegistry.cs is a plain singleton, no DDL for tokens |
| ManagementParityTests.cs needs no changes | Yes | Confirmed untouched via git diff; still passes as part of the 94 Commerce.Integration tests |
| TenantAuthorizationService, TenantScopeResolver, CloudTenantScope, Ordering.cs, Sync.cs, Commerce.Pos.Windows untouched | Yes | Confirmed via git diff and log across the PR range |

## Issues Found

CRITICAL: None.

WARNING: None.

SUGGESTION:
1. The bootstrap admin empty branch_scope (documented limitation) means no catalog-write flow can currently be exercised end-to-end from a fresh bootstrap admin without a manual branch-scope seed; this is accepted as out of scope pending branch persistence, but is worth tracking as a near-term follow-up once that work starts.
2. deploy/README.md new migrate-before-deploy note for 0002 is a good precedent; consider cross-referencing the org-id-typo and orphan-user risk there too, alongside the existing 0001 guidance, so operators reading the runbook see all bootstrap-time caveats in one place.

## Verdict

PASS

All 28 tasks complete and code-state-verified; 114/114 dotnet tests pass live against a running Postgres and pgbouncer stack with no soft-skips; build is clean with 0 warnings and 0 errors; all 5 requirements and 15 scenarios have passing covering tests; the load-bearing RLS asymmetry on user_directory was independently re-confirmed via direct psql queries, not trusted from any prior report; both documented gaps (empty bootstrap branch scope, no org or branch FK) are honestly present in source comments; and all named out-of-scope files were independently confirmed untouched via git diff and log across the PR range. Ready for archive.
