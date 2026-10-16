# Tasks: Commerce Admin Console

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~1750 (design's own estimate: Unit 1 ~220, Unit 2 ~180, Unit 3 ~220, Unit 4 ~320, Unit 5 ~450, Unit 6 ~360) |
| Effective review budget (session default) | 400 changed lines |
| 400-line budget risk | High |
| Chained PRs recommended | Yes, by design — overridden by explicit user decision this session |
| Delivery strategy | single-pr |
| Chain strategy | size-exception |

Decision needed before apply: No — resolved this session via `AskUserQuestion`. User explicitly accepted `size:exception` for one PR covering all 6 work units, over the design's own chained-PR recommendation.

**Hard ordering preserved as task order, not PR order**: Phase 1 (identity-merge migration + `IsSystemAdmin`) MUST be green before Phase 4 (route removal/move) begins — the old `/platform/*` surface must not be deleted before its replacement exists and is proven. Phase 4 MUST be green before Phases 5–6 (Web/POS clients) begin, since both clients call the moved `/account/organizations` endpoints. Phases 2 and 3 (staff listing, branch endpoints) have no dependency on each other or on Phase 4 and may proceed in either order once Phase 1 is green. Phases 5 and 6 are independent of each other.

### Suggested Work Units (commit groups within the one PR)

| Unit | Goal | Depends on | Focused test command | Runtime harness | Rollback boundary |
|------|------|------------|----------------------|-----------------|-------------------|
| 1 | `0012` migration, `UserAccount.IsSystemAdmin`, store read/write | none | `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~MigrationRlsTests\|FullyQualifiedName~UserAccount` | Apply `0012` after the existing `0008_customer_registry.sql` through `0011_payments.sql` lineage against `deploy/dev/compose.yaml`; a seeded `platform_admins` row migrates and still signs in | Revert; run `0012`'s inverse |
| 2 | `GET /account/users` | Unit 1 | `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~AdminConsoleTests` | Two seeded orgs, one with a customer-linked account | Revert; endpoint disappears |
| 3 | `POST/GET /account/branches` | Unit 1 | same filter | Two seeded orgs | Revert; endpoints disappear |
| 4 | `GET/POST /account/organizations` (moved), delete platform-admin plane, `Program.cs` cleanup | Unit 1 | same filter, plus a full `dotnet test Commerce.sln` | Sysadmin sign-in, list two orgs, bootstrap a third; confirm `/platform/*` no longer resolves | Revert; platform scheme/endpoints return |
| 5 | Web: `RequireSystemAdmin`, `UsersScreen`, `BranchesScreen`, `OrganizationsScreen`, routing | Unit 4 | `npm run test` in `src/Commerce.Web` | Manual smoke: sign in as sysadmin and as business-admin, confirm nav/route gating | Revert; screens/routes disappear |
| 6 | POS: `UserAdminClient`, `UsersWindow`, `MainWindow` entry point, configurable installation branding | Unit 4 | automated xUnit coverage for branding resolution/title composition plus the existing manual POS UI boundary | Launch POS, confirm button visibility/window lifecycle and `Vaca Verde` or configured titles while the process remains `Commerce.Pos.Windows.exe` | Revert; window/entry and branding override disappear |

## Phase 1: Identity Merge — Migration and `IsSystemAdmin` (Unit 1)

- [x] 1.1 RED: `MigrationRlsTests` — `0012` adds `users.is_system_admin boolean NOT NULL DEFAULT false`; a seeded `platform_admins` row (from `0007`'s shape) migrates into `users`/`user_directory` under the reserved pseudo-organization (`00000000-0000-0000-0000-000000000001`) with `is_system_admin = true`; the row's original `PasswordHasher<PlatformAdmin>`-produced password hash still verifies via `PasswordHasher<UserAccount>`; `platform_admins` no longer exists after the migration runs; a second run of `0012` is a no-op.
- [x] 1.2 GREEN: create `deploy/db/migrations/0012_admin_console.sql` after the existing `0008_customer_registry.sql` through `0011_payments.sql` lineage (add column, insert pseudo-organization idempotently, `NO FORCE`/`FORCE` toggle around the copy per `0006`'s established convention, `DROP TABLE platform_admins`, documented inverse as a comment block).
- [x] 1.3 GREEN: append the equivalent final-shape DDL to `deploy/dev/db/init-rls.sql` (dev init creates `is_system_admin` directly; no `platform_admins` table or migration step needed there).
- [x] 1.4 RED: unit test — `UserAccount.IsSystemAdmin` round-trips through construction unchanged.
- [x] 1.5 GREEN: add `IsSystemAdmin` property to `src/Commerce.Domain/Identity/UserAccount.cs`.
- [x] 1.6 GREEN: extend `PostgresUserAccountStore` row mapping (`LoadActorAsync`, `CreateStaffUserAsync`, any other row-to-`UserAccount` mapping) to read/write `is_system_admin`.
- [x] 1.7 RED: integration test — a migrated sysadmin row signs in successfully through `/account/sign-in` and `SignedInResponse.isSystemAdmin` is `true`.
- [x] 1.8 GREEN: `SignedInResponse` gains `IsSystemAdmin`; `/account/sign-in` and `/account/me` populate it from `actor.IsSystemAdmin`.
- [x] 1.9 Confirm 1.1/1.4/1.7 green; run `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~MigrationRlsTests|FullyQualifiedName~UserAccount|FullyQualifiedName~SignIn` before starting Phase 2.

## Phase 2: List Staff (Unit 2 — depends on Phase 1)

- [x] 2.1 RED: `AdminConsoleTests GET /account/users` — returns all staff in the caller's own organization.
- [x] 2.2 RED: extend — a user belonging to another organization is never returned — spec: "Listing does not leak another organization's staff".
- [x] 2.3 RED: extend — a `CustomerId`-linked account is excluded from the response — spec: "Listing excludes customer-linked accounts".
- [x] 2.4 RED: extend — a caller lacking `ManageUsers` is rejected — spec: "Caller without ManageUsers is denied".
- [x] 2.5 GREEN: add `UserSummaryDto` and `GET /account/users` to `Account.cs`'s `adminGroup`.
- [x] 2.6 GREEN: add `PostgresUserAccountStore.ListStaffAsync(scope)` (org-scoped query, excludes non-null `CustomerId`).
- [x] 2.7 Confirm 2.1–2.4 green.

## Phase 3: Branch Management (Unit 3 — depends on Phase 1, independent of Phase 2)

- [x] 3.1 RED: `AdminConsoleTests POST /account/branches` — a caller holding `ManageBranchSettings` creates a branch in their own organization — spec: "Business-admin creates a branch in their own organization".
- [x] 3.2 RED: extend — a caller lacking `ManageBranchSettings` is rejected and no branch is created — spec: "Caller without ManageBranchSettings is denied".
- [x] 3.3 RED: `GET /account/branches` — lists only the caller's own organization's branches — spec: "Business-admin lists their organization's branches".
- [x] 3.4 RED: extend — a branch belonging to another organization is never returned — spec: "Listing does not leak another organization's branches".
- [x] 3.5 GREEN: add `CreateBranchRequest`/`CreateBranchResponse`/`BranchSummaryDto` and `POST`/`GET /account/branches` to `Account.cs` (new `RequireAuthorization()` + `TenantScopeEndpointFilter` group, `ManageBranchSettings` gate).
- [x] 3.6 GREEN: add `PostgresOrganizationStore.CreateBranchAsync`/`ListBranchesAsync` (scoped by `TenantScopeEndpointFilter`'s scope, no cross-org path).
- [x] 3.7 Confirm 3.1–3.4 green.

## Phase 4: Cross-Org Routes Moved, Platform-Admin Plane Removed (Unit 4 — depends on Phase 1; MUST be green before Phase 5/6)

- [x] 4.1 RED: `AdminConsoleTests GET /account/organizations` — a sysadmin (`IsSystemAdmin = true`) lists both seeded organizations — spec: "Sysadmin lists organizations across tenants".
- [x] 4.2 RED: extend — a caller without `IsSystemAdmin` is rejected outright, not silently narrowed to their own organization's data — spec: "Caller without sysadmin capability is rejected, not narrowed".
- [x] 4.3 RED: extend — `CanListOrganizations` false (no `platform_readonly` datasource configured) returns 503, never a fallback — regression guard on the unchanged fail-closed behavior.
- [x] 4.4 RED: `POST /account/organizations` — a sysadmin bootstraps a new organization; the created admin can sign in; the audit row names the sysadmin actor and the new org's id — spec: "Sysadmin bootstraps a new organization" / "Sysadmin action targets an explicit organization id".
- [x] 4.5 RED: extend — a caller without `IsSystemAdmin` is rejected — spec: "Business-admin cannot reach a cross-org endpoint".
- [x] 4.6 GREEN: add `GET`/`POST /account/organizations` to `Account.cs` (moved from `PlatformAdmin.cs`, `IsSystemAdmin` check replacing the `PlatformAdmin` cookie policy).
- [x] 4.7 GREEN: move `ListOrganizationsAsync`/`CanListOrganizations` onto `PostgresOrganizationStore` (unchanged SQL/grant, unchanged `platform_readonly` datasource).
- [x] 4.8 RED: regression test — the pre-existing `platform_readonly` RLS privilege assertions from `commerce-role-taxonomy`'s suite still pass unchanged (still only `SELECT (id, name, created_at)` on `organizations`, nothing else).
- [x] 4.9 Confirm 4.1–4.5 and 4.8 green before deleting anything in 4.10–4.13.
- [x] 4.10 GREEN: delete `src/Commerce.Cloud.Api/Endpoints/PlatformAdmin.cs`, `src/Commerce.Cloud.Api/Persistence/PostgresPlatformAdminStore.cs`, `src/Commerce.Domain/Identity/PlatformAdmin.cs`.
- [x] 4.11 GREEN: remove from `Program.cs`: the `PlatformAdminCookie` scheme registration, the `"PlatformAdmin"` authorization policy, the `PasswordHasher<PlatformAdmin>` registration, the `PostgresPlatformAdminStore` DI registration, `MapPlatformAdminEndpoints`. Keep the `platform_readonly`-keyed `NpgsqlDataSource` registration (now consumed by `PostgresOrganizationStore`).
- [x] 4.12 GREEN: remove the `platform_admins` table check from `PostgresReadinessHealthCheck` (query and both messages).
- [x] 4.13 RED: regression test — an org-scoped sign-in and every existing `/account/*` flow are unaffected by the scheme removal (no claim shape change).
- [x] 4.14 Confirm Phase 4 fully green: `dotnet test Commerce.sln` before starting Phase 5/6. Confirm no remaining reference to `/platform` route paths, `PlatformAdminCookie`, or `PasswordHasher<PlatformAdmin>` anywhere under `src/`.
- [x] 4.15 Update `deploy/README.md` — `0012` apply section, removal of the platform-genesis runbook step, new post-migration password-reset operator note for the migrated sysadmin row.

## Phase 5: Web Admin Screens (Unit 5 — depends on Phase 4)

- [x] 5.1 GREEN: add `isSystemAdmin: boolean` to `SignedInResponse` in `src/Commerce.Web/src/api/types.ts`.
- [x] 5.2 GREEN: add API client calls (`listUsers`, `createBranch`, `listBranches`, `listOrganizations`, `createOrganization`) alongside the existing `account.ts` calls.
- [x] 5.3 RED (component test): `RequireSystemAdmin.test.tsx` — renders `<Outlet />` when `user.isSystemAdmin` is true; redirects when false or `user` is null — mirrors `RequireAdmin.test.tsx`'s exact cases.
- [x] 5.4 GREEN: create `src/Commerce.Web/src/routes/RequireSystemAdmin.tsx`.
- [x] 5.5 GREEN: create `src/Commerce.Web/src/screens/UsersScreen.tsx` (list via `GET /account/users`, create via `POST /account/users`, role-reassign via `PUT /account/users/{id}/roles` with a multi-select over the wire-equivalent of `RoleCatalog.OrgAssignable`, force-reset via `POST /account/users/{id}/reset-password`) — spec: "Web Staff/Role Management Screen", "Users Screen Cannot Assign Platform-Admin".
- [x] 5.6 GREEN: create `src/Commerce.Web/src/screens/BranchesScreen.tsx` (list + create) — spec: "Branch Management UI".
- [x] 5.7 GREEN: create `src/Commerce.Web/src/screens/OrganizationsScreen.tsx` (list + onboard) — spec: "Organization Onboarding UI".
- [x] 5.8 GREEN: wire `users` and `branches` routes into `App.tsx` under the existing `RequireAdmin` block; wire `organizations` under a new `RequireSystemAdmin` block.
- [x] 5.9 GREEN: add "Users", "Branches" nav entries to `AppLayout.tsx` (visible under the same condition as the existing "Customers" entry) and "Organizations" (visible only when `user.isSystemAdmin`).
- [x] 5.10 RED (route test): a business-admin without `isSystemAdmin` navigating directly to `/app/organizations` is redirected and the screen does not render — spec: "Business-admin is denied direct navigation to onboarding".
- [x] 5.11 Confirm 5.3/5.10 green; run `npm run test` in `src/Commerce.Web`.
- [x] 5.12 Manual smoke: `npm run dev` against a running Cloud.Api + Postgres; sign in as a business-admin (confirm Organizations link absent, Users/Branches screens work); sign in as the migrated sysadmin (confirm Organizations link present and functional).

## Phase 6: POS Staff/Role Window (Unit 6 — depends on Phase 4, independent of Phase 5)

- [x] 6.1 GREEN: create `src/Commerce.Pos.Windows/UserAdminClient.cs`, mirroring `CustomerAdminClient.cs`'s exact shape (window-scoped `HttpClient`/`CookieContainer`, `SignInAsync`, `IDisposable`) against `/account/users`, `/account/users/{id}/roles`, `/account/users/{id}/reset-password`.
- [x] 6.2 GREEN: create `src/Commerce.Pos.Windows/UsersWindow.xaml` + `.xaml.cs`, mirroring `CustomersWindow`'s shape (list, create, role-reassign, reset-password) — spec: "POS Staff/Role Management Window".
- [x] 6.3 GREEN: add `ManageStaffButton` to `MainWindow.xaml`; wire visibility in `RefreshIdentityText()` identically to `ManageCustomersButton` (`Permission.ManageUsers` check).
- [x] 6.4 GREEN: add `ManageStaffButton_Click` to `MainWindow.xaml.cs`, mirroring `ManageCustomersButton_Click` (fresh client from a `Func<UserAdminClient>` factory, `ShowDialog()`).
- [x] 6.5 GREEN: register the `Func<UserAdminClient>` factory in `PosHostBuilder.cs`, matching the existing `Func<CustomerAdminClient>` registration.
- [x] 6.6 Maintainer-approved evidence substitution: existing focused POS/branding/composition tests (10/10), successful solution build, running local Cloud.Api/Postgres, and a freshly built `Commerce.Pos.Windows.exe` launch/clean stop substitute for the human visual smoke; no human visual execution was claimed.
- [x] 6.7 RED: add `ApplicationBrandingTests` for `Commerce:ApplicationName`: default is `Vaca Verde`; optional `%LOCALAPPDATA%\Incoders\Commerce\branding.json` overrides the default; `Commerce__ApplicationName` overrides the file; values are trimmed; blank, control-character-containing, and over-80-character selected values fail safely to `Vaca Verde`.
- [x] 6.8 GREEN: implement the smallest POS branding resolver/configuration registration that makes 6.7 pass. Keep `branding.json` separate from `installation.json`, load it from the POS data directory, and do not rename `Commerce.Pos.Windows.exe` or alter its process identity.
- [x] 6.9 RED: add tests at the title-composition/application-branding boundary proving the validated application name is used by the main-window and dialog title values.
- [x] 6.10 GREEN: bind `MainWindow` and POS dialog titles, including `UsersWindow`, to the validated application name without changing executable or process identity.
- [x] 6.11 Document the future installer contract only: a future `COMMERCE_APPLICATION_NAME` property may persist the validated value to `branding.json` and use it for shortcut display text. Do not add installer or shortcut assets that do not exist.
- [x] 6.12 Maintainer-approved evidence substitution: branding tests cover default/file/environment/invalid fallback and title composition; successful build plus runtime launch confirms `Commerce.Pos.Windows.exe`; no human visual title or upgrade execution was claimed.

## Phase 7: Full Verification and Cleanup

- [x] 7.1 Apply `0012_admin_console.sql` twice against `deploy/dev/compose.yaml`, confirming idempotency (second run is a no-op, `platform_admins` already absent).
- [x] 7.2 Run `dotnet test Commerce.sln`, confirming all existing tests plus the new `MigrationRlsTests`/`AdminConsoleTests` cases pass with zero regressions.
- [x] 7.3 Run `npm run test` and `npm run build` in `src/Commerce.Web`.
- [x] 7.4 Confirm `Commerce.Pos.Windows` builds (`dotnet build`).
- [x] 7.5 Confirm no remaining reference anywhere in `src/` to `/platform` route paths, `PlatformAdminCookie`, `PasswordHasher<PlatformAdmin>`, or the deleted `PlatformAdmin`/`PostgresPlatformAdminStore` types.
- [x] 7.6 Confirm `RoleCatalog.OrgAssignable` still excludes `platform-admin` and is otherwise untouched by this change (regression guard — spec: "Users Screen Cannot Assign Platform-Admin" depends on this staying true).
