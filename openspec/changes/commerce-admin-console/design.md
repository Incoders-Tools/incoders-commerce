# Design: Commerce Admin Console

## Technical Approach

Two structurally different pieces land together, same split as
`commerce-role-taxonomy`'s design, but this time the second piece
*reverses* that phase's isolation decision instead of extending it.

**(1) New UI surfaces, no new server architecture.** The Web Users screen
follows `CustomersScreen`/`RequireAdmin` verbatim; the POS staff/role
window follows `CustomersWindow`/`CustomerAdminClient` verbatim (a
window-scoped `HttpClient` + `CookieContainer`, discarded on close, DI'd
as a `Func<UserAdminClient>` factory exactly like
`_customerAdminClientFactory`). Branch management is a third screen using
the same precedent. None of this needs new patterns.

**(2) Merge platform-admin into the unified identity model.** The
confirmed decision requires one login surface and permission-driven
visibility, not a rebuild of `UserAccount`. The chosen mechanism is
**Option (b) from proposal.md**: an `IsSystemAdmin` boolean column on
`UserAccount`/`user_directory`, orthogonal to the org-scoped `Permission`
bitmask and to `OrganizationId`. `RoleCatalog`, `RoleGrantPolicy`, and the
existing `platform-admin` catalog entry (`Permission.None`, excluded from
`OrgAssignable`) are **left untouched** — `IsSystemAdmin` is a
capability check, not a role, and a sysadmin's `UserAccount` still
belongs to exactly one organization (a reserved pseudo-organization,
`00000000-0000-0000-0000-000000000001`, minted by this change's
migration), which keeps every existing "a `UserAccount` is scoped to
exactly one organization" invariant (`user-credentials` spec) true
without modification.

**Cross-org write** (bootstrap an organization) keeps
`TryCreateBootstrapAsync`'s existing shape unchanged: `set_config`
targets exactly the organization id supplied in the request, so every
existing RLS policy still applies unmodified. Only the gate in front of
it changes, from the `PlatformAdmin` cookie policy to an
`IsSystemAdmin`-flag check on the caller loaded from the unified cookie.

**Cross-org read** (`GET /account/organizations`, formerly
`GET /platform/organizations`) keeps the existing `platform_readonly`
least-privilege Postgres login and column-scoped grant (`0007`) exactly
as shipped — that privilege boundary lives in Postgres, not in C#
discipline, and nothing about merging the identity model changes it. Only
the application-level gate changes, from the `PlatformAdmin` scheme to
the same `IsSystemAdmin` check. `CanListOrganizations`/503-when-absent
behavior is unchanged, preserving the fail-closed guarantee the spec
requires.

**Route surface**: `/platform/*` is folded into `/account/*`. There is no
remaining reason for a separate route prefix once there is no separate
scheme — a caller distinguishes nothing about the request shape based on
prefix, only the authenticated identity's `IsSystemAdmin` flag matters.
The `PlatformAdminCookie` scheme, its `"PlatformAdmin"` authorization
policy, `PasswordHasher<PlatformAdmin>`, `Commerce.Domain.Identity.PlatformAdmin`,
`Commerce.Cloud.Api.Persistence.PostgresPlatformAdminStore`, and
`Commerce.Cloud.Api.Endpoints.PlatformAdminEndpoints` are all removed —
not deprecated in place — because a second identity plane is exactly what
this change undoes.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Sysadmin capability storage** | `IsSystemAdmin bool` column on `UserAccount`/`user_directory`, defaulting `false`. Read into a new `EffectiveIsSystemAdmin` accessor alongside `EffectivePermissions` in `LoadActorAsync`. Orthogonal to `Permission` and to `RoleCatalog` — a sysadmin's org-scoped `Permission` bitmask can independently be `None`, since none of today's org-scoped endpoints need it. | **A cross-org `Permission` bit** (proposal option (a)) — `Permission` is a `[Flags]` enum whose values are read from `RoleCatalog` and persisted inside the `roles` jsonb array, i.e. it is inherently *role-shaped and org-scoped by convention* even though the CLR type itself has no organization dimension; overloading it to also mean "ignore your own organization" reads as a correctness landmine at every future `HasFlag` call site, and grep shows zero existing call sites that expect a flag to mean anything cross-org. **A dedicated `system_admins` table** (proposal option (c)) — re-introduces a second table to keep in sync with `UserAccount` for the exact identity-fragmentation reason this change exists to remove; the migration and rollback story is strictly worse than one column. |
| **Sysadmin's owning organization** | A single reserved pseudo-organization row, id `00000000-0000-0000-0000-000000000001`, name `"Incoders Platform"`, created by this change's migration if absent. Every sysadmin `UserAccount` is scoped to it. This keeps `organization_id NOT NULL` on `users` completely unmodified — the column's meaning ("the org this row's RLS policies key on") is unchanged; it does not mean "the org this user is limited to" for a sysadmin, because `IsSystemAdmin` overrides that at the authorization layer, not the storage layer. | **Nullable `organization_id` for sysadmins** — breaks the RLS policies' `organization_id = current_setting('app.current_org_id')` shape for every table keyed that way, and reopens exactly the "two identity shapes" problem this change removes. |
| **`/platform/*` route fate** | Folded into `/account/*`: `GET /account/organizations` (was `GET /platform/organizations`), `POST /account/organizations` (was `POST /platform/organizations`). The genesis pair (`/platform/bootstrap*`) is replaced by the migration itself — see below. | **Keep `/platform/*` behind the unified cookie** — a route prefix implying a second surface, with nothing left to justify it once there is one scheme, is exactly the "smoothed dual-session UX" (option (b)) the user explicitly rejected. |
| **`platform_admins` migration** | One migration copies each `platform_admins` row into `users`/`user_directory` under the reserved pseudo-organization, `IsSystemAdmin = true`, empty `roles`/`branch_scope`, reusing `password_hash` byte-for-byte. This is safe because ASP.NET Core's `PasswordHasher<TUser>` format is **not parameterized by `TUser` in the hash bytes** — `TUser` only selects which DI-registered hasher instance verifies it, and the wire format (marker byte + iteration count + salt + subkey) is identical regardless of the closed generic type. `PasswordHasher<UserAccount>` therefore verifies a hash created by `PasswordHasher<PlatformAdmin>` without rehashing. After copying, the migration drops `platform_admins`. | **Force a password reset for every migrated sysadmin** — unnecessary; the hash format compatibility above is a verified platform guarantee, not an assumption, and a forced reset would be a gratuitous availability hit with no security benefit. |
| **`platform_readonly` Postgres role and `organizations_platform_read` policy** | **Unchanged, kept as-is.** The privilege boundary (`GRANT SELECT (id, name, created_at) ON organizations TO platform_readonly`) already lives at the correct layer and has nothing to do with which HTTP scheme gated the caller. Only `PostgresPlatformAdminStore.ListOrganizationsAsync`'s call site moves (into `PostgresOrganizationStore` or a renamed store), and only its application-level gate changes. | **Migrate cross-org read to a claim-scoped RLS policy on `app_runtime`** — reopens the exact "cheaper but reachable from the same pool/role that serves every tenant" hazard `commerce-role-taxonomy`'s design already rejected for the identical reason; nothing about this change's identity merge weakens that argument. |
| **`RoleCatalog`/`RoleGrantPolicy` treatment** | **Untouched.** `platform-admin` remains a catalog entry (`Permission.None`), remains excluded from `OrgAssignable`, and `RoleGrantPolicy.TryAuthorize`'s `ReservedRole` denial keeps firing exactly as before. `IsSystemAdmin` is authorized by a direct boolean check at the endpoint, the same shape as `Permission.ManageUsers`/`ManageBranchSettings` checks, never through `RoleGrantPolicy`. | **Fold `IsSystemAdmin` into `RoleCatalog` as a fifth "role"** — `RoleCatalog` entries are *grantable by another org-scoped admin via the roles endpoint* by construction (that is the entire reason `OrgAssignable` exists); a sysadmin flag must never be settable through that path, so it must not be a `RoleCatalog` entry at all. |
| **Branch-creation endpoint shape** | `POST /account/branches` and `GET /account/branches`, joining a new `RequireAuthorization()` + `TenantScopeEndpointFilter` group (the `adminGroup` pattern from `Account.cs`), gated by `Permission.ManageBranchSettings`. Both endpoints act only on the caller's own organization — `TenantScopeEndpointFilter` supplies the scope, so there is no request-body organization id to validate against a mismatch. | **A cross-org branch-creation path for sysadmins** — not requested (the proposal scopes this to "business-admin, own org" as the resolution-independent default) and not needed: a sysadmin onboarding a new organization already gets its first branch via `POST /account/organizations`, which reuses `TryCreateBootstrapAsync` unmodified. |
| **`GET /account/organizations/{id}`** | **Not built.** The onboarding screen only needs list + create for this phase; the proposal names this endpoint as deferred-and-optional, and nothing in scope reads a single organization's detail. | Building it speculatively — no consumer exists yet; add it when an organization-detail/edit screen is actually scoped. |
| **Web nav placement** | Two new `AppLayout` nav entries, "Users" and "Branches", visible whenever `RequireAdmin`'s existing `ManageUsers` check would pass (both screens sit at `/app/users` and `/app/branches`, nested exactly like `/app/customers` under the existing `<Route element={<RequireAdmin />}>` block). A third entry, "Organizations", renders only when `user.isSystemAdmin` (new field on `SignedInResponse`) is true, and its route uses a new `RequireSystemAdmin` guard mirroring `RequireAdmin`'s shape but checking `isSystemAdmin` instead of a permission bit. | **One `RequireAdmin` guard for everything including Organizations** — `ManageUsers` and cross-org sysadmin capability are deliberately different axes (a business-admin holding every org permission must still be denied Organizations); collapsing them into one guard would be the exact default-visibility bug the proposal's risk table names. |
| **POS staff/role window entry point** | New `ManageStaffButton` on `MainWindow`, visibility gated by `Permission.ManageUsers` exactly like `ManageCustomersButton` (same `RefreshIdentityText` block), opening a new `UsersWindow` via `ShowDialog()` with a fresh `Func<UserAdminClient>` factory-created client, mirroring `ManageCustomersButton_Click`/`CustomerAdminClient` line for line. | **Reuse `CustomersWindow` with a mode flag** — the two screens manage structurally different entities (users+roles vs. customers) with different endpoints and DTOs; a mode flag would make one window do two unrelated jobs, the opposite of the precedent this change is told to follow "directly." |
| **`GET /account/users` shape** | Flat list, no pagination — matches `CustomersScreen`'s existing list endpoint at current scale, and neither screen has a pagination precedent to extend. | Building pagination now — no current org has staff counts anywhere near needing it; add later against real scale data. |
| **Role-reassignment UI** | Reuses the existing full-replace `PUT /account/users/{id}/roles` request shape as-is: a simple multi-select checkbox list sourced from `RoleCatalog.OrgAssignable` (already excludes `platform-admin`), submitting the complete resulting name list. | **A dedicated add/remove-role endpoint** — not requested, and the server already only accepts full-replace; a delta-based UI would have to reconstruct the full set client-side anyway before submitting, adding a translation layer for no behavioral gain. |

## Data Flow

```text
Sign in (every identity, including sysadmin)
  POST /account/sign-in {email, password}
    -> store.FindDirectoryEntryAsync(email)              [unscoped lookup]
    -> store.FindByEmailAsync(scope, email)               [scoped to that entry's org]
    -> PasswordHasher<UserAccount>.VerifyHashedPassword    [SAME hasher as every user,
                                                             including a migrated sysadmin row]
    -> actor = LoadActorAsync(scope, credential.Id)
    -> claims = {org_id, NameIdentifier, Name, session_ver}   <- UNCHANGED shape
    -> SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, claims)
    -> 200 {organizationId, userId, displayName,
            permissions: (int)actor.EffectivePermissions,
            isSystemAdmin: actor.IsSystemAdmin}            <- ONLY new field

List staff (org-scoped, new)
  GET /account/users
    -> RequireAuthorization + TenantScopeEndpointFilter -> scope
    -> caller = LoadActorAsync(scope, NameIdentifier) -> !ManageUsers -> 403
    -> userStore.ListStaffAsync(scope)                     [excludes CustomerId-linked rows]
    -> 200 [UserSummaryDto...]

Create a branch (org-scoped, new)
  POST /account/branches {branchName}
    -> RequireAuthorization + TenantScopeEndpointFilter -> scope
    -> caller = LoadActorAsync(scope, NameIdentifier) -> !ManageBranchSettings -> 403
    -> branchStore.CreateAsync(scope, branchName)
    -> 201 {branchId}

List organizations (cross-org read, moved from /platform)
  GET /account/organizations
    -> RequireAuthorization()                              <- unified cookie, default scheme
    -> caller = LoadActorAsync(scope, NameIdentifier) -> !IsSystemAdmin -> 403
       (fails CLOSED: never silently narrows to caller's own organization)
    -> organizationStore.CanListOrganizations
       -> false -> 503                                      <- UNCHANGED fail-closed behavior
    -> organizationStore.ListOrganizationsAsync()            [platform_readonly datasource,
                                                               UNCHANGED privilege boundary]
    -> 200 [{id, name, createdAt}...]

Bootstrap an organization (cross-org write, explicit target org, moved from /platform)
  POST /account/organizations {organizationName, branchName?, adminEmail, adminPassword}
    -> RequireAuthorization()
    -> caller = LoadActorAsync(scope, NameIdentifier) -> !IsSystemAdmin -> 403
    -> organizationId = Guid.NewGuid()                       <- server-minted, unchanged
    -> TryCreateBootstrapAsync(new CloudTenantScope(organizationId), ...)
       set_config('app.current_org_id', organizationId)      <- UNCHANGED, every policy applies
    -> audit row (actor_kind='org-user', organization_id=organizationId)
    -> 201 {organizationId, branchId, userId}
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `deploy/db/migrations/0008_admin_console.sql` | Create | `users.is_system_admin boolean NOT NULL DEFAULT false`; `branches` table already exists (no schema change there beyond an endpoint); reserved pseudo-organization insert (idempotent, `ON CONFLICT DO NOTHING`); copy each `platform_admins` row into `users`/`user_directory` under the pseudo-org with `is_system_admin = true`; `DROP TABLE platform_admins`. |
| `deploy/dev/db/init-rls.sql` | Modify | Same DDL appended verbatim, minus the drop (dev init has no pre-existing `platform_admins` rows to migrate — it creates the final shape directly). |
| `deploy/README.md` | Modify | `0008` apply section; removes the platform-genesis runbook step (superseded by the migration's pseudo-org sysadmin row plus an explicit post-migration password reset for that row, documented as the new one-time operator step). |
| `src/Commerce.Domain/Identity/UserAccount.cs` | Modify | Add `IsSystemAdmin` property. |
| `src/Commerce.Domain/Identity/PlatformAdmin.cs` | Delete | Superseded by `IsSystemAdmin` on `UserAccount`. |
| `src/Commerce.Cloud.Api/Persistence/PostgresUserAccountStore.cs` | Modify | `LoadActorAsync` reads `is_system_admin`; new `ListStaffAsync(scope)` (excludes `CustomerId`-linked rows); `CreateStaffUserAsync`/row-mapping read/write the new column. |
| `src/Commerce.Cloud.Api/Persistence/PostgresPlatformAdminStore.cs` | Delete | Superseded — `ListOrganizationsAsync`/`CanListOrganizations` move onto `PostgresOrganizationStore`. |
| `src/Commerce.Cloud.Api/Persistence/PostgresOrganizationStore.cs` | Modify | Gains `ListOrganizationsAsync` (via `platform_readonly` datasource, unchanged SQL/grant) and `CanListOrganizations`; gains `CreateBranchAsync`/`ListBranchesAsync` for the caller's own organization. |
| `src/Commerce.Cloud.Api/Endpoints/PlatformAdmin.cs` | Delete | Its two surviving endpoints (list/create organizations) move into `Account.cs`; the genesis pair is superseded by the migration. |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modify | Add `GET /account/users`, `POST /account/branches`, `GET /account/branches`, `GET /account/organizations`, `POST /account/organizations` (moved), `IsSystemAdmin` gate helper alongside the existing `ManageUsers` checks. `SignedInResponse` gains `IsSystemAdmin`. |
| `src/Commerce.Cloud.Api/Program.cs` | Modify | Remove the `PlatformAdminCookie` scheme, `PasswordHasher<PlatformAdmin>` registration, `"PlatformAdmin"` policy, `PostgresPlatformAdminStore` DI registration, `MapPlatformAdminEndpoints` call. The `platform_readonly`-keyed `NpgsqlDataSource` registration stays, now consumed by `PostgresOrganizationStore`. |
| `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` | Modify | Remove the `platform_admins` table check; the readiness query and message no longer reference it. |
| `src/Commerce.Web/src/api/types.ts` | Modify | `SignedInResponse` gains `isSystemAdmin: boolean`. |
| `src/Commerce.Web/src/api/account.ts` (or equivalent) | Modify | New calls: `listUsers`, `createBranch`, `listBranches`, `listOrganizations`, `createOrganization`. |
| `src/Commerce.Web/src/routes/RequireSystemAdmin.tsx` | Create | `RequireAdmin`'s exact shape, checking `user.isSystemAdmin` instead of a `Permission` bit. |
| `src/Commerce.Web/src/screens/UsersScreen.tsx` | Create | `CustomersScreen` precedent: list, create, role-reassign (multi-select over `RoleCatalog.OrgAssignable`'s wire equivalent), reset-password. |
| `src/Commerce.Web/src/screens/BranchesScreen.tsx` | Create | List + create branch, own organization only. |
| `src/Commerce.Web/src/screens/OrganizationsScreen.tsx` | Create | Sysadmin-only: list + onboard a new organization. |
| `src/Commerce.Web/src/routes/AppLayout.tsx` | Modify | Nav entries for Users, Branches (under `RequireAdmin`), Organizations (under `RequireSystemAdmin`). |
| `src/Commerce.Web/src/App.tsx` | Modify | New nested routes: `users`, `branches` under the existing `RequireAdmin` block; `organizations` under a new `RequireSystemAdmin` block. |
| `src/Commerce.Pos.Windows/UserAdminClient.cs` | Create | `CustomerAdminClient`'s exact shape against `/account/users`/`/account/users/{id}/roles`/`/account/users/{id}/reset-password`. |
| `src/Commerce.Pos.Windows/UsersWindow.xaml` + `.xaml.cs` | Create | `CustomersWindow`'s exact shape: list, create, role-reassign, reset-password. |
| `src/Commerce.Pos.Windows/MainWindow.xaml` + `.xaml.cs` | Modify | New `ManageStaffButton`, gated identically to `ManageCustomersButton`; new `Func<UserAdminClient>` DI factory; `ManageStaffButton_Click` mirroring `ManageCustomersButton_Click`. |
| `src/Commerce.Pos.Windows/PosHostBuilder.cs` | Modify | Register `Func<UserAdminClient>` factory, same registration shape as the existing `Func<CustomerAdminClient>`. |
| `tests/Commerce.Cloud/UserAccountTests.cs` (or equivalent) | Modify | `IsSystemAdmin` round-trips through `LoadActorAsync`. |
| `tests/Commerce.Integration/AdminConsoleTests.cs` | Create | `GET /account/users` (org isolation, excludes customer-linked), branch creation/listing (own-org only), `GET /account/organizations` (sysadmin-only, fail-closed, 503-without-datasource), `POST /account/organizations` (sysadmin-only). |
| `tests/Commerce.Integration/MigrationRlsTests.cs` | Modify | `0008` applies cleanly; a seeded `platform_admins` row migrates to a `users` row with `is_system_admin = true` under the pseudo-org, its original password hash still verifies, and `platform_admins` no longer exists afterward. |

## Interfaces / Contracts

```sql
-- 0008_admin_console.sql (forward-only: migrate platform_admins into users, then drop it)
ALTER TABLE users ADD COLUMN IF NOT EXISTS is_system_admin boolean NOT NULL DEFAULT false;

INSERT INTO organizations (id, name)
VALUES ('00000000-0000-0000-0000-000000000001', 'Incoders Platform')
ON CONFLICT (id) DO NOTHING;

-- Owner-run rewrite needs the same NO FORCE / FORCE toggle 0006 established,
-- since `users` carries FORCE ROW LEVEL SECURITY.
BEGIN;
ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
INSERT INTO users (id, organization_id, email, password_hash, roles, branch_scope, is_system_admin)
SELECT id, '00000000-0000-0000-0000-000000000001', email, password_hash, '[]'::jsonb, '[]'::jsonb, true
FROM platform_admins;
-- user_directory mirrors the users insert (email -> organization_id lookup table).
INSERT INTO user_directory (email, organization_id)
SELECT email, '00000000-0000-0000-0000-000000000001' FROM platform_admins
ON CONFLICT (email) DO NOTHING;
ALTER TABLE users FORCE ROW LEVEL SECURITY;
COMMIT;

DROP TABLE IF EXISTS platform_admins;
-- Rollback: recreate platform_admins (0007's DDL), copy is_system_admin=true
-- rows back out by email, delete them from users/user_directory, drop the
-- column. Documented as a comment block in this file; not expected to be
-- exercised since 0007's platform_admins carried at most dev-bootstrap rows.
```

```csharp
// Commerce.Domain/Identity/UserAccount.cs (added member)
public bool IsSystemAdmin { get; }

// Commerce.Cloud.Api/Endpoints/Account.cs (added/changed records)
public sealed record SignedInResponse(
    Guid OrganizationId, Guid UserId, string DisplayName, int Permissions, bool IsSystemAdmin);

public sealed record UserSummaryDto(
    Guid UserId, string Email, IReadOnlyList<string> RoleNames, bool IsRevoked);

public sealed record CreateBranchRequest(string BranchName);
public sealed record CreateBranchResponse(Guid BranchId);
public sealed record BranchSummaryDto(Guid BranchId, string BranchName);

// Moved from PlatformAdmin.cs, unchanged shape:
public sealed record OrganizationSummary(Guid Id, string Name, DateTimeOffset CreatedAt);
public sealed record CreateOrganizationRequest(
    string OrganizationName, string? BranchName, string AdminEmail, string AdminPassword);
public sealed record CreateOrganizationResponse(Guid OrganizationId, Guid BranchId, Guid UserId);
```

```typescript
// Commerce.Web/src/api/types.ts (added field)
interface SignedInResponse {
  organizationId: string
  userId: string
  displayName: string
  permissions: number
  isSystemAdmin: boolean   // NEW
}
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | `UserAccount.IsSystemAdmin` round-trips unchanged through construction/mapping | xUnit |
| Integration | `GET /account/users`: returns only the caller's org's staff, excludes a `CustomerId`-linked row, `ManageUsers`-less caller -> 403 | `WebApplicationFactory` + live Postgres |
| Integration | `POST /account/branches` / `GET /account/branches`: creates and lists only within the caller's own organization; `ManageBranchSettings`-less caller -> 403 | same, two seeded orgs |
| Integration | `GET /account/organizations`: a caller without `IsSystemAdmin` -> 403 and the response is not silently narrowed to their own org; a sysadmin sees both seeded orgs; datasource absent -> 503 | same |
| Integration | `POST /account/organizations`: sysadmin creates org+branch+business-admin who can then sign in; non-sysadmin -> 403 | same |
| Integration | Sign-in: a migrated sysadmin row (seeded with a `PasswordHasher<PlatformAdmin>`-produced hash) signs in successfully through `PasswordHasher<UserAccount>` verification, proving hash-format compatibility across the `TUser` type parameter | same |
| Integration (migration) | `0008` migrates a seeded `platform_admins` row into `users` under the pseudo-org with `is_system_admin = true`, the row's original password still verifies, and `platform_admins` no longer exists after the migration runs | `MigrationRlsTests` |
| Integration (RLS) | `platform_readonly`'s existing privilege boundary is unaffected by this change — still only `SELECT (id, name, created_at)` on `organizations`, nothing else | same, unchanged from `commerce-role-taxonomy`'s suite, re-run as a regression check |

## Threat Matrix

| Native row | Applicability |
|---|---|
| Documentation-like paths | N/A — no file-classification boundary |
| Git repository selection | N/A — no product code runs Git |
| Commit state | N/A — no commit automation added |
| Push state | N/A — `railway.json` untouched |
| PR commands | N/A — no PR automation added |
| Routing | **Applicable** — new org-scoped routes (`/account/users`, `/account/branches`) reuse the existing `RequireAuthorization` + `TenantScopeEndpointFilter` + store-loaded-actor + permission-flag shape; moved cross-org routes (`/account/organizations`) drop the separate cookie scheme in favor of a direct `IsSystemAdmin` check on the same actor load, fail-closed (403, never a silent org-scoped fallback) exactly like every other permission check in this file. RED tests: non-sysadmin -> 403 on both organizations routes, cross-org branch/staff isolation, customer-linked exclusion from the staff list. |
| Process integration | **Applicable** — the `platform_readonly` outbound database identity is unchanged (same role, same column-level grant, same policy); only its Program.cs registration's consuming store class changes name. RED tests: the existing RLS privilege-error assertions, re-run unchanged. |
| Shell / subprocess | N/A — none introduced. |

## Migration / Rollout

Forward-only. Per environment, before deploying the new image: apply
`0008_admin_console.sql` via `psql` against the direct (non-pooled)
connection. This migrates any existing `platform_admins` row(s) — in
every deployed environment today, that is at most the dev-bootstrap
genesis admin — into `users` under the reserved pseudo-organization with
`is_system_admin = true`, preserving the original password hash, then
drops `platform_admins`. No `__*_PASSWORD__` placeholder is introduced by
this migration; `platform_readonly`'s existing credential is untouched.

Immediately after the migration, the operator should force a password
reset for the migrated sysadmin row via the existing admin-forced-reset
endpoint (now itself gated the normal `ManageUsers` way, since the
migrated row is an ordinary `UserAccount`) — a purely operational
hygiene step, not a security requirement created by this change (the
hash remains valid and unchanged).

No impact on any org-scoped session: existing org cookies are untouched
(no claim change to the default scheme). Any previously issued
`PlatformAdminCookie` session stops authenticating the moment the scheme
is removed from `Program.cs` — this is intended, not a regression, since
that scheme no longer exists to validate against.

Rollback: revert the commit, then apply `0008`'s documented inverse
(recreate `platform_admins`, copy `is_system_admin = true` rows back out
by email, delete them from `users`/`user_directory`, drop the column).

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | `0008` migration + dev init sync + `UserAccount.IsSystemAdmin` + `PostgresUserAccountStore` read/write + migration/RLS tests | ~220 | Migration applies cleanly, migrated row signs in, `is_system_admin` round-trips | Revert; run the inverse migration |
| 2 | `GET /account/users` + `ListStaffAsync` + integration tests | ~180 | Org isolation, customer-link exclusion, `ManageUsers` gate | Revert; endpoint disappears |
| 3 | `POST/GET /account/branches` + `PostgresOrganizationStore` branch methods + integration tests | ~220 | Own-org-only creation/listing, `ManageBranchSettings` gate | Revert; endpoints disappear |
| 4 | `GET/POST /account/organizations` (moved) + delete `PlatformAdmin.cs`/`PostgresPlatformAdminStore.cs`/`PlatformAdmin.cs` (domain) + `Program.cs` scheme/policy removal + `SignedInResponse.IsSystemAdmin` + health check update + integration tests | ~320 | Sysadmin-only gate, fail-closed 503 preserved, old `/platform/*` routes gone, existing org sign-in unaffected | Revert; the platform scheme/endpoints return |
| 5 | Web: `RequireSystemAdmin` + `UsersScreen` + `BranchesScreen` + `OrganizationsScreen` + `AppLayout`/`App.tsx` routing + api client calls + tests | ~450 | Each screen renders/guards correctly per permission/flag; existing screens unaffected | Revert; routes/screens disappear |
| 6 | POS: `UserAdminClient` + `UsersWindow` + `MainWindow` entry point + `PosHostBuilder` registration + tests | ~280 | Button visibility, window lifecycle matches `CustomersWindow`, server-side gate re-checked | Revert; window/entry disappear |

Decision needed before apply: Yes
Chained PRs recommended: Yes
400-line budget risk: High

**Why chained**: ~1,670 authored lines across six genuinely independent,
dependency-ordered slices — over four times the single-PR budget the
current session preflight selected. Units 1–4 land the server-side
identity merge and new endpoints (each buildable and testable in
isolation, ordered so the migration and its data-safety net exist before
anything depends on `IsSystemAdmin`, and the old `/platform` surface is
removed only after its replacement is proven). Units 5–6 are the two
independent UI clients consuming that finished server surface — Web and
POS.Windows share no code and can review in either order once Unit 4
lands. **This exceeds the "Single PR" delivery strategy selected at this
session's preflight; the parent orchestrator must re-collect delivery
strategy (split into a chain, or record an explicit `size:exception`)
before `sdd-apply` runs — see Review Workload Guard.**

## Open Questions

- [ ] None blocking. One accepted assumption with a documented fallback:
      every deployed environment's `platform_admins` table today holds at
      most the dev-bootstrap genesis admin (no reported production
      sysadmin onboarding has happened yet) — if a real environment
      turns out to hold additional rows, `0008`'s migration still handles
      them correctly (it copies every row, not just one), so this is a
      scale assumption about *how many* rows migrate, not a correctness
      gap.
