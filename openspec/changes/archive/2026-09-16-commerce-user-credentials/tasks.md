# Tasks: Commerce User Credentials

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~380 (design's own estimate: ~70 SQL, ~150 store/registry, ~90 endpoints, ~30 SPA, plus tests) |
| 400-line budget risk | Medium |
| Chained PRs recommended | No |
| Suggested split | Single PR (design explicitly rejects splitting: a mid-split state leaves the actor-trust hole half-closed) |
| Delivery strategy | exception-ok |
| Chain strategy | size-exception |

Decision needed before apply: No
Chained PRs recommended: No
Chain strategy: size-exception
400-line budget risk: Medium

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Migration + dev init script + store + registry + endpoints + DI + readiness check + SPA + tests, atomic | PR 1 (size:exception) | `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~UserAccount\|FullyQualifiedName~AccountEndpoint\|FullyQualifiedName~MigrationRls` | `deploy/dev/compose.yaml` up, apply `0002_users.sql` twice, `npm run build` + Vitest in `src/Commerce.Web` | Revert one commit; `DROP TABLE user_directory, users;` (no FKs point at them) |

## Phase 1: Migration and DI foundation

- [x] 1.1 RED: add `MigrationRlsTests` cases for `users`/`user_directory` (cross-org `users` read returns zero rows; unscoped `users` read fails closed; `0002` idempotent on re-apply) in `tests/Commerce.Integration/MigrationRlsTests.cs` — spec: user-credentials "User row is isolated by organization".
- [x] 1.2 GREEN: create `deploy/db/migrations/0002_users.sql` with `users` (FORCE RLS, `app_runtime` GRANT/REVOKE, `users_tenant_isolation` policy matching `0001`'s exact shape) and `user_directory` (asymmetric `USING (true)` / org-scoped `WITH CHECK`), per design's Interfaces/Contracts SQL verbatim.
- [x] 1.3 GREEN: append the same DDL to `deploy/dev/db/init-rls.sql` (dev literal password, hand-synced per `0001`'s existing convention). Re-run 1.1 to confirm green.
- [x] 1.4 Create `src/Commerce.Cloud.Api/Persistence/UserRecords.cs`: `UserDirectoryEntry`, `UserCredentialRecord`, `NewUserAccount` records exactly as specified in design.
- [x] 1.5 RED: unit tests for `BootstrapTokenRegistry` (issue→consume succeeds; second consume fails; expired fails; wrong token fails; re-issue invalidates prior token) using an injected clock, in a new `tests/Commerce.Cloud.Api.Tests` or existing xUnit unit project.
- [x] 1.6 GREEN: create `src/Commerce.Cloud.Api/Authentication/BootstrapTokenRegistry.cs` — in-memory, per-org `(SHA-256 hash, expiresAtUtc, consumed)`, 15-min expiry, one-time-use, per design's exact mechanism.

## Phase 2: Credential store and hashing

- [x] 2.1 RED: add `tests/Commerce.Integration/UserAccountStoreTests.cs` covering `FindDirectoryEntryAsync` (unscoped), `FindByEmailAsync`/`LoadActorAsync` cross-org isolation, `is_revoked`→`UserAccount.IsRevoked` mapping, roles-jsonb round-trip, `HasAnyUserAsync`, `TryCreateAsync` duplicate-email returns `false` not exception — reuse `PostgresCloudInboxStoreTests`/`PostgresTestFixture` skip conventions.
- [x] 2.2 GREEN: create `src/Commerce.Cloud.Api/Persistence/PostgresUserAccountStore.cs` following `PostgresCloudInboxStore.cs`'s exact shape (`NpgsqlDataSource`, per-method `NpgsqlTransaction`, `SELECT set_config('app.current_org_id', $1, true)` first, except `FindDirectoryEntryAsync` which stays unscoped). Implement all five methods from the design's interface.
- [x] 2.3 GREEN: register `PasswordHasher<UserAccount>` as a singleton and `PostgresUserAccountStore`/`BootstrapTokenRegistry` in DI in `src/Commerce.Cloud.Api/Program.cs`. No new `PackageReference` needed (build spike already confirmed).
- [x] 2.4 Wire `HashPassword` into `TryCreateAsync`'s caller path (bootstrap flow) and `VerifyHashedPassword` into sign-in, treating `SuccessRehashNeeded` as success; rehash-on-login explicitly out of scope (comment it in code).

## Phase 3: Endpoint rewrites

- [x] 3.1 RED: `tests/Commerce.Integration/AccountEndpointTests.cs` — sign-in success issues cookie with stored `org_id`/`NameIdentifier`; wrong password → 401; unknown email → 401 with identical body/shape to wrong-password (dummy-hash timing parity per design risk #4); revoked user → 401.
- [x] 3.2 GREEN: rewrite `src/Commerce.Cloud.Api/Endpoints/Account.cs` sign-in: `SignInRequest(Email, Password)`, unscoped directory lookup, `CloudTenantScope` from stored org id, org-scoped `FindByEmailAsync`, `VerifyHashedPassword`, generic 401 on miss/mismatch/revoked, cookie claims stamped only after success.
- [x] 3.3 RED: extend `AccountEndpointTests.cs` — `POST /account/bootstrap/request-token` returns 202 with empty body and logs the token (capture via `ILogger`), rejects when org already has users (409); bootstrap-completion endpoint: valid token+org+email+password creates admin (roles=`[{"name":"admin","permissions":15}]`, `branch_scope='{}'`); replayed token → 401; expired token → 401; wrong-org token → 401 (spec: "Bootstrap token is scoped to one organization").
- [x] 3.4 GREEN: add `POST /account/bootstrap/request-token` (anonymous, `BootstrapTokenRequest`) and bootstrap-completion route (`BootstrapRequest`) to `Account.cs`, wired to `BootstrapTokenRegistry` and `PostgresUserAccountStore.TryCreateAsync` inside one transaction that re-checks zero-users.
- [x] 3.5 Confirm in `TenantAuthorizationService` whether it requires a non-empty `BranchScope` containing `TargetBranchId` (design's open question); adjust bootstrap admin's seeded `branch_scope` literal only if required — no design change.
- [x] 3.6 RED: extend `tests/Commerce.Integration` (new or existing catalog endpoint test) asserting a rename request with forged `ActorId`/`ActorBranchScope`/`ActorRoles` in the body is ignored — the store-loaded actor's roles/scope govern the authorization outcome (spec: tenant-access-foundation "Privilege escalation via request body is denied").
- [x] 3.7 GREEN: rewrite `src/Commerce.Cloud.Api/Endpoints/Catalog.cs` — `RenameProductRequest` drops `ActorId`/`ActorBranchScope`/`ActorRoles`; load `UserAccount` via `LoadActorAsync(scope, userId)` using the cookie's `NameIdentifier` claim; null/revoked → 403.

## Phase 4: Readiness gate and downstream SPA

- [x] 4.1 RED: extend `PostgresReadinessHealthCheck` tests to assert `/health/ready` fails when `users`/`user_directory` FORCE-RLS or policies are missing.
- [x] 4.2 GREEN: extend `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` to verify FORCE-RLS/policy shape for `users` and `user_directory`, matching the existing `sync_inbox` verification pattern.
- [x] 4.3 Update `src/Commerce.Web/src/api/types.ts`: `SignInRequest {email, password}`; `RenameProductRequest` drops `actorId`/`actorBranchScope`/`actorRoles`.
- [x] 4.4 Update `src/Commerce.Web/src/api/catalog.ts` and `src/Commerce.Web/src/screens/CatalogScreen.tsx` to stop sending actor fields, matching the new `RenameProductRequest` shape.
- [x] 4.5 Update `src/Commerce.Web/src/screens/SignInScreen.tsx` to post `{email, password}` instead of org/user uuid + display name.
- [x] 4.6 RED->GREEN: update `src/Commerce.Web/src/screens/CatalogScreen.test.tsx` to assert the posted body carries no actor fields; add/update a `SignInScreen` test asserting the `{email,password}` payload.

## Phase 5: Verification and cleanup

- [x] 5.1 Verify `tests/Commerce.Integration/ManagementParityTests.cs` needs NO code changes — it calls `CatalogManagementService`/`CloudCatalogManagementAdapter` directly, never the HTTP endpoint. Run it to confirm it still compiles and passes unchanged; do not edit this file.
- [x] 5.2 Confirm no changes were made to `TenantAuthorizationService`, `TenantScopeResolver`, `CloudTenantScope`, `Ordering.cs`, `Sync.cs`, or `Commerce.Pos.Windows` — out of scope per design.
- [x] 5.3 Run the full existing xUnit suite (83 tests) and confirm all pass unchanged; run `dotnet build` to confirm zero new `PackageReference` entries beyond existing `Npgsql`.
- [x] 5.4 Apply `0002_users.sql` twice against `deploy/dev/compose.yaml` to confirm idempotency; document the migrate-before-deploy ordering note in `deploy/README.md` alongside `0001`'s existing instructions.
- [x] 5.5 Run `npm run build` and Vitest in `src/Commerce.Web` to confirm the SPA changes compile and pass.
