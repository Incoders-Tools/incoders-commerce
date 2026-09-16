# Design: Organization and branch persistence

## Technical Approach

Additive persistence slice, same shape as `commerce-user-credentials`. One append-only migration (`deploy/db/migrations/0003_organizations_branches.sql`) adds `organizations` + `branches` with the exact FORCE-RLS/`app_runtime`/`current_setting('app.current_org_id', true)` pattern `0002_users.sql` already uses, hand-synced into `deploy/dev/db/init-rls.sql`. One new `Persistence/PostgresOrganizationStore.cs` owns a **single** `NpgsqlTransaction` that creates the organization, its one branch, the first admin user, and the directory entry together. `Endpoints/Account.cs`'s bootstrap route delegates the whole write to that one store call and seeds `branch_scope = [branchId]` instead of `[]`. `TenantAuthorizationService`, `CloudTenantScope`, `TenantScopeResolver`, `Sync.cs`, `Ordering.cs`, `Catalog.cs`, and `Commerce.Pos.Windows` are untouched.

The proposal's open identity question is closed: `organizations.id` and `branches.id` are fresh `Guid.NewGuid()` values, matching every other entity in this system. `name` is the human-friendly label; the id stays opaque.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Transaction composition (the core decision)** | `PostgresOrganizationStore` owns the connection and the transaction. `PostgresUserAccountStore` gains an `internal` transaction-participating method `InsertAsync(NpgsqlConnection, NpgsqlTransaction, CloudTenantScope, NewUserAccount, ct)` carrying the exact `users` + `user_directory` insert bodies that `TryCreateAsync` has today, **minus** tx lifecycle and `set_config`. The public `TryCreateAsync` becomes a thin wrapper (open → begin → `set_config` → zero-users guard → `InsertAsync` → commit), so every existing caller and test keeps its current behavior and signature. The org store injects the user store via DI and calls `InsertAsync` inside its own transaction. One owner of the transaction, zero duplicated INSERT SQL. | (a) **Endpoint orchestrates the transaction** — puts `NpgsqlConnection`/`NpgsqlTransaction` into `Endpoints/Account.cs`, which today contains zero Npgsql and would become the only endpoint that knows the driver. (b) **Copy the user INSERTs into the org store** — two places to keep in sync with the `users` schema; the `user_directory` write would silently drift. (c) **A new orchestrating service over two stores** — an extra layer whose only job is to hand one store's transaction to another, for exactly one call site; the store that already owns the transaction is the simpler owner. |
| Reject-if-org-exists | Checked **inside** the write transaction as `SELECT EXISTS (SELECT 1 FROM organizations WHERE id = $1)`, immediately after `set_config`, before any insert. The pre-existing zero-users guard is kept and runs next. Both are re-checks inside the same transaction, closing the TOCTOU window exactly as `TryCreateAsync` already does for users. | An endpoint-level pre-check only — races against a concurrent bootstrap. (Kept as an optional cheap pre-flight only if the endpoint already needs it; the in-transaction check is the authority.) |
| Bootstrap token vs. transaction ordering | `registry.TryConsume` stays **before** the transaction. A failed transaction therefore burns the token and the operator must request a new one. Single-use is the security property; re-usability after failure is not. | Peek-then-consume-after-commit — opens a replay window between peek and commit, trading a hard security guarantee for operator convenience. |
| `branches` RLS policy | `branches` gets its **own** symmetric policy on its **own** `organization_id` column, identical in shape to `users_tenant_isolation`. It does not inherit or derive scoping from `organizations`. Reason: RLS is per-table; a policy referencing the parent would need a subquery on `organizations` (slower, and the parent row's own visibility becomes a second failure mode). `branches.organization_id` is denormalized precisely so the policy stays a direct column comparison. | A policy of the form `organization_id IN (SELECT id FROM organizations)` — recursive visibility dependency plus a per-row subquery, for identical results. |
| `branches` → `organizations` FK | `REFERENCES organizations(id) ON DELETE CASCADE`. Both tables are new in this migration, so there is no legacy data to validate against. Note: Postgres exempts referential-integrity checks from RLS, so the FK check can see a row the policy would hide — harmless here because `WITH CHECK` already pins `branches.organization_id` to the current scope, making a cross-org parent unreachable. | No FK — loses the one integrity guarantee that is free in this change. |
| Identity | Fresh `Guid.NewGuid()` for both ids, generated server-side in the endpoint. `organizationId` still arrives in the bootstrap request (the token registry is keyed by it and the existing contract already carries it); the endpoint uses it as the organization's id. Branch id is always server-minted. | A human-readable slug id — diverges from every other entity; `name` already serves the human-facing role. |
| No FK retrofit onto existing tables | Deliberate scope boundary. `users.organization_id`, `sync_inbox.organization_id/branch_id`, and `user_directory.organization_id` keep their comment-documented "no FK" status. | See "Migration / Rollout" for the informed opinion on whether a later retrofit is straightforward. |

## Data Flow

```text
Bootstrap (the only writer of organizations/branches)
  operator POST /account/bootstrap/request-token {organizationId}
    -> HasAnyUserAsync -> 409 if non-empty
    -> token -> ILogger.LogInformation -> container stdout only; 202 empty body

  operator POST /account/bootstrap
        {organizationId, token, organizationName, branchName?, email, password}
    -> validate: organizationName non-blank; branchName ?? "Main"
    -> registry.TryConsume(org, token) -> 401 on miss/expired/replayed   [token burned]
    -> orgId = request.OrganizationId; branchId = Guid.NewGuid(); userId = Guid.NewGuid()
    -> PostgresOrganizationStore.TryCreateBootstrapAsync(scope, org, branch, admin)

         BEGIN
           1. SELECT set_config('app.current_org_id', $orgId, true)   <- always first
           2. SELECT EXISTS (SELECT 1 FROM organizations WHERE id=$orgId)
                 -> true  => ROLLBACK, OrganizationAlreadyExists
           3. SELECT EXISTS (SELECT 1 FROM users)
                 -> true  => ROLLBACK, OrganizationAlreadyHasUsers
           4. INSERT organizations (id, name)
           5. INSERT branches      (id, organization_id, name)
           6. userStore.InsertAsync(conn, tx, scope, admin)
                 INSERT users (…, branch_scope = ARRAY[branchId])
                 INSERT user_directory (email_normalized, organization_id, user_id)
           7. COMMIT
         any PostgresException (unique violation / RLS WITH CHECK) => ROLLBACK, Conflict
         any other throw / cancellation => `await using` disposes tx => Postgres rolls back

    -> Created => 200 {organizationId, branchId, userId}
    -> anything else => 409

Authenticated rename (unchanged code, now reachable)
  sign-in -> cookie(org_id, NameIdentifier) -> LoadActorAsync
    -> actor.BranchScope now contains the real branchId
    -> TenantAuthorizationService.Authorize(targetBranchId = branchId) -> allowed
```

**Atomicity argument.** Steps 4–6 are four INSERTs in one Postgres transaction on one connection. Postgres aborts the entire transaction on the first error (subsequent statements fail `25P02`), so a mid-flow failure can never commit a prefix. No partial state — an orphaned organization, a branch without an organization, or an organization without an admin — is representable. The only non-DB failure mode (process death between COMMIT and the HTTP response) leaves a fully valid, complete tenancy root; the operator simply sees a failed request and finds bootstrap already done on retry (409).

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `deploy/db/migrations/0003_organizations_branches.sql` | Create | `organizations` + `branches`, enabled+forced RLS, policies, `app_runtime` GRANTs. `0001`/`0002` are NOT touched (append-only). |
| `deploy/dev/db/init-rls.sql` | Modify | Same DDL appended verbatim (hand-sync convention; no password placeholder in this file's new section — `app_runtime` already exists from the `0001` block above it). |
| `src/Commerce.Cloud.Api/Persistence/PostgresOrganizationStore.cs` | Create | Owns the single bootstrap transaction; `PostgresCloudInboxStore` shape (`NpgsqlDataSource`, `set_config` first). |
| `src/Commerce.Cloud.Api/Persistence/OrganizationRecords.cs` | Create | `NewOrganization`, `NewBranch`, `BootstrapOutcome`. |
| `src/Commerce.Cloud.Api/Persistence/PostgresUserAccountStore.cs` | Modify | Extract `internal InsertAsync(conn, tx, scope, user, ct)`; `TryCreateAsync` becomes its wrapper. No public signature changes. |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modify | `BootstrapRequest` gains `OrganizationName` + `BranchName?`; new `BootstrapResponse`; bootstrap delegates to the org store; stale "empty branch scope" comment block deleted. |
| `src/Commerce.Cloud.Api/Program.cs` | Modify | Register `PostgresOrganizationStore`. |
| `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` | Modify | Extend the table/FORCE-RLS/policy verification to `organizations` and `branches`; update the remediation message to name `0003`. |
| `src/Commerce.Cloud.Api/Endpoints/TestSeedEndpoints.cs` | Modify | Seam narrows: seeds through the **real** org+branch+admin path and returns the real `branchId`; the arbitrary `branchScope` input is removed. See "Test seam" below. |
| `src/Commerce.Web/e2e/helpers.ts` | Modify | `seedUser` drops `branchScope`, returns `branchId`; comment rewritten. |
| `src/Commerce.Web/e2e/catalog.spec.ts` | Modify | "Allowed" test uses the real `branchId`; "denied" test now targets a *different* random branch id (a real cross-branch denial, not a "no branches exist" artifact). |
| `src/Commerce.Web/src/api/types.ts` | Modify | Only if a bootstrap request type exists there — verify during apply; the SPA does not call bootstrap today. |
| `tests/Commerce.Integration/MigrationRlsTests.cs` | Modify | Apply `0003`; idempotency; cross-org and unscoped deny for both new tables. |
| `tests/Commerce.Integration/OrganizationStoreTests.cs` | Create | Happy path + the three atomicity cases. |
| `tests/Commerce.Integration/AccountEndpointTests.cs` | Modify | Bootstrap now returns org/branch ids; real end-to-end rename authorization. |

## Interfaces / Contracts

```sql
-- deploy/db/migrations/0003_organizations_branches.sql
CREATE TABLE IF NOT EXISTS organizations (
    id         uuid PRIMARY KEY,
    name       text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS branches (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS branches_org_name_unique ON branches (organization_id, name);
CREATE INDEX IF NOT EXISTS branches_organization_id_idx ON branches (organization_id);

ALTER TABLE organizations ENABLE ROW LEVEL SECURITY;
ALTER TABLE organizations FORCE ROW LEVEL SECURITY;
ALTER TABLE branches      ENABLE ROW LEVEL SECURITY;
ALTER TABLE branches      FORCE ROW LEVEL SECURITY;

REVOKE ALL ON organizations, branches FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON organizations TO app_runtime;  -- app_runtime exists from 0001
GRANT SELECT, INSERT, UPDATE ON branches      TO app_runtime;

-- Symmetric isolation, identical in shape to users_tenant_isolation, including
-- the NULLIF(..., '')::uuid pooler-safety hardening from 0001. NEVER regress it.
DROP POLICY IF EXISTS organizations_tenant_isolation ON organizations;
CREATE POLICY organizations_tenant_isolation ON organizations
    USING (id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- branches carries its OWN organization_id so its policy is a direct column
-- comparison, never a subquery through organizations.
DROP POLICY IF EXISTS branches_tenant_isolation ON branches;
CREATE POLICY branches_tenant_isolation ON branches
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
```

Note the `organizations` policy compares `id`, not `organization_id` — the organization *is* the tenant row. That is the one intentional shape difference from `users_tenant_isolation`.

```csharp
public sealed record NewOrganization(Guid Id, string Name);
public sealed record NewBranch(Guid Id, string Name);

public enum BootstrapOutcome
{
    Created,
    OrganizationAlreadyExists,
    OrganizationAlreadyHasUsers,
    EmailAlreadyRegistered,
}

public sealed class PostgresOrganizationStore
{
    public PostgresOrganizationStore(NpgsqlDataSource dataSource, PostgresUserAccountStore userStore);

    /// ONE transaction: set_config -> org-exists guard -> zero-users guard ->
    /// INSERT organizations -> INSERT branches -> userStore.InsertAsync -> COMMIT.
    /// No partial-bootstrap state is representable.
    Task<BootstrapOutcome> TryCreateBootstrapAsync(
        CloudTenantScope scope, NewOrganization organization, NewBranch branch,
        NewUserAccount admin, CancellationToken ct);
}

// PostgresUserAccountStore — new transaction-participating overload.
// Does NOT call set_config and does NOT commit; the caller owns both.
// Throws PostgresException on unique violation so the owning transaction decides.
internal Task InsertAsync(
    NpgsqlConnection connection, NpgsqlTransaction tx,
    CloudTenantScope scope, NewUserAccount user, CancellationToken ct);
```

```csharp
// Endpoints/Account.cs — current shape is
//   BootstrapRequest(Guid OrganizationId, string Token, string Email, string Password)
public sealed record BootstrapRequest(
    Guid OrganizationId, string Token, string OrganizationName, string? BranchName,
    string Email, string Password);

public sealed record BootstrapResponse(Guid OrganizationId, Guid BranchId, Guid UserId);
```

`OrganizationName` is required (blank → 400 `ValidationProblem`, **before** `TryConsume`, so a validation mistake does not burn the token). `BranchName` is optional and defaults to `"Main"`; both names are trimmed. The admin is created with the existing four-permission `admin` role and `branch_scope = [branchId]`.

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Integration (RLS) | `0003` applies twice cleanly; cross-org read of `organizations`/`branches` returns zero rows; unscoped transaction fails closed (zero rows, not a cast error); a `WITH CHECK`-violating insert into `branches` throws | Extend `MigrationRlsTests.cs` with `ResolveOrganizationsMigrationPath` / `ApplyOrganizationsMigration` mirroring the existing `0002` helpers, plus a `ResetOrganizations` truncate helper |
| Integration (**atomicity — the critical test**) | Three RED-first cases, each asserting **zero** rows in `organizations`, `branches`, `users`, and `user_directory` afterwards: (1) org id already exists → `OrganizationAlreadyExists`; (2) org already has a user → `OrganizationAlreadyHasUsers`; (3) the admin insert itself fails mid-transaction — force it with a `user_directory` email that is already registered to a *different* org, so steps 4 and 5 have already run and only step 6 throws. Case (3) is the one that actually proves rollback rather than an early guard. | `OrganizationStoreTests.cs`, live Postgres, `PostgresTestFixture` skip convention |
| Integration (endpoint) | Bootstrap returns `{organizationId, branchId, userId}`; a second bootstrap on the same org → 409; blank `organizationName` → 400 **and the token is still consumable**; omitted `branchName` creates a branch named `Main` | `WebApplicationFactory` in `AccountEndpointTests.cs` |
| Integration (end-to-end authorization) | Bootstrap → sign in → load actor → `actor.BranchScope` contains the created `branchId` → `TenantAuthorizationService.Authorize` with `TargetBranchId = branchId` returns allowed; the same actor with a *different* branch id returns `not-found` | `AccountEndpointTests.cs`, full HTTP path through `/catalog/products/{id}/rename` |
| E2E (Playwright) | "Allowed" rename uses the branch id the real bootstrap path created; "denied" rename targets a different random branch id | `catalog.spec.ts` + `helpers.ts` rewrite (see below) |
| Unit | None new — no new pure-domain logic; `Organization`/`Branch` stay plain read models | — |

### Test seam: recommendation

**Narrow the seam, do not delete it.** `TestSeedEndpoints.cs`'s docblock lists two justifications. This change kills the second one (arbitrary branch scope was fabricated because no branch persistence existed) but **not** the first — the bootstrap token still reaches only server stdout, which an out-of-process browser harness cannot read. So:

- Remove `branchScope` from `TestSeedUserRequest`/`seedUser`. Nothing may hand-fabricate a branch scope anymore.
- Make the seam create org + branch + admin through the **same** `PostgresOrganizationStore.TryCreateBootstrapAsync` the real endpoint calls, and return `branchId` in `TestSeedUserResponse`.
- The seam's only remaining privilege becomes "skip the stdout token hop" — an honest, minimal test affordance instead of a capability the product does not have.
- `catalog.spec.ts`'s "allowed" test then exercises a genuinely real authorization path; its "denied" test becomes a real cross-branch denial instead of an artifact of the empty-scope limitation. Both docblocks must be rewritten — the current ones assert facts this change makes false.

This cleanup is in scope: leaving the seam as-is would keep a comment block in the repo that actively lies about the product.

## Threat Matrix

| Native row | Applicability |
|---|---|
| Documentation-like paths | N/A — no file-classification boundary |
| Git repository selection | N/A — no product code runs Git |
| Commit state | N/A — no commit automation added |
| Push state | N/A — `railway.json` and deploy automation untouched |
| PR commands | N/A — no PR automation added |
| Routing | **Applicable** — no new route, but `/account/bootstrap`'s request shape and write surface expand. Safe behavior: the organization-exists and zero-users guards are both re-checked inside the write transaction; name validation runs before token consumption; the token stays single-use and stdout-only; the response exposes only ids the caller just created. RED tests: duplicate-org 409, second-bootstrap 409, blank-name 400 with the token intact, and the three atomicity cases above. |

No shell, subprocess, or process-integration boundary is introduced.

## Migration / Rollout

Forward-only and additive. Per environment, run `deploy/db/migrations/0003_organizations_branches.sql` via `psql` against the direct (non-pooled) connection exactly as `deploy/README.md` documents for `0001`/`0002`, **before** deploying the new image — `/health/ready` fails closed until the tables exist, so ordering is enforced by the readiness gate rather than by discipline.

**Local dev safety — explicitly confirmed, not assumed.** `0003` contains only `CREATE TABLE IF NOT EXISTS`, `CREATE [UNIQUE] INDEX IF NOT EXISTS`, `ALTER TABLE … ROW LEVEL SECURITY` on the two brand-new tables, `REVOKE`/`GRANT` on the two brand-new tables, and `DROP POLICY IF EXISTS` + `CREATE POLICY` on the two brand-new tables. It contains **zero** statements touching `users`, `user_directory`, or `sync_inbox`, and the only FK it creates points from a new table to another new table. An existing local Postgres that already holds `users`/`sync_inbox` rows from prior verification sessions therefore cannot break: no existing row is read, rewritten, or validated against a new constraint, and pre-existing `users.organization_id` values that match no `organizations` row remain perfectly legal because no FK is added to `users`. Applying `0003` twice is a no-op by the same `IF NOT EXISTS` / `DROP POLICY IF EXISTS` mechanics `0002` already relies on, and `MigrationRlsTests` asserts it.

**Rollback**: revert the `Account.cs` / store / seam commits and optionally `DROP TABLE branches, organizations;` — nothing outside those two tables references them, so the drop cannot cascade into user or inbox data. Reverted bootstrap returns to `branch_scope = '{}'`.

**On the deferred FK retrofit (design's informed opinion).** Adding `users.organization_id → organizations(id)` and `sync_inbox.organization_id → organizations(id)` later is *mechanically* straightforward: `ALTER TABLE … ADD CONSTRAINT … NOT VALID` then `VALIDATE CONSTRAINT` in a second pass, which does not take a long exclusive lock. The real cost is data, not DDL: every organization id that pre-dates this change has no `organizations` row, so validation fails until those rows are backfilled or purged. That is cheap today (dev-only data), and gets more expensive with every deployed tenant — so the retrofit is a *soon* follow-up, not a *someday* one. `branch_scope` is the genuinely hard case and will **not** yield to a plain FK: it is a `uuid[]`, and Postgres has no per-element foreign keys, so enforcing it needs either a trigger or a `user_branches` join table replacing the array. That is a schema change to `users`, well outside this change's shape.

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | Everything: `0003` + dev mirror, `PostgresOrganizationStore` + records, `PostgresUserAccountStore` extraction, `Account.cs`, `Program.cs`, readiness check, test seam + E2E rewrite, all tests | ~750 | `0003` applies twice cleanly; new integration tests green against `deploy/dev/compose.yaml`; full xUnit suite green; `npm run build` + Vitest + Playwright green | Revert one commit; drop the two tables |

Rough breakdown: ~90 SQL across two files, ~130 org store + records, ~40 net user-store refactor, ~55 endpoint, ~25 readiness, ~60 seam + E2E, ~350 tests.

Decision needed before apply: Yes
Chained PRs recommended: No
400-line budget risk: High

**Why one PR despite the budget.** The reviewable unit *is* the transaction. Splitting schema from the transaction that writes it means PR #1 lands two tables nobody writes to, and PR #2 lands the atomicity invariant with its schema already merged and out of the diff — the reviewer would never see the DDL and the transaction side by side, which is exactly the pairing that needs review here. This mirrors `commerce-user-credentials`' own single-PR reasoning. The ~750 estimate sits comfortably inside the ~1,100-line precedent, and roughly half of it is tests. Recommend a `size:exception` acknowledgement rather than a chain. If apply overruns badly, the only defensible split is **PR #1 = `0003` + dev mirror + org store + `MigrationRlsTests`/`OrganizationStoreTests`** (dead code, no behavior change, trivially revertible) and **PR #2 = endpoint + readiness + seam + E2E**, chained onto #1 — never a split that leaves the seam fabricating branch scopes after the real path exists.

## Open Questions

- [ ] `src/Commerce.Web/src/api/types.ts` — confirm during apply whether any SPA type mirrors `BootstrapRequest`. The SPA does not call bootstrap today, so this is expected to be a no-op row in the file table.
- [ ] `POST /account/bootstrap/request-token` still takes a client-chosen `organizationId` and only checks "no users yet". After this change it could additionally reject an id that already exists in `organizations` — same answer either way (409), so it is a clarity improvement, not a correctness one. Cheap to add in the same PR; flagged rather than silently included.
