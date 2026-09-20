# Design: Commerce role taxonomy

## Technical Approach

Two structurally different pieces land together.

**(1) Staff role taxonomy — additive, no schema change.** A static
`RoleCatalog` in `Commerce.Domain/Identity` maps canonical names to `Permission`
sets. The on-disk shape is unchanged: `users.roles` stays the jsonb
`[{"name":…,"permissions":<int>}]` array `PostgresUserAccountStore` already
serializes. Two routes join the existing `ManageUsers`-gated `/account/users`
group created by `commerce-password-recovery`, reusing its exact authorization
shape (`RequireAuthorization` + `TenantScopeEndpointFilter` + `LoadActorAsync`
+ `EffectivePermissions.HasFlag`), plus a pure `RoleGrantPolicy` grant-cap
check. The `admin` → `business-admin` rename is a forward-only jsonb rewrite.

**(2) Platform administration — a parallel identity plane.** A `platform_admins`
table with no `organization_id` column, its own `PasswordHasher<PlatformAdmin>`,
its own cookie scheme (`CloudAuthenticationSchemes.PlatformAdminCookie`), and a
`/platform` group that requires **only** that scheme and does **not** carry
`TenantScopeEndpointFilter`.

**The crux — cross-org Postgres access — resolves into two tiers, and the first
tier needs no new mechanism at all.** Every *write* a platform admin performs in
this slice targets exactly one, explicitly named organization. That is not a
cross-org operation: the store sets
`set_config('app.current_org_id', <organization id from the request body>, true)`
and every existing `FORCE ROW LEVEL SECURITY` policy applies unchanged. The only
difference from an org-scoped call is the *provenance* of the org id (request
body instead of cookie claim), which is exactly what the `PlatformAdminCookie`
scheme authorizes and what every audit row records. `TryCreateBootstrapAsync` is
reused verbatim. Only one operation is genuinely cross-org — listing
organizations — and it gets its own least-privilege Postgres login rather than
any bypass. See the decisions table.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Platform-admin write path (bootstrap an org, force-create its first `business-admin`)** | **No new database capability.** `app_runtime` + transaction-local `set_config('app.current_org_id', $targetOrgId, true)`, where `$targetOrgId` comes from the request body and is echoed into the audit row. Every existing policy (`organizations_tenant_isolation`, `users_tenant_isolation`, `branches_tenant_isolation`, `user_directory_lookup`) applies verbatim and unchanged; the write is confined to exactly one organization by the database, not by C#. The blast radius of a compromised platform-admin session is therefore "one organization per authenticated, audited call", never "all rows". `PostgresOrganizationStore.TryCreateBootstrapAsync` is reused with zero modification to its SQL. | **BYPASSRLS on a platform role** — turns one credential into unconditional read/write over every tenant's users, hashes, and reset tokens, and makes every existing policy untestable for that path. Rejected outright. **`SECURITY DEFINER` bootstrap function** — the repo has no PL/pgSQL, `FORCE ROW LEVEL SECURITY` filters the definer (the table owner) too, so it would need a bypass anyway or its own policy; it buys nothing over parameterized `set_config` and hides the write behind DDL that no integration test currently exercises. |
| **Platform-admin cross-org *read* (`GET /platform/organizations`)** | A **second, least-privilege Postgres login** `platform_readonly`, created in `0007` with the `__PLATFORM_READONLY_PASSWORD__` placeholder convention `0001` established for `app_runtime`. Its *entire* privilege set in the database is a **column-level** `GRANT SELECT (id, name, created_at) ON organizations`, paired with `CREATE POLICY organizations_platform_read ON organizations FOR SELECT TO platform_readonly USING (true)`. A `TO role` policy applies to nobody else, so `app_runtime`'s isolation is untouched. The role has no grant on `users`, `user_directory`, `branches`, `password_reset_tokens`, `device_credentials`, `sync_inbox`, `platform_admins`, or `audit_log` — reading a tenant secret through this login is not "blocked", it is **unrepresentable**. A second `NpgsqlDataSource` (keyed, `ConnectionStrings:CommercePlatformRead`) is injected only into `PostgresPlatformAdminStore.ListOrganizationsAsync`. Missing connection string ⇒ the route returns 503 and startup logs a warning (the `LogOnlyEmailSender` graceful-degradation precedent); it never silently falls back to `app_runtime`. | **A GUC-gated policy** (`USING (NULLIF(current_setting('app.platform_admin_id', true), '') IS NOT NULL)` on `app_runtime`) — cheaper, but it makes cross-tenant reads reachable from the *same* connection pool and the *same* role that serves every tenant request, so one mis-scoped store method or one injected `set_config` re-opens the whole database. The privilege boundary belongs in Postgres, not in C# discipline. **A view** — `security_invoker = off` executes as the view owner, and `FORCE ROW LEVEL SECURITY` applies to the owner, so a view returns zero rows without a policy anyway. |
| **`platform_admins` table shape and RLS** | Not tenant data, so it carries no `organization_id` and no tenant policy. `ENABLE` + `FORCE ROW LEVEL SECURITY`, `REVOKE ALL FROM PUBLIC`. Grants to `app_runtime` are deliberately asymmetric and column-scoped: `SELECT`, `INSERT`, and `UPDATE (last_sign_in_at_utc)` — **no** `UPDATE` on `password_hash` or `email` and **no** `DELETE`, because this slice has no platform password-change or deletion flow, so those mutations are structurally unavailable rather than merely unimplemented. Policies: `FOR SELECT USING (true)` (sign-in resolves an email before any scope exists — the `user_directory_lookup` precedent, and there is no tenant dimension to leak); `FOR INSERT WITH CHECK (NOT EXISTS (SELECT 1 FROM platform_admins))` — **genesis-only**: the API can create the first platform admin and can never create a second. | An `organization_id` column set to a sentinel — makes a cross-tenant superuser look like tenant data and would let it be returned by a tenant-scoped query. An unconditional INSERT policy — a bug in the genesis endpoint would mint unlimited platform admins. |
| **Platform genesis endpoint gating** | `POST /platform/bootstrap/request-token` + `POST /platform/bootstrap`, reusing the existing singleton `BootstrapTokenRegistry` keyed by `Guid.Empty` (the platform pseudo-organization). Identical shape to `/account/bootstrap`: the plaintext token reaches only server stdout (`railway logs`), the HTTP response is always an empty-body 202, and the token is consumed before the transaction. The database `NOT EXISTS` policy is the second, independent barrier. | An unguarded genesis endpoint racing the internet at first deploy — the exact hole the org bootstrap token already exists to close. A migration-seeded row — the password hash must be produced by `PasswordHasher`, not by SQL. |
| **Scheme mutual exclusivity** | `Program.cs` adds a second `.AddCookie(CloudAuthenticationSchemes.PlatformAdminCookie, …)` with its **own** `Cookie.Name = "commerce.platform"` and `Cookie.Path = "/platform"`, and a named policy `PlatformAdmin` = `.AddAuthenticationSchemes(PlatformAdminCookie).RequireAuthenticatedUser()` (the exact `DeviceBearer` precedent already in `Program.cs`). The `/platform` group uses `.RequireAuthorization("PlatformAdmin")`; every org group keeps bare `.RequireAuthorization()`, which resolves to `DefaultScheme` = `CookieAuthenticationDefaults.AuthenticationScheme` only. Because each policy names its schemes explicitly, an org cookie authenticates nothing under `/platform` and a platform cookie authenticates nothing under `/account` — and the platform cookie is not even *sent* to `/account` (`Path=/platform`). The platform identity carries **no** `org_id` claim, so `TenantScopeResolver.TryResolve` fails closed on it by construction. | A `platform-admin` role/flag on `UserAccount` — one compromised org session, one jsonb write, or one grant-cap bug becomes cross-tenant. Explicitly rejected in the proposal. Reusing the default cookie with an extra claim — a single forged/leaked cookie crosses both planes. |
| **`RoleCatalog` shape** | `public static class RoleCatalog` in `Commerce.Domain/Identity`: `const string` names, a `FrozenDictionary<string, Permission>` (case-insensitive ordinal), `bool TryResolve(string name, out Role role)`, and `IReadOnlySet<string> OrgAssignable` (= catalog minus `platform-admin`). Lookup is by string because the wire and the persisted jsonb are both name-keyed; an enum would need a parallel string map plus a parse failure mode for legacy rows, for no gain. Permissions are **read from the catalog only** — the request DTO has no permissions field at all, so "permissions from the body" is not validated-away, it is unrepresentable. | A DB-backed catalog table — tenants must not edit role definitions (out of scope) and it adds a migration plus a cache. An enum with `[Description]` attributes — reflection at every sign-in for a four-entry map. |
| **Grant-cap location** | A pure static `RoleGrantPolicy.TryAuthorize(UserAccount caller, IReadOnlyList<string> requestedNames, out IReadOnlyList<RoleDto> roles, out GrantDenial denial)` in `Commerce.Domain/Identity`, called by **both** new endpoints before any I/O. Denials: `UnknownRole`, `ReservedRole` (`platform-admin` from an org-scoped caller — checked *before* the subset math, so it is refused even for a caller holding every flag), `ExceedsCallerPermissions` (`(union & ~caller.EffectivePermissions) != 0`). It is *not* wired into the existing `/account/users/{id}/reset-password` route: that route grants no roles, and changing its authorization shape would be an unrequested behavior change. | Enforcement inline in each handler — duplicated, and duplicated security logic drifts. A `TenantAuthorizationService` action — that service is branch/action-oriented and shared with POS; `commerce-password-recovery` already rejected it for the same reason. |
| **Audit table shape and RLS** | `audit_log` (append-only) with `organization_id uuid NULL` — platform sign-in and genesis have no owning organization, and a sentinel uuid would be a lie. `ENABLE` + `FORCE ROW LEVEL SECURITY`, `REVOKE ALL FROM PUBLIC`, and **`GRANT INSERT ONLY`** to `app_runtime` (no SELECT, no UPDATE, no DELETE). One policy: `FOR INSERT WITH CHECK (organization_id IS NULL OR organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)` — an org actor can only write history for its own organization, and a platform write passes because the store has already scoped the transaction to the explicit target org. "Nobody can read audit rows through the API" is therefore enforced by a missing grant *and* a missing policy, not by the absence of an endpoint. The insert uses no `RETURNING` (it has no SELECT privilege). Reading later requires an explicit new policy — a deliberate, reviewable future change; the inverse/read statements ship commented in `0007`. | Reusing `Commerce.Application.Audit.IAuditSink` — its `void Record(AuditEntry)` is synchronous, fire-and-forget, in-memory, has a non-nullable `OrganizationId`, and carries no entity id or old/new value; it cannot enlist in an `NpgsqlTransaction`, which is the whole requirement. Extending it would change a type shared with POS. The duplication is a known, recorded seam. |
| **Audit write mechanism** | A transaction-participating static `AuditLogWriter.InsertAsync(NpgsqlConnection, NpgsqlTransaction, UserManagementAuditEntry, CancellationToken)` in `src/Commerce.Cloud.Api/Auditing/`, following `PostgresUserAccountStore.InsertAsync`'s exact convention: the caller owns the connection, the transaction, and `set_config`; this method never commits, never rolls back, never calls `set_config`. Called explicitly from inside each owning store method, so "same transaction as the mutating write" is structural — the audit row and the mutation commit together or not at all. | Middleware / endpoint filter — runs outside the store's transaction, so it records *attempts*, not committed facts, and a rolled-back mutation would still be audited. A `NOTIFY`/background writer — loses rows on crash, which defeats the purpose. A Postgres trigger — invisible to the C# tests and cannot see the actor identity without yet another GUC. |
| **Rename migration must defeat FORCE RLS** | The rewrite runs as the migration operator, and `users` has `FORCE ROW LEVEL SECURITY`, so with no `app.current_org_id` set the owner is filtered by `users_tenant_isolation` too and a naive `UPDATE` would silently touch **zero rows**. `0006` therefore wraps the rewrite in `BEGIN; ALTER TABLE users NO FORCE ROW LEVEL SECURITY; <UPDATE>; ALTER TABLE users FORCE ROW LEVEL SECURITY; COMMIT;` plus a `DO $$ … RAISE EXCEPTION $$` assertion that no `"admin"` entry survives. FORCE is off only inside one transaction, for the owner only (`ENABLE` still binds `app_runtime` throughout), and the file aborts rather than half-applying. | Running the `UPDATE` unwrapped — the silent-zero-rows trap: the migration "succeeds", nobody notices, and every existing org keeps an unknown role name. Relying on the operator role having `BYPASSRLS` — Supabase does not guarantee that attribute, and a migration must not depend on an unverified role attribute. |
| **Migration file split** | **Two files.** `0006_role_taxonomy.sql` = the data rewrite only (transactional, FORCE-toggling, self-asserting, idempotent because the second run matches zero rows). `0007_platform_administration.sql` = additive DDL (`platform_admins`, `audit_log`, `platform_readonly` role + column grant + `TO`-scoped policy). Different failure modes (a one-shot data rewrite vs. re-runnable `CREATE … IF NOT EXISTS`), different rollback statements, and only `0007` carries a `__PLATFORM_READONLY_PASSWORD__` placeholder — keeping the secret-placeholder file separate from the data rewrite preserves `0001`'s convention and lets the two land in different chained PRs. | One `0006` doing both — an abort in the rewrite half leaves the reviewer unsure which half applied, and it forces the rename and the platform plane into one PR. |
| **Relationship to the existing org bootstrap-token flow** | **Coexist, via shared logic.** `POST /platform/organizations` calls the same `PostgresOrganizationStore.TryCreateBootstrapAsync` that `/account/bootstrap` calls; only the audit row and the authorization in front of it differ. The anonymous `/account/bootstrap/request-token` + `/account/bootstrap` pair is left byte-identical (apart from the `"admin"` → `RoleCatalog.BusinessAdmin` literal) and is documented as the deprecated operator path in `deploy/README.md`. | Deleting the anonymous pair in this change — it is the only self-service onboarding path today, its removal has independent rollback risk, and the proposal explicitly does not require it. Duplicating the bootstrap SQL under `/platform` — two transactions to keep in sync; the whole point of `TryCreateBootstrapAsync` was single ownership. |

## Data Flow

```text
Create staff user (org-scoped)
  POST /account/users {email, password, roleNames[], branchIds[]}
    -> RequireAuthorization (default cookie) + TenantScopeEndpointFilter -> scope
    -> caller = userStore.LoadActorAsync(scope, NameIdentifier)
       -> null | IsRevoked | !HasFlag(ManageUsers) -> 403
    -> RoleGrantPolicy.TryAuthorize(caller, roleNames)     [pure, before any I/O]
       -> UnknownRole | ReservedRole | ExceedsCallerPermissions -> 400 / 403
    -> branchIds not a subset of caller-org branches -> 400
    -> ONE tx: set_config(scope)
               -> userStore.InsertAsync  (users + user_directory)
               -> AuditLogWriter.InsertAsync(user.created, new=roleNames)
               -> COMMIT            <- mutation and audit row are atomic
    -> 201 {userId}

Assign roles (org-scoped)
  PUT /account/users/{userId}/roles {roleNames[]}
    -> same caller gate; target = LoadActorAsync(scope, userId)
       -> null -> 404   (cross-org target is null by RLS == "no such user")
    -> RoleGrantPolicy.TryAuthorize(caller, roleNames)
    -> ONE tx: set_config -> UPDATE users SET roles = $1::jsonb WHERE id = $2
                          -> AuditLogWriter (old = target's prior role names,
                                             new = requested role names)
                          -> COMMIT
    -> 204

Platform sign-in (no org anywhere)
  POST /platform/sign-in {email, password}
    -> platformStore.FindByEmailAsync(email)        [UNSCOPED; SELECT USING(true)]
       -> null -> dummy VerifyHashedPassword (timing parity) -> 401
    -> PasswordHasher<PlatformAdmin>.VerifyHashedPassword -> Failed -> 401
    -> SignInAsync(PlatformAdminCookie, claims = {NameIdentifier, Name})
       NO org_id claim exists -> TenantScopeResolver fails closed on this identity
    -> UPDATE platform_admins SET last_sign_in_at_utc = now()  + audit row
    -> 200

List organizations (the ONLY genuinely cross-org read)
  GET /platform/organizations
    -> RequireAuthorization("PlatformAdmin")   <- default cookie authenticates nothing here
    -> platform_readonly datasource absent -> 503
    -> SELECT id, name, created_at FROM organizations ORDER BY name
       (platform_readonly login; organizations_platform_read TO platform_readonly;
        column grant makes any other column or table a privilege error)
    -> 200 [{id, name, createdAt}]

Bootstrap an organization (platform-scoped write, explicit target org)
  POST /platform/organizations {organizationName, branchName?, adminEmail, adminPassword}
    -> RequireAuthorization("PlatformAdmin")
    -> organizationId = Guid.NewGuid()          <- server-minted, never caller-supplied
    -> TryCreateBootstrapAsync(new CloudTenantScope(organizationId), ...,
         roles: [RoleCatalog.BusinessAdmin], audit: platform-admin actor)
       set_config('app.current_org_id', organizationId) -> EVERY existing policy
       applies unchanged; the write cannot touch a second organization
    -> audit row (actor_kind='platform-admin', organization_id=organizationId)
       inside the SAME transaction
    -> 201 {organizationId, branchId, userId}
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `deploy/db/migrations/0006_role_taxonomy.sql` | Create | Transactional `"admin"` → `"business-admin"` jsonb rewrite with the `NO FORCE`/`FORCE` toggle and a post-condition assertion. |
| `deploy/db/migrations/0007_platform_administration.sql` | Create | `platform_admins`, `audit_log`, `platform_readonly` role, column grant, `TO`-scoped `organizations` read policy. |
| `deploy/dev/db/init-rls.sql` | Modify | Same DDL appended verbatim (hand-kept parity convention; `MigrationRlsTests` asserts it). |
| `deploy/README.md` | Modify | `0006`/`0007` apply sections, `__PLATFORM_READONLY_PASSWORD__` handling, `ConnectionStrings__CommercePlatformRead`, platform-genesis runbook, deprecation note on `/account/bootstrap`. |
| `deploy/staging-runbook.md` | Modify | The new variable in the per-environment checklist. |
| `src/Commerce.Domain/Identity/RoleCatalog.cs` | Create | Canonical name → `Permission` map; `TryResolve`; `OrgAssignable`. |
| `src/Commerce.Domain/Identity/RoleGrantPolicy.cs` | Create | Pure grant-cap + reserved-role + unknown-role decision. |
| `src/Commerce.Domain/Identity/PlatformAdmin.cs` | Create | `Id`, `Email` — no `OrganizationId`. Hasher generic argument. |
| `src/Commerce.Cloud.Api/Auditing/UserManagementAuditEntry.cs` | Create | The persisted record shape. |
| `src/Commerce.Cloud.Api/Auditing/AuditLogWriter.cs` | Create | Transaction-participating `InsertAsync`; no commit, no `set_config`, no `RETURNING`. |
| `src/Commerce.Cloud.Api/Persistence/PostgresPlatformAdminStore.cs` | Create | `FindByEmailAsync` (unscoped), `TryCreateGenesisAsync`, `TouchLastSignInAsync`, `ListOrganizationsAsync` (platform_readonly datasource). |
| `src/Commerce.Cloud.Api/Persistence/PostgresUserAccountStore.cs` | Modify | `CreateStaffUserAsync` (owns a tx: `set_config` → `InsertAsync` → audit), `ReplaceRolesAsync` (tx: update + audit), `ListRoleNamesAsync`. |
| `src/Commerce.Cloud.Api/Persistence/PostgresOrganizationStore.cs` | Modify | `TryCreateBootstrapAsync` accepts an optional `UserManagementAuditEntry` written inside its existing transaction. SQL otherwise untouched. |
| `src/Commerce.Cloud.Api/Authentication/DeviceBearerAuthenticationHandler.cs` | Modify | Add `CloudAuthenticationSchemes.PlatformAdminCookie` const (the file already owns that class). |
| `src/Commerce.Cloud.Api/Endpoints/PlatformAdmin.cs` | Create | `/platform` group: genesis pair, sign-in, sign-out, list organizations, create organization. |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modify | `POST /account/users`, `PUT /account/users/{userId}/roles`; bootstrap literal → `RoleCatalog.BusinessAdmin`. |
| `src/Commerce.Cloud.Api/Program.cs` | Modify | Second `AddCookie`, `PlatformAdmin` policy, `PasswordHasher<PlatformAdmin>`, keyed platform-read `NpgsqlDataSource` (conditional), `PostgresPlatformAdminStore`, `MapPlatformAdminEndpoints`. |
| `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` | Modify | Add `platform_admins` and `audit_log` (+ `relforcerowsecurity` + policy names) to the single readiness query and both messages. |
| `tests/Commerce.Cloud/RoleCatalogTests.cs`, `RoleGrantPolicyTests.cs` | Create | Pure unit coverage. |
| `tests/Commerce.Integration/RoleTaxonomyTests.cs`, `PlatformAdminTests.cs` | Create | Endpoint + store coverage against live Postgres. |
| `tests/Commerce.Integration/MigrationRlsTests.cs` | Modify | Apply `0006`/`0007`; re-apply idempotency; the privilege assertions below. |

## Interfaces / Contracts

```sql
-- 0006_role_taxonomy.sql (data rewrite; run as the migration owner)
BEGIN;
ALTER TABLE users NO FORCE ROW LEVEL SECURITY;   -- owner-only, inside this tx
UPDATE users
   SET roles = (
       SELECT jsonb_agg(
           CASE WHEN e->>'name' = 'admin'
                THEN jsonb_set(e, '{name}', '"business-admin"')
                ELSE e END)
       FROM jsonb_array_elements(roles) AS e)
 WHERE roles @> '[{"name":"admin"}]';
ALTER TABLE users FORCE ROW LEVEL SECURITY;
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM users WHERE roles @> '[{"name":"admin"}]') THEN
    RAISE EXCEPTION '0006: legacy "admin" role entries survived the rewrite';
  END IF;
END $$;
COMMIT;
-- Inverse (rollback): the same block with the two names swapped.
```

```sql
-- 0007_platform_administration.sql (additive)
CREATE TABLE IF NOT EXISTS platform_admins (
    id                  uuid PRIMARY KEY,
    email               text NOT NULL UNIQUE,   -- normalized (lower/trim)
    password_hash       text NOT NULL,
    created_at_utc      timestamptz NOT NULL DEFAULT now(),
    last_sign_in_at_utc timestamptz NULL
);                                  -- no organization_id: NOT tenant data

CREATE TABLE IF NOT EXISTS audit_log (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at_utc timestamptz NOT NULL DEFAULT now(),
    actor_kind      text NOT NULL,   -- 'org-user' | 'platform-admin'
    actor_id        uuid NOT NULL,
    organization_id uuid NULL,       -- NULL for platform sign-in / genesis
    entity_type     text NOT NULL,   -- 'user' | 'organization' | 'platform-admin'
    entity_id       uuid NOT NULL,
    action          text NOT NULL,   -- 'user.created' | 'user.roles.assigned' | ...
    old_value       jsonb NULL,
    new_value       jsonb NULL
);
CREATE INDEX IF NOT EXISTS audit_log_entity_idx ON audit_log (entity_type, entity_id, occurred_at_utc DESC);
CREATE INDEX IF NOT EXISTS audit_log_org_idx    ON audit_log (organization_id, occurred_at_utc DESC);

ALTER TABLE platform_admins ENABLE ROW LEVEL SECURITY;
ALTER TABLE platform_admins FORCE ROW LEVEL SECURITY;
ALTER TABLE audit_log       ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_log       FORCE ROW LEVEL SECURITY;
REVOKE ALL ON platform_admins, audit_log FROM PUBLIC;

GRANT SELECT, INSERT ON platform_admins TO app_runtime;
GRANT UPDATE (last_sign_in_at_utc) ON platform_admins TO app_runtime;  -- column-scoped
GRANT INSERT ON audit_log TO app_runtime;                              -- append-only; NO SELECT

DROP POLICY IF EXISTS platform_admins_lookup ON platform_admins;
CREATE POLICY platform_admins_lookup ON platform_admins FOR SELECT USING (true);
DROP POLICY IF EXISTS platform_admins_touch ON platform_admins;
CREATE POLICY platform_admins_touch  ON platform_admins FOR UPDATE USING (true) WITH CHECK (true);
-- Genesis-only: a SECOND platform admin is unrepresentable through the app.
DROP POLICY IF EXISTS platform_admins_genesis ON platform_admins;
CREATE POLICY platform_admins_genesis ON platform_admins FOR INSERT
    WITH CHECK (NOT EXISTS (SELECT 1 FROM platform_admins));

-- An org actor can only write history for its OWN organization; a platform
-- write passes because its tx is already scoped to the explicit target org.
DROP POLICY IF EXISTS audit_log_append ON audit_log;
CREATE POLICY audit_log_append ON audit_log FOR INSERT
    WITH CHECK (organization_id IS NULL
                OR organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
-- No SELECT policy and no SELECT grant, by design. Reading audit rows is a
-- future, explicitly reviewed change:
--   GRANT SELECT ON audit_log TO <reader>;
--   CREATE POLICY audit_log_read ON audit_log FOR SELECT TO <reader> USING (...);

-- The ONLY cross-organization read capability in the system.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'platform_readonly') THEN
        CREATE ROLE platform_readonly WITH LOGIN PASSWORD '__PLATFORM_READONLY_PASSWORD__';
    ELSE
        ALTER ROLE platform_readonly WITH LOGIN PASSWORD '__PLATFORM_READONLY_PASSWORD__';
    END IF;
END $$;
GRANT USAGE ON SCHEMA public TO platform_readonly;
GRANT SELECT (id, name, created_at) ON organizations TO platform_readonly;  -- and nothing else, anywhere
DROP POLICY IF EXISTS organizations_platform_read ON organizations;
CREATE POLICY organizations_platform_read ON organizations
    FOR SELECT TO platform_readonly USING (true);   -- TO-scoped: app_runtime unaffected
-- Rollback: DROP POLICY organizations_platform_read ON organizations;
--           DROP OWNED BY platform_readonly; DROP ROLE platform_readonly;
--           DROP TABLE audit_log, platform_admins;
```

```csharp
// Commerce.Domain/Identity/RoleCatalog.cs
public static class RoleCatalog
{
    public const string BusinessAdmin = "business-admin";
    public const string Seller        = "seller";
    public const string Provider      = "provider";
    public const string PlatformAdmin = "platform-admin";   // never org-assignable

    public static bool TryResolve(string name, out Role role);
    public static IReadOnlySet<string> OrgAssignable { get; }  // all except PlatformAdmin
}
// business-admin = ViewSales|ManageCatalog|ManageUsers|ManageBranchSettings (today's exact set)
// seller = ViewSales      provider = Permission.None      platform-admin = Permission.None
//   (platform-admin's capability lives in the PlatformAdminCookie scheme, not in a flag)

// Commerce.Domain/Identity/RoleGrantPolicy.cs
public enum GrantDenial { None, UnknownRole, ReservedRole, ExceedsCallerPermissions }
public static bool TryAuthorize(
    UserAccount caller, IReadOnlyList<string> requestedNames,
    out IReadOnlyList<RoleDto> roles, out GrantDenial denial);

// Commerce.Cloud.Api/Auditing
public sealed record UserManagementAuditEntry(
    string ActorKind, Guid ActorId, Guid? OrganizationId,
    string EntityType, Guid EntityId, string Action,
    string? OldValueJson, string? NewValueJson);
internal static Task InsertAsync(NpgsqlConnection c, NpgsqlTransaction tx,
    UserManagementAuditEntry entry, CancellationToken ct);   // never commits, never set_config

// Endpoints
public sealed record CreateUserRequest(string Email, string Password, string[] RoleNames, Guid[] BranchIds);
public sealed record CreateUserResponse(Guid UserId);
public sealed record AssignRolesRequest(string[] RoleNames);
public sealed record PlatformSignInRequest(string Email, string Password);
public sealed record PlatformBootstrapTokenRequest();                     // empty body
public sealed record PlatformGenesisRequest(string Token, string Email, string Password);
public sealed record OrganizationSummary(Guid Id, string Name, DateTimeOffset CreatedAt);
public sealed record CreateOrganizationRequest(
    string OrganizationName, string? BranchName, string AdminEmail, string AdminPassword);
public sealed record CreateOrganizationResponse(Guid OrganizationId, Guid BranchId, Guid UserId);
```

`CreateUserRequest` has **no** permissions field: catalog-only permissions are a
type-level property, not a validation rule.

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | `RoleCatalog`: every canonical name resolves to its exact `Permission` set; `provider` is `None`; unknown name fails; `OrgAssignable` excludes `platform-admin` | xUnit |
| Unit | `RoleGrantPolicy`: subset grant allowed; superset denied `ExceedsCallerPermissions`; `platform-admin` denied `ReservedRole` **even for an all-flags caller**; unknown name denied; empty list allowed | xUnit, no I/O |
| Integration | `POST /account/users`: `business-admin` creates a `seller` who then signs in; `ManageUsers`-less caller ⇒ 403; branch id outside the caller's org ⇒ 400; a body that smuggles permissions cannot change the stored set | `WebApplicationFactory` + live Postgres |
| Integration | `PUT .../roles`: role change persists; cross-org target ⇒ 404 identical to a nonexistent id; grant-cap and reserved-role denials | same, two seeded orgs |
| Integration | Audit: each of create/assign/bootstrap writes exactly one row with actor, entity, org, action, old/new; **a rolled-back mutation writes no audit row** (force a unique violation) | same, read via an owner-privileged test connection |
| Integration | Scheme exclusivity: an org cookie on `/platform/*` ⇒ 401; a platform cookie on `/account/users` ⇒ 401; the platform cookie is not sent to `/account` (`Path`); a platform identity has no `org_id` claim | same |
| Integration | Platform genesis: token pair works once; a second genesis attempt is rejected by the `NOT EXISTS` policy even when the endpoint is called directly | same |
| Integration | Platform bootstrap org: creates org + branch + `business-admin`, who signs in; the audit row names the platform actor and the new org | same |
| Integration (RLS) | `platform_readonly` can `SELECT id, name, created_at` from `organizations` across two seeded orgs with no `app.current_org_id` set, but **fails with a privilege error** on `users`, `user_directory`, `branches`, `password_reset_tokens`, `device_credentials`, `sync_inbox`, `platform_admins`, and `audit_log`, and on any `INSERT`/`UPDATE`/`DELETE` against `organizations` | `MigrationRlsTests`, direct connections |
| Integration (RLS) | `app_runtime` cannot `SELECT` from `audit_log` at all; cannot `UPDATE audit_log`; cannot insert an audit row for another org; cannot `UPDATE platform_admins.password_hash` | same |
| Integration (migration) | `0006` rewrites a pre-seeded `"admin"` user, that user's permission set is byte-identical, they still sign in, and a second run is a no-op; `0007` re-applies cleanly | same |

## Threat Matrix

| Native row | Applicability |
|---|---|
| Documentation-like paths | N/A — no file-classification boundary |
| Git repository selection | N/A — no product code runs Git |
| Commit state | N/A — no commit automation added |
| Push state | N/A — `railway.json` untouched |
| PR commands | N/A — no PR automation added |
| Routing | **Applicable** — two new authenticated org routes and six new `/platform` routes, two of which are anonymous (genesis pair). Safe behavior: org routes go through `RequireAuthorization` + `TenantScopeEndpointFilter` + a store-loaded actor + `ManageUsers` + `RoleGrantPolicy`; `/platform` routes require the `PlatformAdmin` policy naming only `PlatformAdminCookie`, so the default cookie authenticates nothing there and vice versa; the genesis pair mirrors `/account/bootstrap`'s log-only token and is additionally closed forever by the `NOT EXISTS` INSERT policy; platform sign-in returns one generic 401 on every failure branch with dummy-hash timing parity. RED tests: cross-scheme cookie rejection, second-genesis rejection, grant-cap escalation, reserved-role grant, cross-org target, missing permission (see Testing Strategy). |
| Process integration | **Applicable** — one new outbound database identity (`platform_readonly`). Safe behavior: LOGIN role with a single column-level `SELECT` grant and a `TO`-scoped policy; its connection string is a distinct env var, never logged; when absent the route fails closed with 503 and never falls back to `app_runtime`. RED tests: the privilege-error assertions above, plus the missing-connection-string 503. |
| Shell / subprocess | N/A — none introduced. |

## Migration / Rollout

Forward-only. Per environment, **before** deploying the new image: apply
`0006_role_taxonomy.sql` then `0007_platform_administration.sql` via `psql`
against the direct (non-pooled) connection, replacing
`__PLATFORM_READONLY_PASSWORD__` with a freshly generated per-environment
password immediately before piping (the `0001` convention). `/health/ready`
fails closed until `platform_admins` and `audit_log` exist, so ordering is
enforced by the gate, not by discipline. Then set
`ConnectionStrings__CommercePlatformRead` and deploy. Finally run the platform
genesis once: `POST /platform/bootstrap/request-token`, read the token from
`railway logs`, `POST /platform/bootstrap`. The window closes permanently after
that first row — if genesis is not run promptly after deploy, anyone who can
read the logs *and* reach the API can claim it, so treat it as an immediate
post-deploy step, not a later chore.

No session impact: existing org cookies are untouched (no claim change).

Rollback: revert the commit, then run `0006`'s inverse block (names swapped) and
`0007`'s `DROP` block, both shipped as comments in their files.

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | `RoleCatalog` + `RoleGrantPolicy` + unit tests + the `"admin"` literal in `Account.cs` → `RoleCatalog.BusinessAdmin` + `0006` + dev init sync + `MigrationRlsTests` for the rewrite | ~230 | `0006` applies twice cleanly, seeded `"admin"` user renamed and still signs in | Revert; run the inverse rewrite |
| 2 | `0007` + dev init sync + `AuditLogWriter` + `UserManagementAuditEntry` + readiness check + RLS/privilege tests | ~280 | New tables exist, forced RLS and every privilege assertion green; no behavior change yet | Revert; `DROP TABLE` |
| 3 | `POST /account/users` + `PUT .../roles` + store methods with in-transaction audit + integration tests | ~330 | Grant cap, reserved role, cross-org 404, atomic audit | Revert; the tables become unused, not broken |
| 4 | `PlatformAdmin.cs` + `PostgresPlatformAdminStore` + second cookie scheme + policy + platform-read datasource + `Program.cs` + `TryCreateBootstrapAsync` audit parameter + integration tests + README/runbook | ~400 | Scheme exclusivity, genesis-once, list orgs, bootstrap org, 503 without the connection string | Revert; drop the role and policy |

Decision needed before apply: Yes
Chained PRs recommended: Yes
400-line budget risk: High

**Why chained**: ~1 240 authored lines, three times the budget, and the slices
are genuinely independent and ordered by dependency. Unit 1 is pure domain plus a
data rewrite with no new surface. Unit 2 is dead schema plus a writer nobody
calls. Unit 3 uses both on the already-authorized org plane. Unit 4 adds the
platform plane last, so the highest-risk reviewable (a second auth scheme and a
second database login) arrives on its own, against an already-merged and tested
audit path. No slice leaves a security hole open across a merge: the platform
endpoints cannot land before the audit writer that records them exists. Feature
Branch Chain — PR #1 targets the feature branch, #2 targets #1, #3 targets #2,
#4 targets #3.

## Open Questions

- [ ] None blocking. Three accepted assumptions, each with a documented
      fallback: the migration operator owns `users` (true for every `0001`–`0005`
      apply, and the `NO FORCE` toggle needs ownership, not superuser); the
      `platform_readonly` connection string is provisioned per environment
      (absent ⇒ a 503 route, never a silent `app_runtime` fallback); and
      `Commerce.Application.Audit.IAuditSink` stays in place unchanged — the
      persisted `audit_log` path is deliberately separate, and unifying the two
      is a recorded future seam, not a gap in this change.
