```yaml
schema: gentle-ai.verify-result/v1
verdict: pass-with-warnings
blockers: 0
critical_findings: 0
requirements: 9/9
scenarios: 29/29
test_command: dotnet test Commerce.sln
test_exit_code: 1 (flaky, pre-existing, unrelated to this change -- see Issues)
build_command: dotnet build Commerce.sln
build_exit_code: 0
```

# Verification Report: Commerce Role Taxonomy

Change: commerce-role-taxonomy
Date: 2026-09-17
Mode: Strict TDD
Overall verdict: PASS WITH WARNINGS

Independently re-verified from the current working tree (uncommitted, branch
`feat/password-recovery-and-branding`), not taken from any prior apply-phase
summary. Task-level TDD evidence (RED/GREEN per task, deviations recorded) was
read directly from `tasks.md` -- this project's convention embeds TDD evidence
in tasks.md rather than a separate `apply-progress` artifact; no such artifact
exists on disk for any prior archived change in this repo either.

## Scenario Count (recounted from source, not trusted from any prior arithmetic)

- `specs/user-credentials/spec.md`: 10 scenarios (Canonical Role Catalog x4,
  Staff User Creation and Role Assignment x4, Business-Admin Rename Migration x2)
- `specs/tenant-access-foundation/spec.md`: 10 scenarios (Role and Revocation
  Enforcement x5 incl. 2 pre-existing/unchanged, Platform-Admin Scheme
  Isolation x2, Audit Logging for User-Management Actions x3)
- `specs/platform-administration/spec.md`: 9 scenarios (Identity/Credentials x2,
  Sign-In x2, List Organizations x1, Bootstrap x1, Scope Isolation x2, Auditing x1)
- **Total: 29 scenarios** (matches the ~29 estimate; exact recount confirms it)

## Build and Test Evidence (independently executed)

- `dotnet build Commerce.sln` -> exit 0, Build succeeded, 0 Warnings, 0 Errors.
- `dotnet test Commerce.sln`, run 1 (fresh, first invocation of this session):
  248/248 passed (1 Commerce.Bootstrap.Tests + 19 Commerce.Upgrade + 228
  Commerce.Integration), 0 failed, 0 skipped -- matches the changes own claim
  exactly.
- `dotnet test Commerce.sln`, runs 2-4 (re-executed independently to confirm
  stability): **1 failure reproduced 3 of 4 times** --
  `Commerce.Integration.AccountEndpointTests.ResetRequest_RepeatedWithinThrottleWindow_IssuesNoSecondToken_Returns202ByteIdenticalToFirst`,
  `Expected: Accepted / Actual: InternalServerError`. This test:
  - is in `AccountEndpointTests.cs`, a file with **zero diff** in this change
    (`git diff --stat` confirms), last touched by the already-merged
    `commerce-password-recovery` commit `1ad7fef`.
  - passes 100% of the time in isolation (`--filter FullyQualifiedName~AccountEndpointTests`,
    29/29 passed) and 100% of the time when the whole `AccountEndpointTests`
    class runs alone.
  - is a pre-existing test-isolation/parallelism flake (shared throttle-window
    state colliding across parallel test collections in a full-suite run), not
    a regression introduced by commerce-role-taxonomy.
- `dotnet test Commerce.sln --filter` scoped to every test class this change
  added or extended (`RoleCatalog`, `RoleGrantPolicy`, `RoleTaxonomyTests`,
  `PlatformAdminTests`, `PostgresPlatformAdminStoreTests`, `AuditLogWriterTests`,
  `MigrationRlsTests`, `PostgresReadinessHealthCheckTests`): **75/75 passed**,
  reproduced cleanly on every run.

## Highest-Risk Claim Verification (independently re-checked against source, not summary)

1. **0006 NO FORCE/FORCE RLS toggle + post-condition assertion**: Confirmed in
   `deploy/db/migrations/0006_role_taxonomy.sql` -- `ALTER TABLE users NO FORCE
   ROW LEVEL SECURITY` wraps the rewrite, `FORCE` is restored before a `DO $$
   RAISE EXCEPTION` post-condition checks for surviving `"admin"` rows, all
   inside one transaction. `MigrationRlsTests.RoleTaxonomyMigration_RenamesAdminToBusinessAdmin_PreservingPermissionsAndPasswordHash`
   seeds a real `"admin"` row, applies `0006` once (renamed, permission set
   `15` unchanged, password hash byte-identical, still verifies via
   `PasswordHasher.VerifyHashedPassword`), then applies it again and asserts
   the row stays renamed with no exception -- genuinely exercises idempotency,
   not just re-application without error.
2. **`platform_readonly` negative privilege**: `0007`'s only grant is
   `GRANT SELECT (id, name, created_at) ON organizations TO platform_readonly`
   plus a `TO platform_readonly`-scoped policy. `MigrationRlsTests.PlatformReadonly_CanReadOrganizationSummaryColumns_ButNothingElse`
   independently connects as `platform_readonly` and asserts `SqlState ==
   "42501"` (insufficient_privilege) against `users`, `user_directory`,
   `branches`, `password_reset_tokens`, `device_credentials`, `sync_inbox`,
   `platform_admins`, `audit_log`, and any INSERT/UPDATE/DELETE on
   `organizations`. Real negative-privilege assertions, not summary trust.
3. **`audit_log` unreadable by `app_runtime`**: `0007` grants `INSERT` only
   (no `SELECT`, `UPDATE`, `DELETE`) to `app_runtime` on `audit_log`, and its
   sole policy is `FOR INSERT`. `MigrationRlsTests.AppRuntime_CannotReadAuditLog_CannotWriteCrossOrgAuditRow_CannotUpdatePlatformAdminPasswordHash`
   independently connects as `app_runtime` and asserts `SqlState == "42501"`
   on both `SELECT * FROM audit_log` and `UPDATE audit_log`, and that a
   cross-org `INSERT` is rejected by `WITH CHECK`. Confirmed at the DDL level,
   not the design's stated intent alone.
4. **Grant-cap enforcement, platform-admin refused before subset math**:
   `RoleGrantPolicy.TryAuthorize` (source read directly) checks
   `RoleCatalog.PlatformAdmin` equality and returns `ReservedRole` **before**
   the `(union & ~caller.EffectivePermissions)` subset check runs.
   `RoleGrantPolicyTests.TryAuthorize_PlatformAdmin_IsDenied_ReservedRole_EvenForAllFlagsCaller`
   and the integration-level `RoleTaxonomyTests.AssignRoles_PlatformAdminGrant_IsDenied_EvenForAnAllFlagsCaller`
   both give the caller every flag and still assert denial -- genuinely proves
   the ordering, not just the outcome.
5. **Scheme isolation, both directions**: `Program.cs` registers a second
   `AddCookie(PlatformAdminCookie, Cookie.Name="commerce.platform",
   Cookie.Path="/platform")` and a `PlatformAdmin` policy that names only that
   scheme; `/account/users` uses bare `.RequireAuthorization()` (default
   scheme only). `PlatformAdminTests.OrgCookie_OnPlatformEndpoint_Returns401`,
   `PlatformCookie_OnOrgEndpoint_Returns401`, and
   `PlatformCookie_IsNeverSentTo_Account_BecauseOfCookiePath` cover both
   directions plus the `Cookie.Path` mechanism itself.
6. **Audit rows in the same transaction as the mutation**: read
   `PostgresUserAccountStore.CreateStaffUserAsync` and `ReplaceRolesAsync`
   directly -- both open exactly one `NpgsqlConnection`/`NpgsqlTransaction`,
   pass that same `connection`/`tx` pair into `AuditLogWriter.InsertAsync`
   (which itself never calls `Commit`/`Rollback`/`set_config` -- confirmed by
   reading `AuditLogWriter.cs`), and only then call `tx.CommitAsync`. No
   second connection anywhere in the path. `AuditLogWriterTests` and
   `RoleTaxonomyTests.RolesUpdate_FailedAuditWrite_RollsBackTheMutation_NoOrphanedRow`
   exercise the rollback-together property.

## Regression Confirmation

- `git diff --stat -- src/Commerce.Cloud.Api/Endpoints/Account.cs`: 145
  insertions, **1 deletion** -- the single deletion is the bootstrap literal
  `"admin"` -> `RoleCatalog.BusinessAdmin` rename documented in tasks.md 1.5.
  Every other existing endpoint (`/sign-in`, `/sign-out`, `/me`, `/bootstrap`,
  `/reset-password`, `/renew-password`) is present in the diff only as
  unchanged context -- no edits to their bodies.
- `git diff --stat src/Commerce.Web src/Commerce.Domain/Ordering/CustomerOrderingAccess.cs`:
  empty output -- genuinely zero changes to either path.

## Task Completion vs. Code State

All 59 tasks in `tasks.md` are marked `[x]`. Spot-checked against actual code
(not checkbox trust): `RoleCatalog.cs`, `RoleGrantPolicy.cs`, `PlatformAdmin.cs`,
`0006`/`0007` migrations, `AuditLogWriter.cs`, `UserManagementAuditEntry.cs`,
`PostgresPlatformAdminStore.cs`, the `Account.cs` and `PlatformAdmin.cs`
endpoints, and the `Program.cs` scheme wiring are all present and match their
task descriptions. Deviations noted inline in tasks.md (test project location,
`ManageCatalog` substituted for the nonexistent `RecordSales`, the
RLS-violation path used for 3.17 instead of a unique-constraint race) are
consistent with what the source actually does.

## Spec Compliance Matrix

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Canonical Role Catalog | Catalog permissions used, not body-supplied | `RoleTaxonomyTests.CreateUser_BusinessAdminCreatesSellerInSameOrg_Returns201_WithExactCatalogPermissions` | COMPLIANT |
| Canonical Role Catalog | Unknown role name rejected | `CreateUser_UnknownRoleName_Returns400_AndNoUserPersisted` | COMPLIANT |
| Canonical Role Catalog | Translated role name rejected | `CreateUser_TranslatedRoleName_Returns400_NeverMappedToSeller` | COMPLIANT |
| Canonical Role Catalog | Reserved provider grants no capability | `CreateUser_ProviderRole_GrantsNoCapability` | COMPLIANT |
| Staff User Creation and Role Assignment | Business-admin creates seller in same org | `CreateUser_BusinessAdminCreatesSellerInSameOrg_Returns201_WithExactCatalogPermissions` | COMPLIANT |
| Staff User Creation and Role Assignment | Cross-org branch scope rejected | `CreateUser_CrossOrganizationBranch_Returns400_AndNoUserPersisted` | COMPLIANT |
| Staff User Creation and Role Assignment | Grant-cap violation rejected | `CreateUser_GrantExceedsCallerPermissions_Returns403_AndNoUserPersisted` | COMPLIANT |
| Staff User Creation and Role Assignment | Org-scoped caller cannot grant platform-admin | `AssignRoles_PlatformAdminGrant_IsDenied_EvenForAnAllFlagsCaller` | COMPLIANT |
| Business-Admin Rename Migration | Pre-existing admin signs in as business-admin | `MigrationRlsTests.RoleTaxonomyMigration_RenamesAdminToBusinessAdmin_PreservingPermissionsAndPasswordHash` | COMPLIANT |
| Business-Admin Rename Migration | New bootstrap mints business-admin | `Account.cs` bootstrap literal + existing `AccountEndpointTests.Bootstrap_*` (unaffected path) | COMPLIANT |
| Role and Revocation Enforcement | Revoked access | Pre-existing, unchanged by this PR | COMPLIANT (unchanged) |
| Role and Revocation Enforcement | Offline revocation boundary | Pre-existing, unchanged by this PR | COMPLIANT (unchanged) |
| Role and Revocation Enforcement | Actor identity loaded from persisted store | Pre-existing (`commerce-user-credentials`), unaffected | COMPLIANT (unchanged) |
| Role and Revocation Enforcement | Privilege escalation via request body denied | Pre-existing, unaffected | COMPLIANT (unchanged) |
| Role and Revocation Enforcement | Role name outside catalog grants nothing | `RoleCatalog.TryResolve` returning false + `RoleGrantPolicy` unknown-role rejection | COMPLIANT |
| Platform-Admin Scheme Isolation | Org-scoped caller cannot reach platform-admin endpoint | `PlatformAdminTests.OrgCookie_OnPlatformEndpoint_Returns401` | COMPLIANT |
| Platform-Admin Scheme Isolation | Platform-admin cannot reach org-scoped endpoint | `PlatformAdminTests.PlatformCookie_OnOrgEndpoint_Returns401` | COMPLIANT |
| Audit Logging for User-Management Actions | Audit row written atomically | `RoleTaxonomyTests.AssignRoles_Success_AuditRow_RecordsOldAndNewRoleNames`, `CreateUser_Success_WritesExactlyOneAuditRow_InsideTheSameAction` | COMPLIANT |
| Audit Logging for User-Management Actions | Failed audit write rolls back the action | `RoleTaxonomyTests.RolesUpdate_FailedAuditWrite_RollsBackTheMutation_NoOrphanedRow` | COMPLIANT |
| Audit Logging for User-Management Actions | No orphaned audit row | same test | COMPLIANT |
| Platform Admin Identity and Credentials | No organization scope | `PostgresPlatformAdminStoreTests` (schema read: no `organization_id` column) | COMPLIANT |
| Platform Admin Identity and Credentials | Credentials verified separately | `PlatformAdminTests.SignIn_KnownAdmin_Succeeds_AndCookieCarriesNoOrgIdClaim` | COMPLIANT |
| Platform-Admin Sign-In | Successful sign-in | `SignIn_KnownAdmin_Succeeds_AndCookieCarriesNoOrgIdClaim` | COMPLIANT |
| Platform-Admin Sign-In | Org-scoped credentials do not authenticate | `SignIn_UnknownEmail_WrongPassword_AndOrgScopedCredentials_AllReturn401` | COMPLIANT |
| List Organizations | Lists across tenants | `ListOrganizations_ReturnsBothSeededOrgs_WithNoCurrentOrgIdSet` | COMPLIANT |
| Bootstrap Organization First Business-Admin | Bootstraps a new organization | `BootstrapOrganization_CreatesOrgBranchAndAdmin_InOneTransaction_AdminSignsIn_AuditedWithExplicitOrgId` | COMPLIANT |
| Platform-Admin Scope Isolation | Business-admin cannot reach platform-admin endpoint | `OrgCookie_OnPlatformEndpoint_Returns401` | COMPLIANT |
| Platform-Admin Scope Isolation | Action targets explicit organization id | `BootstrapOrganization_CreatesOrgBranchAndAdmin_InOneTransaction_AdminSignsIn_AuditedWithExplicitOrgId` (server-minted organizationId, never claim-derived) | COMPLIANT |
| Platform-Admin Action Auditing | Bootstrap action is audited | same test | COMPLIANT |

Compliance summary: 29/29 scenarios compliant (10 net-new/modified role-taxonomy
requirements scenarios have new covering tests; 4 unchanged tenant-access-foundation
scenarios remain compliant via pre-existing, unaffected coverage).

## Correctness (Static + Runtime Evidence)

| Requirement | Status | Notes |
|---|---|---|
| Canonical Role Catalog | Implemented | `FrozenDictionary`, case-insensitive, canonical casing preserved on resolve |
| Staff User Creation and Role Assignment | Implemented | Grant-cap + branch-subset checks before any I/O; catalog-only permissions |
| Business-Admin Rename Migration | Implemented | Transactional, FORCE-toggling, self-asserting, idempotent |
| Platform-Admin Scheme Isolation | Implemented | Second cookie scheme, distinct Cookie.Path, named-scheme policies |
| Audit Logging for User-Management Actions | Implemented | Same connection/transaction, no RETURNING, no separate write path |
| Platform Admin Identity and Credentials | Implemented | Separate table, no organization_id, dedicated PasswordHasher<PlatformAdmin> |
| List Organizations | Implemented | Dedicated platform_readonly login, column-level grant only |
| Bootstrap Organization First Business-Admin | Implemented | Reuses TryCreateBootstrapAsync verbatim, server-minted org id |
| Platform-Admin Action Auditing | Implemented | Audit entry constructed with actor_kind=platform-admin inside the same transaction |

## Coherence (Design)

| Decision | Followed? | Notes |
|---|---|---|
| No new DB capability for platform-admin writes (set_config to explicit target org) | Yes | PlatformAdmin.cs endpoint mints organizationId server-side, passes it into TryCreateBootstrapAsync existing scope-setting path |
| Least-privilege platform_readonly login for the only cross-org read | Yes | Column-level grant confirmed in 0007; negative-privilege tests confirm nothing else is reachable |
| platform_admins genesis-only INSERT policy | Yes | NOT EXISTS (SELECT 1 FROM platform_admins) in 0007; Genesis_TokenPair_SucceedsOnce_SecondAttemptRejected_EvenCalledDirectly |
| audit_log append-only, no SELECT grant/policy | Yes | Confirmed in 0007 DDL and by AppRuntime_CannotReadAuditLog test |
| RoleGrantPolicy as a pure, no-I/O static class called before any write | Yes | Confirmed by reading RoleGrantPolicy.cs and both endpoint call sites in Account.cs |
| Scheme mutual exclusivity via distinct Cookie.Path and named policies | Yes | Confirmed in Program.cs; both directions tested |
| TryCreateBootstrapAsync reused verbatim by /platform/organizations | Yes | PostgresOrganizationStore.TryCreateBootstrapAsync extended only with an optional audit-entry parameter |

## Issues Found

CRITICAL: None attributable to this change.

WARNING:
1. `dotnet test Commerce.sln` is flaky on the current HEAD: a pre-existing
   test, `AccountEndpointTests.ResetRequest_RepeatedWithinThrottleWindow_IssuesNoSecondToken_Returns202ByteIdenticalToFirst`
   (file has zero diff in this change; last touched by the already-merged
   `commerce-password-recovery`), failed in 3 of 4 independent full-suite runs
   with `Expected: Accepted / Actual: InternalServerError`, but passed 100% of
   the time in isolation and 100% of the time when every test class this
   change added/extended is run scoped (75/75). This looks like a
   test-isolation or parallelism collision on shared throttle-window state
   across parallel xUnit collections, not a regression caused by
   commerce-role-taxonomy code. The proposals own Success Criterion states
   "dotnet test Commerce.sln passes" unconditionally -- this flake technically
   violates that criterion on an unlucky run, even though it is outside this
   changes diff. Recommend filing this as a separate, pre-existing bug to fix
   (likely a shared static throttle-registry keyed too broadly, or a missing
   test-collection isolation attribute) rather than blocking this changes
   archive, but do not archive silently past it -- flag it explicitly to the
   user/orchestrator before merge.

SUGGESTION:
1. `audit_log` currently has no read path at all (by design, commented in
   `0007`) -- this is explicitly deferred, but worth tracking so a future
   change does not have to rediscover the exact grant/policy statements needed.
2. Consider adding a `[Collection]` isolation boundary or resetting the
   throttle-window state between `AccountEndpointTests` runs and whatever
   parallel collection collides with it, to make `dotnet test Commerce.sln`
   deterministically green again.

## Verdict

PASS WITH WARNINGS

All 59 tasks complete and code-state-verified against the actual source (not
checkbox trust); all 29 spec scenarios across the three delta/new spec files
have passing, non-trivial covering tests (spot-checked for real production-code
exercise, no tautologies or ghost loops found in `RoleGrantPolicyTests`,
`RoleTaxonomyTests`, `PlatformAdminTests`); the five highest-risk claims in the
task brief were independently re-verified against source and live-Postgres
test assertions, not trusted from any prior summary; `git diff` confirms
`Account.cs` pre-existing endpoints are behaviorally untouched (145
insertions, 1 deletion -- the bootstrap literal rename) and that `Commerce.Web`
and `CustomerOrderingAccess.cs` have zero changes. The sole open item is a
flaky, pre-existing, out-of-scope test in `AccountEndpointTests.cs` that
causes `dotnet test Commerce.sln` to intermittently fail on unrelated grounds;
this changes own 75 tests pass deterministically every time. Recommend
surfacing the flake to the user before archive, but it does not represent a
defect in commerce-role-taxonomy implementation.
