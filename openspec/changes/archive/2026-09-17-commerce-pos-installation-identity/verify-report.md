```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:6c4e5f967cf270d3728e8335d11c71951d4d3e59a35c5c6dc1ba3b717198e998
verdict: pass
blockers: 0
critical_findings: 0
requirements: 8/8
scenarios: 21/21
test_command: dotnet test Commerce.sln
test_exit_code: 0
test_output_hash: sha256:e416844233e73f8dadb4549b2e313085cbb4cc1185f24f19b3ec68a4f1cd5d20
build_command: dotnet build Commerce.sln --no-incremental
build_exit_code: 0
build_output_hash: sha256:0d8eb1bbb11ed8941a9e630ae67290163122b1018475270b26703a1606c30a54
```

# Verification Report: Commerce POS Installation Identity (Independent Re-verification)

Change: commerce-pos-installation-identity
Date: 2026-09-17
Mode: Strict TDD
Overall verdict: PASS

Independently re-verified from a clean state at HEAD 716a06b7ef5eb1f1ab4179fa36f4905754f556d0 (PR #26 core implementation plus follow-up fixes PR #27, which removed a redundant ProtectedData PackageReference, and PR #28, which fixed the invalid XML comment PR #27 introduced that briefly broke dev's build). This report does not trust any prior report or apply-phase self-assessment; every dimension below was re-executed and re-read from scratch.

## Build and Test Evidence (independently executed)

- `docker ps` confirmed `incoders-commerce-postgres-1` (healthy) and `incoders-commerce-pgbouncer-1` already running before test execution, so all live-Postgres/RLS/store tests ran for real, not soft-skipped.
- `dotnet clean Commerce.sln` followed by `dotnet build Commerce.sln --no-incremental` (a genuine clean rebuild, not an incremental/cached build) -> exit 0, Build succeeded, 0 Warning(s), 0 Error(s). The two NU1510 warnings that PR #27 claimed to fix (redundant ProtectedData PackageReference, already transitively supplied) are genuinely gone on a from-scratch rebuild, not merely reduced or hidden by incremental caching.
- `dotnet test Commerce.sln` -> exit 0, 157/157 passed, 0 failed, 0 skipped:
  - Commerce.Bootstrap.Tests: 1 passed
  - Commerce.Upgrade: 19 passed
  - Commerce.Integration: 137 passed
  - Total: 1 + 19 + 137 = 157, matching the expected 127 baseline + 30 new.
  - One fail-level log line appears mid-run from the health check service (deliberately unreachable-IP SocketException); this is expected diagnostic output from a readiness-health-check test exercising the unreachable-database path on purpose, not a test failure, and the run still reports Test Run Successful for that project.
- `dotnet test Commerce.sln --filter FullyQualifiedName~DeviceCredentialsMigration_UnscopedUpdateToUnrevokeOrRewrite_Throws` -> 1/1 passed in isolation. Read the test body directly (MigrationRlsTests.cs, lines 765-855): it opens a live NpgsqlConnection as the app_runtime role (PostgresTestFixture.DirectConnectionString, not the owner role), inserts one already-revoked row and one live row as the table owner, then attempts (1) an unscoped UPDATE device_credentials SET is_revoked = false against the revoked row inside its own transaction and asserts PostgresException is thrown by Postgres itself before rolling back, and (2) an unscoped UPDATE device_credentials SET branch_id = ... against a live row (a field rewrite that would not also revoke the row) and again asserts PostgresException. Both are genuine Postgres-level RLS WITH CHECK rejections against a live database row, not application-layer assertions.
- Independently re-ran the structural isolation test filter -> 1/1 passed (PosCompositionRootTests.BranchNodeAssembly_DoesNotReference_PosWindowsAssembly), confirming the sync-vs-sales assembly-graph guarantee via GetReferencedAssemblies().
- SPA plus Playwright E2E, executed for real per src/Commerce.Web/README.md: npm test (Vitest, mocked-fetch) -> 6/6 passed; npm run test:e2e:build-backend-spa (tsc + vite build + copy to wwwroot) -> clean; dotnet run for Commerce.Cloud.Api started in Development, /health and /health/ready both returned 200; local-ssl-proxy fronted it on port 5443; npm run test:e2e (Playwright, real browser, real Postgres, real cookie auth) -> 6/6 passed (sign-in.spec.ts x3, ordering.spec.ts x1, catalog.spec.ts x2, including both genuine cross-branch and cross-org cases). Both background processes were killed by PID afterward; wwwroot was reset to gitkeep-only (one accidental deletion of the gitkeep file itself during cleanup was caught and restored via git checkout; git status now shows zero diff in wwwroot). This confirms the device-bearer-shape change genuinely did not touch the cookie-auth SPA path: git diff across the full PR #26, #27, #28 range shows Program.cs gained only two additive lines (registering PostgresDeviceCredentialStore and MapDeviceEndpoints), Endpoints/Account.cs is untouched, and the cookie scheme remains the default authentication scheme.

## RLS Independent Re-verification (live psql against incoders-commerce-postgres-1)

Directly queried pg_policies and pg_class against the live commerce_dev database as commerce_owner (not read from any prior report):

    relrowsecurity | relforcerowsecurity
    t              | t                     (device_credentials: RLS enabled AND forced)

     policyname                | cmd    | permissive | roles    | qual | with_check
     device_credentials_issue  | INSERT | PERMISSIVE | {public} |      | (organization_id = (NULLIF(current_setting(app.current_org_id, true), ))::uuid)
     device_credentials_lookup | SELECT | PERMISSIVE | {public} | true |
     device_credentials_revoke | UPDATE | PERMISSIVE | {public} | true | is_revoked

Exactly three policies, matching the design: SELECT USING (true) with no with_check (unscoped lookup, by design, since the organization is not known until this row resolves it); INSERT WITH CHECK pinned to the current org setting; UPDATE USING (true) WITH CHECK (is_revoked), meaning an unscoped update is permitted only when the resulting row is revoked. FORCE ROW LEVEL SECURITY was confirmed independently, not inferred from row counts.

## Deletion Confirmation

- src/Commerce.Application/Access/InstallationIdentityService.cs and src/Commerce.Domain/Tenancy/Installation.cs are absent from the working tree (directory listings of both folders show no such files).
- A repository-wide search for InstallationIdentityService across src and tests (excluding bin/obj binary artifacts) produces only comments and docs explaining the deletion: DeviceCredentialRecords.cs (doc comment), DeviceCredentialStoreTests.cs (doc comments describing the relocated test), and PosCompositionRootTests.cs (a doc comment plus the live assertion that InstallationIdentityService is no longer registered in the composition root). Zero live source references.
- A repository-wide search for Tenancy.Installation or a class named Installation across src and tests returns zero hits of any kind.

## Device Pairing Flow Spot Check (Endpoints/Device.cs)

Read the file in full. POST /device/pair matches design.md exactly: single-branch operators are auto-issued a credential in one POST (when exactly one branch is in scope, that branch is selected automatically); multi-branch operators receive status branch-selection-required with the branch list and must re-POST the same email and password plus the chosen branchId (a stateless two-step flow, no server-held pairing ticket, matching the designs stated rationale); zero-branch operators receive an explicit 403 with code no-branches-assigned, distinct from the generic 401 used for every credential-verification failure. The dummy-hash timing-parity path runs on both the unknown-directory-entry and unknown-credential branches.

## Real-Verification Confirmation (DeviceBearerAuthenticationHandler.cs)

Read the file in full (85 lines). HandleAuthenticateAsync hashes the presented bearer token and calls into PostgresDeviceCredentialStore.FindByTokenHashAsync -- a genuine call into the credential store, not a parse of claimed GUIDs. A null result fails authentication with an unknown-credential message; a revoked record fails with a revoked-credential message; only on success are claims for organization, branch, and installation built exclusively from the returned database row, never from the presented string. This is confirmed by reading the executable code path, not merely claimed in a comment.

## Structural Isolation Test Confirmation

The BranchNodeAssembly_DoesNotReference_PosWindowsAssembly filter was re-run in isolation above and passed 1/1.

## Out-of-Scope Files -- Confirmed Untouched

A git diff spanning the full range from before PR #26 through PR #28 inclusive, against TenantAuthorizationService.cs and PostgresUserAccountStore.cs, produced zero diff output for both files across the entire combined change range. Endpoints/Device.cs calls into PostgresUserAccountStores existing directory-lookup, email-lookup, and actor-loading methods plus the shared PasswordHasher -- it does not duplicate or add any parallel password-check logic anywhere in src or tests.

## Task Completion vs. Code State

All 31 tasks in tasks.md are marked complete (re-confirmed by reading the file in full: 31 checked, 0 unchecked, across 7 phases).

## Manual Verification Doc Genuineness (deploy/pos-manual-verify.md, Part 2)

Read in full. Contains concrete, non-hypothetical evidence throughout: a real process ID, real GUIDs for organization, branch, and installation ids cross-checked against the test-seed endpoints response, a real DPAPI ciphertext header confirming the token is never stored as plaintext, real sqlite3 and psql query outputs run as independent processes, and an honestly-documented real bug found and fixed live during the run (a WPF shutdown-mode lifecycle bug that silently killed the whole app after first-time pairing, with the exact fix and a re-run confirmation). Step 6 is marked as a partial pass with a well-reasoned deviation from the literal script (a cross-branch re-pair correctly does not flush the prior branchs pending outbox under the new branch identity -- explained as correct and safe behavior, not silently reinterpreted as a full pass). This reads as genuinely executed manual verification, not an assumed checklist.

## Spec Compliance Matrix (21/21 scenarios, requirement count 8)

| Spec | Requirement | Scenario | Test / Evidence | Result |
|---|---|---|---|---|
| pos-installation-identity | Sign-In Required | Fresh install has no local credential | LocalInstallationStoreTests.LoadOrCreate_FirstRun_YieldsFreshInstallationId_WithNoPairing; manual-verify Part 2 step 1 | COMPLIANT |
| pos-installation-identity | Sign-In Required | Invalid sign-in credentials are rejected | DeviceEndpointTests.Pair_WrongPassword_Returns401; manual-verify Part 2 step 2 | COMPLIANT |
| pos-installation-identity | Branch Selection Scoped | Single authorized branch is auto-selected | DeviceEndpointTests.Pair_SingleBranch_AutoPairs_WithToken; manual-verify step 3 | COMPLIANT |
| pos-installation-identity | Branch Selection Scoped | Multiple authorized branches require explicit selection | DeviceEndpointTests.Pair_MultiBranch_NoBranchIdGiven_ReturnsSelectionRequired_ListingOnlyInScopeBranches; manual-verify step 6 | COMPLIANT |
| pos-installation-identity | Branch Selection Scoped | Zero authorized branches shows an operational message | DeviceEndpointTests.Pair_ZeroBranches_Returns403_NoBranchesAssigned_NotGeneric401 | COMPLIANT |
| pos-installation-identity | Server-Issued Verifiable Credential | Credential issued after successful pairing | DeviceEndpointTests.Pair_SingleBranch_AutoPairs_WithToken and Pair_MultiBranch_StepTwo_RepostsCredentialsWithChosenBranch_Pairs | COMPLIANT |
| pos-installation-identity | Server-Issued Verifiable Credential | Persisted identity reused on restart | LocalInstallationStoreTests.SaveThenLoad_RoundTrips_PairingData | COMPLIANT |
| pos-installation-identity | Offline Sales Continue | Revoked credential does not block local sales | Structural BranchNodeAssembly_DoesNotReference_PosWindowsAssembly plus manual-verify step 5 (live revoke, second sale still succeeds) | COMPLIANT |
| pos-installation-identity | Offline Sales Continue | Revoked credential blocks sync until re-sign-in | DeviceEndpointTests.Sync_RevokedToken_Returns401; manual-verify step 5 | COMPLIANT |
| pos-installation-identity | Re-Pairing Without Manual File Deletion | Operator re-pairs to a different branch | DeviceCredentialStoreTests.IssueAsync_SameInstallationId_RevokesPriorRow_AndRecordsLineage; manual-verify step 6 (partial pass on outbox-flush wording, correctly reasoned) | COMPLIANT |
| pos-installation-identity | Re-Pairing Without Manual File Deletion | Operator re-pairs to the same branch | LocalInstallationStoreTests.Repair_OverwritesPairing_ButPreservesSameInstallationId | COMPLIANT |
| tenant-access-foundation | Installation Identity and Audit | Auditable sensitive action | Pre-existing baseline audit tests, unchanged, part of the green 127-test baseline | COMPLIANT |
| tenant-access-foundation | Installation Identity and Audit | Installation replacement identity | DeviceCredentialStoreTests.IssueAsync_SameInstallationId_RevokesPriorRow_AndRecordsLineage | COMPLIANT |
| tenant-access-foundation | Installation Identity and Audit | Device bearer credential must be server-verified, not caller-asserted | DeviceEndpointTests.Sync_OldBearerShape_Returns401 and Sync_UnissuedRandomToken_Returns401 | COMPLIANT |
| tenant-access-foundation | Installation Identity and Audit | Server-verified device credential resolves branch scope | DeviceEndpointTests.Sync_ValidToken_Returns200_AndPersistedRowCarriesServerIdentity_NeverCallerClaimed | COMPLIANT |
| branch-offline-sync | Retryable Delivery with Idempotent Effects | Duplicate delivery | Pre-existing baseline idempotency test | COMPLIANT |
| branch-offline-sync | Retryable Delivery with Idempotent Effects | Interrupted synchronization | Pre-existing baseline retry test | COMPLIANT |
| branch-offline-sync | Retryable Delivery with Idempotent Effects | Unregistered installation is rejected before sync | DeviceEndpointTests.Sync_UnissuedRandomToken_Returns401 | COMPLIANT |
| branch-offline-sync | Retryable Delivery with Idempotent Effects | Revoked installation credential blocks sync only | DeviceEndpointTests.Sync_RevokedToken_Returns401; manual-verify step 5 | COMPLIANT |
| organization-persistence | Branch Listing Scoped to a Users Branch Scope | Query returns only in-scope branches | DeviceEndpointTests.Pair_MultiBranch_NoBranchIdGiven_ReturnsSelectionRequired_ListingOnlyInScopeBranches (exercises PostgresOrganizationStore.ListBranchesAsync end to end) | COMPLIANT |
| organization-persistence | Branch Listing Scoped to a Users Branch Scope | Query returns nothing for a user with an empty branch scope | Structurally guaranteed by the ANY-array-membership WHERE clause over an empty array (always zero rows) and by RLS; no direct unit test calls ListBranchesAsync with an empty array in isolation, since the endpoint short-circuits to a 403 before ever calling it with an empty scope | COMPLIANT (see WARNING) |

## Correctness (Static + Runtime Evidence)

| Requirement | Status | Notes |
|---|---|---|
| Sign-In Required When No Valid Device Credential Exists | Implemented and tested | No fabricated identity before sign-in succeeds |
| Branch Selection Scoped to the Signed-In Operator | Implemented and tested | Auto-select, picker, and zero-branch cases all covered |
| Server-Issued, Verifiable Device Credential | Implemented and tested | Opaque secret, SHA-256 hash as primary key, zero claims stored in the token itself |
| Offline Sales Continue Despite Credential Problems | Implemented, tested, and manually proven live | Assembly-graph structural guarantee plus a live revoke-then-sell manual run |
| Re-Pairing Without Manual File Deletion | Implemented and tested | Same installation-id lineage preserved across re-pair |
| Installation Identity and Audit (tenant-access-foundation) | Implemented and tested | Old self-signed bearer shape now hard-rejected |
| Retryable Delivery with Idempotent Effects (branch-offline-sync) | Implemented and tested | Unregistered or revoked credentials rejected before any operation retry |
| Branch Listing Scoped to a Users Branch Scope (organization-persistence) | Implemented, mostly tested | Positive case directly tested; empty-scope case structurally correct but only indirectly exercised |

## Coherence (Design)

| Decision | Followed? | Notes |
|---|---|---|
| Opaque secret plus a hash of that secret as the primary key, zero claims in the token | Yes | Confirmed via DeviceTokenHasher usage and handler code |
| Plain SHA-256, not the slow password hasher, for the credential hash | Yes | Confirmed via DeviceTokenHasher and migration comment |
| Asymmetric device_credentials RLS: SELECT true, INSERT org-scoped, UPDATE true with a revoked-only check | Yes | Independently re-confirmed via live pg_policies |
| InstallationIdentityService and Tenancy.Installation deleted, not deprecated | Yes | Confirmed absent from the working tree and from all live source references |
| One stateless device-pairing endpoint, two-step re-post, no separate pairing-ticket registry | Yes | Confirmed by reading Endpoints/Device.cs in full |
| CloudTenantScope stays organization-id only; branch travels as a claim via DeviceIdentity | Yes | Confirmed no CloudTenantScope signature change; a branch claim type is present on the handler |
| No verification cache on the sync path | Yes | Handler hits the store on every request; no caching layer present |
| DPAPI-encrypted device token at rest, current-user scope | Yes | Confirmed via the encrypts-device-token-at-rest test and the manual-verify docs real DPAPI header evidence |
| Single-PR coordinated cutover, POS and Cloud.Api together | Yes | PR #26 is one merge; #27 and #28 are narrowly-scoped follow-up fixes, not a split of the bearer contract |

## Issues Found

CRITICAL: None.

WARNING:
1. PostgresOrganizationStore.ListBranchesAsyncs empty-array behavior (the organization-persistence scenario for an empty branch scope) has no direct unit or integration test calling the method in isolation with an empty array -- the only production caller short-circuits to a 403 before ever calling it with an empty scope. The behavior is structurally correct (an ANY-array-membership WHERE clause over an empty array always yields zero rows, and RLS remains org-scoped), but this specific scenario is proven by SQL semantics and code inspection rather than by a passing runtime test targeting that exact input. Recommend adding one direct store-level test for this case as a low-cost follow-up; this does not block archive.

SUGGESTION:
1. deploy/pos-manual-verify.md Part 2 step 6 is honestly marked as a partial pass rather than a plain pass, with a well-reasoned deviation explanation. This is good practice already followed by the author; no action needed.
2. Two untracked files under src/Commerce.Cloud.Api/Properties/ exist in the working tree, pre-existing before this verification session and unrelated to this change; does not affect the verdict.

## Verdict

PASS

A genuine clean rebuild is 0 warnings and 0 errors, confirming PR #27s NU1510 fix is real and not merely incrementally cached. The full suite is 157/157 (127 baseline plus 30 new) against a live Postgres and pgbouncer stack with no soft-skips. The critical RLS-violation test was re-run in isolation and its body was read line by line: it genuinely attempts both an unscoped un-revoke and an unscoped field rewrite against live rows through the app_runtime role, and both are rejected by Postgres itself via a PostgresException, not by application-layer assertions. Live pg_policies and pg_class queries independently confirm device_credentials has forced row level security and exactly the three designed policies. InstallationIdentityService.cs and Tenancy/Installation.cs are confirmed deleted with zero live source references. Endpoints/Device.cs pairing flow and DeviceBearerAuthenticationHandler.cs real store-backed lookup were both read in full and match the design. The structural sync-vs-sales isolation test passes. The full SPA plus Playwright E2E recipe was re-run for real (6/6) and confirms this change did not touch the cookie-auth SPA path; background processes were cleaned up and wwwroot was correctly reset to gitkeep-only. TenantAuthorizationService.cs and PostgresUserAccountStore.cs are confirmed completely untouched via git diff across the full combined PR range, so no parallel credential-verification logic was introduced. The manual-verify document contains genuine, concrete execution evidence, including an honestly-documented real bug found and fixed live. One non-blocking WARNING (an indirectly-but-correctly-covered organization-persistence edge case) does not affect the verdict. Ready for archive.
