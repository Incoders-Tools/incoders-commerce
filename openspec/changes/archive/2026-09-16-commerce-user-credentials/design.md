# Design: Commerce user credentials

## Technical Approach

Additive persistence slice on the existing Cloud.Api host. One append-only migration (`deploy/db/migrations/0002_users.sql`) adds two tables; one new `Persistence/PostgresUserAccountStore.cs` follows `PostgresCloudInboxStore.cs`'s exact shape (`NpgsqlDataSource` injected, every method opens its own `NpgsqlTransaction`, `SELECT set_config('app.current_org_id', $1, true)` as the first statement, then query, then commit). `Endpoints/Account.cs` gains real password verification plus a log-gated bootstrap path; `Endpoints/Catalog.cs` stops trusting the request body and loads the actor from the store using the cookie's `NameIdentifier` claim. `TenantAuthorizationService`, `TenantScopeResolver`, `CloudTenantScope`, `Ordering.cs`, `Sync.cs`, and `Commerce.Pos.Windows` are untouched.

**Build spike result (`PasswordHasher<TUser>`)**: CONFIRMED by an actual `dotnet build` (orchestrator ran it directly, since the design agent's context had no shell tool). A temporary spike file referencing `new PasswordHasher<UserAccount>()` was added to `src/Commerce.Cloud.Api`, built successfully with **0 warnings, 0 errors, and zero new `PackageReference` entries** (only the existing `Npgsql` package remained), then removed. `PasswordHasher<UserAccount>` resolves cleanly under `Microsoft.NET.Sdk.Web` from the ASP.NET Core shared framework, as predicted by the on-disk reference-assembly check. No apply task needs to re-run this spike — it is closed.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| Sign-in identifier | `email`, normalized to lowercase/trimmed on write and read. Operators bootstrap by email; an org-scoped username would still require the org to be known first (see next row). | Username — same lookup problem, worse UX. |
| Org resolution at sign-in | A second, credential-free table `user_directory (email_normalized PK, organization_id, user_id)`, readable **unscoped**. Sign-in: (1) unscoped directory lookup by email → `organization_id`; (2) mint `CloudTenantScope` from that row; (3) org-scoped read of `users` for the hash/roles/branch scope; (4) verify. The cookie's `org_id` claim is stamped from the **stored** `organization_id`, never from client input. `users` itself keeps `0001_init_rls.sql`'s exact isolation policy — no weakening, no second DB role, no `SECURITY DEFINER`. The directory leaks only "this email exists and maps to this org uuid", and it is never exposed over HTTP; sign-in always answers a generic 401. | (a) Client-supplied `organizationId` at sign-in — technically sound (the password is still the gate, and the claim comes from the row) but forces the user to type a uuid to log in; (b) a `SECURITY DEFINER` lookup function — `FORCE ROW LEVEL SECURITY` binds the owner too, so it would need its own GUC juggling; (c) a dedicated unscoped `app_signin` role — a second connection string and secret for one query. |
| Directory RLS policy | Asymmetric: `USING (true)` (global read, required for the lookup) + `WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)` (writes stay org-scoped). RLS stays enabled+forced so the unscoped read is an explicit, reviewable policy rather than an absent one. | No RLS on the table — same effect, but silently, and it breaks the repo's "every table is enabled+forced" convention. |
| Roles storage | `roles jsonb NOT NULL DEFAULT '[]'` — array of `{"name": "...", "permissions": <int>}`, deserialized to `Role(string, Permission)`. Roles are code-defined (`Permission` is a `[Flags]` enum in `Commerce.Domain.Identity`); a join table would need a persisted role catalog, which is an explicit non-goal. | `user_roles` join table — requires the deferred role/permission catalog. |
| Branch scope storage | `branch_scope uuid[] NOT NULL DEFAULT '{}'` — Npgsql maps `uuid[]` to `Guid[]` natively; the value is a set of ids with no shape, unlike roles. | jsonb — no gain, loses the native mapping. |
| Revocation | `is_revoked boolean NOT NULL DEFAULT false` on the user row, mapped by calling `UserAccount.Revoke()` after construction (the setter is private). Sign-in and actor load both reject revoked users. | Deferring to user-management work — the column is one line now and free later. |
| Hashing | `PasswordHasher<UserAccount>` registered as a singleton (`Microsoft.AspNetCore.Identity`). `HashPassword` on create; `VerifyHashedPassword` on sign-in, treating `SuccessRehashNeeded` as success (rehash-on-login is out of scope, noted). No `IdentityDbContext`, no `UserManager`, no EF. | Hand-rolled PBKDF2/BCrypt — a new dependency or hand-rolled crypto for an algorithm the shared framework already ships. |
| Bootstrap token storage | In-memory singleton `Authentication/BootstrapTokenRegistry` holding, per organization, `(SHA-256 hash of the token, expiresAtUtc, consumed)`. No plaintext, no DDL, no cleanup job; tokens die on container restart, which bounds the blast radius to one deploy window. | A `bootstrap_tokens` table — a third table, survives restarts, needs expiry sweeping, for a one-shot operator flow. |
| Bootstrap token delivery | `POST /account/bootstrap/request-token` is **anonymous** but returns `202 Accepted` with an **empty body**; the plaintext token is only ever written to stdout at `LogInformation`. Possession therefore requires reading `railway logs` / the Railway dashboard, which is the operator boundary — calling the endpoint anonymously yields the attacker nothing. | Returning the token in the response (anyone could bootstrap); an admin-authenticated endpoint (circular — no admin exists yet); CLI-only (user-rejected: no shell on Railway). |

## Data Flow

```text
Sign-in
  SPA POST /account/sign-in {email, password}
    -> PostgresUserAccountStore.FindDirectoryEntryAsync(email)        [unscoped tx]
       -> miss: dummy VerifyHashedPassword (timing parity) -> 401
    -> CloudTenantScope(directory.OrganizationId)
    -> FindByEmailAsync(scope, email)                                 [set_config tx]
    -> PasswordHasher.VerifyHashedPassword(...) | is_revoked -> 401
    -> SignInAsync(cookie: org_id=<stored>, NameIdentifier=<stored id>, Name=<email>)

Authenticated rename
  SPA POST /catalog/products/{id}/rename {targetBranchId, currentName, ..., newName}
    -> RequireAuthorization + TenantScopeEndpointFilter -> CloudTenantScope (org_id claim)
    -> NameIdentifier claim -> LoadActorAsync(scope, userId)          [set_config tx]
       -> null or IsRevoked -> 403
    -> CloudCatalogManagementAdapter.RenameProduct(scope, actor, ...)  (unchanged)

Bootstrap
  operator POST /account/bootstrap/request-token {organizationId}
    -> HasAnyUserAsync(scope) == true -> 409
    -> RandomNumberGenerator 32 bytes -> base64url; registry stores SHA-256(token), now+15min
    -> ILogger.LogInformation("bootstrap token for {Org}: {Token}")   -> container stdout
    -> 202, empty body
  operator POST /account/bootstrap {organizationId, token, email, password}
    -> registry.TryConsume(org, token): fixed-time hash compare, not expired, not consumed
    -> one tx: set_config -> re-check zero users -> INSERT users + INSERT user_directory -> COMMIT
    -> registry marks consumed (single-use; replay -> 401)
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `deploy/db/migrations/0002_users.sql` | Create | `users` + `user_directory`, enabled+forced RLS, policies, `app_runtime` GRANTs. `0001_init_rls.sql` is NOT touched (append-only). |
| `deploy/dev/db/init-rls.sql` | Modify | Same two tables appended verbatim (dev password literal instead of the placeholder). The two files are kept in sync by hand today — `0001` was promoted from `init-rls.sql` and `MigrationRlsTests` asserts they behave identically; follow that convention. |
| `src/Commerce.Cloud.Api/Persistence/PostgresUserAccountStore.cs` | Create | Npgsql store, `PostgresCloudInboxStore` shape. |
| `src/Commerce.Cloud.Api/Persistence/UserRecords.cs` | Create | `UserDirectoryEntry`, `UserCredentialRecord`, `NewUserAccount` records. |
| `src/Commerce.Cloud.Api/Authentication/BootstrapTokenRegistry.cs` | Create | In-memory per-org one-time token issue/consume. |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modify | Real sign-in, two bootstrap routes; `SignInRequest` shape change. |
| `src/Commerce.Cloud.Api/Endpoints/Catalog.cs` | Modify | Actor from store; `RenameProductRequest` drops actor fields. |
| `src/Commerce.Cloud.Api/Program.cs` | Modify | DI for the store, `PasswordHasher<UserAccount>`, `BootstrapTokenRegistry`. |
| `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` | Modify | Extend the existing table/FORCE-RLS/policy verification to `users` and `user_directory`. |
| `src/Commerce.Web/src/api/types.ts` | Modify | `SignInRequest {email,password}`; `RenameProductRequest` drops `actorId`/`actorBranchScope`/`actorRoles`. |
| `src/Commerce.Web/src/screens/SignInScreen.tsx` | Modify | Email + password fields replace org/user uuid + display name. |
| `src/Commerce.Web/src/screens/CatalogScreen.tsx` | Modify | Drop the Actor ID field and the hardcoded `catalog-manager` role. |
| `src/Commerce.Web/src/screens/CatalogScreen.test.tsx` | Modify | Assert the posted body carries no actor fields. |
| `tests/Commerce.Integration/UserAccountStoreTests.cs` | Create | Store + RLS coverage (live Postgres, `PostgresTestFixture` skip convention). |
| `tests/Commerce.Integration/AccountEndpointTests.cs` | Create | Sign-in and bootstrap HTTP coverage. |
| `tests/Commerce.Integration/MigrationRlsTests.cs` | Modify | Apply `0002` too; cross-org `users` read returns zero rows; idempotent on re-apply. |
| `tests/Commerce.Integration/ManagementParityTests.cs` | Modify | Unchanged call shape (it calls `CatalogManagementService`/`CloudCatalogManagementAdapter` directly, not HTTP) — **verify only**; it must still compile and pass. The HTTP actor-trust change is covered by the new `AccountEndpointTests`/catalog endpoint test instead. |

## Interfaces / Contracts

```sql
CREATE TABLE IF NOT EXISTS users (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL,                 -- no FK: organizations are not persisted (non-goal)
    email           text NOT NULL,                 -- stored already normalized (lower/trim)
    password_hash   text NOT NULL,
    branch_scope    uuid[] NOT NULL DEFAULT '{}',
    roles           jsonb NOT NULL DEFAULT '[]',   -- [{"name":"...","permissions":<int>}]
    is_revoked      boolean NOT NULL DEFAULT false,
    created_at_utc  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS users_org_email_unique ON users (organization_id, email);

CREATE TABLE IF NOT EXISTS user_directory (
    email_normalized text PRIMARY KEY,             -- globally unique: one email, one account
    organization_id  uuid NOT NULL,
    user_id          uuid NOT NULL
);

ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE ROW LEVEL SECURITY;
ALTER TABLE user_directory ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_directory FORCE ROW LEVEL SECURITY;

REVOKE ALL ON users, user_directory FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON users TO app_runtime;         -- app_runtime already exists from 0001
GRANT SELECT, INSERT ON user_directory TO app_runtime;

DROP POLICY IF EXISTS users_tenant_isolation ON users;
CREATE POLICY users_tenant_isolation ON users
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Deliberately asymmetric: the sign-in email->organization lookup runs BEFORE any
-- org scope exists, so reads are global; writes stay org-scoped. This table holds
-- no credential material and is never exposed over HTTP.
DROP POLICY IF EXISTS user_directory_lookup ON user_directory;
CREATE POLICY user_directory_lookup ON user_directory
    USING (true)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
```

```csharp
public sealed record UserDirectoryEntry(string EmailNormalized, Guid OrganizationId, Guid UserId);
public sealed record UserCredentialRecord(Guid Id, Guid OrganizationId, string Email, string PasswordHash, bool IsRevoked);
public sealed record NewUserAccount(Guid Id, string Email, string PasswordHash, IReadOnlyList<Guid> BranchScope, IReadOnlyList<Role> Roles);

public sealed class PostgresUserAccountStore
{
    public PostgresUserAccountStore(NpgsqlDataSource dataSource);

    // Unscoped by design: the only method that does NOT call set_config, because
    // the organization is what it is resolving. Credential-free projection.
    Task<UserDirectoryEntry?> FindDirectoryEntryAsync(string email, CancellationToken ct);

    Task<UserCredentialRecord?> FindByEmailAsync(CloudTenantScope scope, string email, CancellationToken ct);
    Task<UserAccount?> LoadActorAsync(CloudTenantScope scope, Guid userId, CancellationToken ct);
    Task<bool> HasAnyUserAsync(CloudTenantScope scope, CancellationToken ct);
    Task<bool> TryCreateAsync(CloudTenantScope scope, NewUserAccount user, CancellationToken ct);
}
```

`LoadActorAsync` constructs `new UserAccount(id, organization_id, branch_scope, roles)` and calls `Revoke()` when `is_revoked` is true, since the setter is private. `TryCreateAsync` inserts into `users` and `user_directory` inside **one** transaction after the scope statement and re-checks "zero users in this org" inside that same transaction; the `user_directory` primary key makes a duplicate email a clean `false`, not an exception surfaced to the caller.

```csharp
// Endpoints/Account.cs
public sealed record SignInRequest(string Email, string Password);
public sealed record SignedInResponse(Guid OrganizationId, Guid UserId, string DisplayName); // unchanged shape
public sealed record BootstrapTokenRequest(Guid OrganizationId);
public sealed record BootstrapRequest(Guid OrganizationId, string Token, string Email, string Password);

// Endpoints/Catalog.cs — actor fields removed
public sealed record RenameProductRequest(
    Guid TargetBranchId, string CurrentName, Guid CategoryId, Guid DefaultUnitId,
    string NewName, bool IsOffline, Guid CorrelationId);
```

`RoleDto` stays (the store's jsonb deserialization target) but leaves the HTTP request surface.

The bootstrapped first user gets `roles = [{"name":"admin","permissions":15}]` (all four `Permission` flags) and `branch_scope = '{}'`. Empty branch scope is intentional: the admin is org-wide, and `TenantAuthorizationService` is untouched — if it requires an explicit branch match, the bootstrap task must seed the scope from the request instead, and the apply phase must confirm which against the actual service.

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | `BootstrapTokenRegistry`: issue → consume succeeds; second consume fails; expired token fails; wrong token fails; issuing again invalidates the prior token | xUnit, injected clock |
| Unit | Roles jsonb round-trip; `is_revoked` → `UserAccount.IsRevoked` mapping | xUnit against the store's mapping helpers |
| Integration | Sign-in: correct password → 200 + cookie whose `org_id` equals the **stored** org; wrong password → 401; unknown email → 401 (same body/shape); revoked user → 401 | `WebApplicationFactory` + live Postgres |
| Integration | Bootstrap: valid token creates the admin; replayed token → 401; expired token → 401; second bootstrap on a non-empty org → 409; the `202` response body contains no token | `WebApplicationFactory`, `ILogger` capture for the token |
| Integration | Rename: actor is loaded from the store, so a body that no longer carries actor fields still authorizes correctly; a user whose stored roles lack `ManageCatalog` gets 403 even with a valid cookie | `WebApplicationFactory`, seeded users |
| Integration | RLS: cross-org `users` read returns zero rows; unscoped `users` read fails closed (zero rows, not a cast error); `0002` is idempotent when applied twice | Extend `MigrationRlsTests` / `PostgresTestFixture` |
| Frontend | `CatalogScreen` posts no actor fields; `SignInScreen` posts `{email,password}` | Vitest + Testing Library, existing `*.test.tsx` convention |

## Threat Matrix

| Native row | Applicability |
|---|---|
| Documentation-like paths | N/A — no file-classification boundary |
| Git repository selection | N/A — no product code runs Git |
| Commit state | N/A — no commit automation added |
| Push state | N/A — no deploy automation changes; `railway.json` untouched |
| PR commands | N/A — no PR automation added |
| Routing | **Applicable** — two new anonymous HTTP routes under `/account`. Safe behavior: `request-token` returns no token material and refuses orgs that already have users; `bootstrap` requires a fixed-time-compared, single-use, 15-minute token AND a still-empty org, both re-checked inside the write transaction. RED tests: replay, expiry, non-empty org, and "no token in the 202 body" (see Testing Strategy). |

No shell, subprocess, or process-integration boundary is introduced.

## Migration / Rollout

Forward-only and additive. Per environment: run `deploy/db/migrations/0002_users.sql` via `psql` against the direct (non-pooled) connection, exactly as `deploy/README.md` documents for `0001`, **before** deploying the new image — `/health/ready` will fail closed until the tables exist, so ordering is enforced by the readiness gate rather than by discipline. Then deploy, read the token from `railway logs`, bootstrap the first admin, and confirm sign-in. Rollback: revert the commit (endpoints return to their previous shape) and, optionally, `DROP TABLE user_directory, users;` — nothing has a foreign key to them and `sync_inbox` is untouched.

**Breaking change**: `RenameProductRequest` and `SignInRequest` change shape. The SPA is served from the same image, so client and server ship together; there is no independently deployed consumer to version.

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | Everything: migration + dev init script, store, registry, endpoints, DI, readiness check, SPA updates, tests | ~380 | `0002` applies twice cleanly; new integration tests green against `deploy/dev/compose.yaml`; `npm run build` + Vitest green; existing xUnit suite green | Revert one commit; drop the two tables |

Decision needed before apply: No
Chained PRs recommended: No
400-line budget risk: Medium

**Why one PR**: the change is atomic in the security sense — splitting it would land a period where the schema exists but the endpoints still trust the request body, or where the SPA posts fields the API no longer accepts. The estimate (~380 authored lines: ~70 SQL across two files, ~150 store + registry, ~90 endpoints, ~30 SPA, plus tests) sits just under the 400-line budget with tests counted. If the apply phase overruns, the only defensible split is **PR #1 = migration + dev init script + store + its RLS tests** (dead code, no behavior change, trivially revertible) and **PR #2 = endpoints + SPA + endpoint tests**, chained onto #1 — never a split that leaves the actor-trust hole open across a merge.

## Open Questions

- [ ] Does `TenantAuthorizationService` require a non-empty `BranchScope` containing `TargetBranchId`? If yes, the bootstrap admin needs a seeded branch scope rather than `'{}'`. Resolve by reading the service in the first apply task — it changes one literal, not the design.
- [ ] Multi-replica Railway would break the in-memory token registry (issued on instance A, redeemed on instance B). Cloud.Api runs a single replica today; if that changes, the registry must move to a `bootstrap_tokens` table. Documented, accepted for now.
