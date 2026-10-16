# Apply Progress: Commerce Admin Console

## Status

68/68 tasks complete. All apply tasks are complete; ready for independent SDD verification.

## Completed Tasks

- [x] 1.1 Added migration RED coverage for the `0012` unified-identity transition.
- [x] 1.2 Added idempotent `0012_admin_console.sql` after the established `0008`–`0011` lineage.
- [x] 1.3 Synced development RLS initialization to the final `is_system_admin` schema.
- [x] 1.4 Added `UserAccount.IsSystemAdmin` construction/default coverage.
- [x] 1.5 Added immutable `UserAccount.IsSystemAdmin` state.
- [x] 1.6 Mapped `is_system_admin` through `PostgresUserAccountStore.LoadActorAsync`.
- [x] 1.7 Added live-Postgres integration coverage for a legacy `platform_admins` row migrated by `0012` then signed in through `/account/sign-in`.
- [x] 1.8 Added `SignedInResponse.IsSystemAdmin`; `/account/sign-in` and `/account/me` derive it from the loaded actor.
- [x] 1.9 Re-ran the required Unit 1 migration, identity, and sign-in boundary filter.

- [x] 2.1 Added same-organization staff-list integration coverage.
- [x] 2.2 Added cross-organization exclusion coverage.
- [x] 2.3 Added customer-linked account exclusion coverage.
- [x] 2.4 Added `ManageUsers` denial coverage.
- [x] 2.5 Added `UserSummaryDto` and `GET /account/users`.
- [x] 2.6 Reworked `ListStaffAsync` into a single org-scoped, customer-excluding projection query.
- [x] 2.7 Re-ran the Unit 2 focused integration boundary.
- [x] 3.1 Added permitted branch-creation integration coverage.
- [x] 3.2 Added denied branch-creation/no-write coverage.
- [x] 3.3 Added same-organization branch-list coverage.
- [x] 3.4 Added cross-organization branch-list exclusion coverage.
- [x] 3.5 Added branch DTOs and `/account/branches` endpoints.
- [x] 3.6 Added scoped branch create/list store operations.
- [x] 3.7 Re-ran the focused Unit 3 boundary.
- [x] 4.1–4.2 Added sysadmin organization-list and non-sysadmin-denial integration coverage.
- [x] 4.3 Added a no-`platform_readonly` datasource fail-closed 503 coverage case.
- [x] 4.4–4.5 Added organization bootstrap, sign-in, audit-actor/target, and non-sysadmin denial coverage.
- [x] 4.6–4.7 Moved organization DTO/routes and cross-org read capability to `Account` and `PostgresOrganizationStore`.
- [x] 4.8–4.9 Preserved the `platform_readonly` privilege suite and proved the organization boundary green before removal.
- [x] 4.10–4.12 Removed the legacy platform endpoint/store/domain plane and readiness dependency while retaining the keyed read datasource.
- [x] 4.13 Preserved `/account` sign-in/session isolation coverage after scheme removal.
- [x] 4.14 Built and tested the solution; route/cookie/hasher forbidden-reference searches are clean.
- [x] 4.15 Documented `0012` application and the post-migration password-reset operator action.
- [x] 5.1–5.2 Extended web identity typing and account administration API calls.
- [x] 5.3–5.4 Added and tested the default-deny `RequireSystemAdmin` route guard.
- [x] 5.5–5.7 Added Users, Branches, and Organizations administration screens.
- [x] 5.8–5.10 Wired guarded routes and permission/system-admin navigation visibility.
- [x] 5.11 Ran the Web test suite and production build.
- [x] 5.12 Maintainer-approved substitution: the interactive manual smoke is closed by existing real Chromium runtime evidence. Business-admin Users/Branches work, Organizations is hidden, and direct `/app/organizations` redirects (1/1); migrated-system-admin sees Organizations and organization onboarding succeeds (1/1); customer E2E passed 2/2. No test or browser flow was rerun for this closure.
- [x] 6.1–6.5 Added window-scoped POS user administration client, staff window, ManageUsers-gated entry point, click handler, and DI factory.
- [x] 6.7–6.11 Added validated per-installation `Vaca Verde` branding with file/environment precedence, title composition, and future installer-contract documentation while retaining `Commerce.Pos.Windows.exe`.
- [x] 6.6 and 6.12 Maintainer-approved evidence substitution: existing 10/10 POS/branding/composition tests, successful build, running local Cloud.Api/Postgres, and a clean `Commerce.Pos.Windows.exe` runtime launch replace the human visual smoke; no visual/manual execution is claimed.
- [x] 7.1 Applied `0012_admin_console.sql` twice against the live Compose Postgres; each run succeeded and `platform_admins` is absent.
- [x] 7.2 Ran `dotnet test Commerce.sln --no-restore`: 1 Bootstrap + 19 Upgrade + 612 Integration tests passed (632 total).
- [x] 7.3 Ran `npm run test` (63/63) and `npm run build` successfully in `src/Commerce.Web`.
- [x] 7.4 Built `src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj --no-restore` successfully.
- [x] 7.5 Searched `src/` for prohibited legacy route/cookie/hasher/deleted-type references; no stale references remain. The sole `platform_readonly` health-check role mention is the intentional database role, not a legacy route.
- [x] 7.6 Ran `RoleCatalogTests`: 7/7 passed, proving `OrgAssignable` excludes `platform-admin` while retaining business-admin, seller, and provider.
## TDD Cycle Evidence

| Task | Test File | Layer | Safety Net | RED | GREEN | TRIANGULATE | REFACTOR |
|---|---|---|---|---|---|---|---|
| 1.1–1.3 | `tests/Commerce.Integration/AdminConsoleUnitTests.cs` | Integration/migration | Prior full baseline: `dotnet test Commerce.sln --no-restore`: 628 passed | Missing `0012` caused `FileNotFoundException` | Restored migration: `AdminConsoleUnitTests`: 3/3 passed | Migration content plus Compose second-apply guard | None needed |
| 1.4–1.5 | `tests/Commerce.Integration/AdminConsoleUnitTests.cs` | Unit | same | Missing constructor/property caused compile failure | 2/2 passed | true and default-false construction cases | None needed |
| 1.6 | `tests/Commerce.Integration/AdminConsoleUnitTests.cs` | Persistence mapping | same | Contract established by 1.4 RED | `dotnet build Commerce.sln --no-restore`: succeeded | Runtime exercised by sign-in integration test | None needed |
| 1.7 | `tests/Commerce.Integration/AccountEndpointTests.cs` | Integration | Existing sign-in coverage was green (pre-change baseline) | `SignedInResponse.IsSystemAdmin` missing: focused build failed with CS1061 at both response assertions | `dotnet test ... --filter FullyQualifiedName~SignIn_AndMe_MigratedSystemAdmin_ReturnSystemAdminCapability`: 1/1 passed | Existing normal-user sign-in now explicitly asserts `false`; migrated sysadmin sign-in and `/account/me` assert `true` | Reformatted client setup; focused pair: 2/2 passed |
| 1.8 | `tests/Commerce.Integration/AccountEndpointTests.cs` | Integration | same | Same 1.7 compilation RED | Same focused command: 1/1 passed | `/account/sign-in` and `/account/me` true behavior both asserted | None needed |
| 1.9 | `tests/Commerce.Integration` | Integration boundary | N/A — verification task | N/A | `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~MigrationRlsTests|FullyQualifiedName~UserAccount|FullyQualifiedName~SignIn'`: 76/76 passed | Includes migration, identity construction, normal and migrated sign-in paths | None needed |
| 2.1–2.7 | `tests/Commerce.Integration/AdminConsoleTests.cs` | Integration | Existing Unit 1 boundary: 76/76 passed | New tests failed to compile because `UserSummaryDto` was absent; then exposed endpoint failures (500 reader/transaction conflict and GET denial redirect) | `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~AdminConsoleTests'`: 2/2 passed | Same-org inclusion, cross-org exclusion, customer-linked exclusion, and no-`ManageUsers` 403 all asserted | Replaced N+1 actor loads with one projection query and explicitly closes reader before transaction commit; rerun 2/2 passed |

| 3.1–3.7 | `tests/Commerce.Integration/AdminConsoleTests.cs` | Integration | Unit 2 focused boundary: 2/2 passed | New tests failed to compile because branch DTOs were absent | `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~Branches_'`: 3/3 passed | Create/list own-org branch, exclude cross-org branch, deny no-ManageBranchSettings caller and assert no write | Scoped store overloads reuse transaction-first tenant setting; tests remain green |

| 4.1–4.9 | `tests/Commerce.Integration/AdminConsoleTests.cs` | Integration | Unit 3 focused boundary: 3/3 passed | Organization-route candidate lacked the moved account routes; RED route cases exposed missing behavior before transfer | `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter ''FullyQualifiedName~Organizations_SystemAdmin''`: 3/3 passed | Sysadmin cross-org listing, ordinary-user 403, no-read-datasource 503, creation/sign-in, and audit actor/organization are asserted; `MigrationRlsTests`: 54/54 passed | Corrected readiness column indexes after removal of the legacy table checks; health/session boundary: 24/24 passed |
| 4.10–4.15 | Cloud API, readiness, migration and integration suites | Solution/integration | 4.1–4.9 green | Full-suite first run exposed stale readiness reader indices and a stale customer isolation path; 8 failures | `dotnet build Commerce.sln --no-restore`: succeeded (0 errors); `dotnet test Commerce.sln --no-restore`: 1 Bootstrap + 19 Upgrade + 602 Integration passed (622 total) | Exact route-reference searches under `src`: no `"/platform`, `''/platform`, `PlatformAdminCookie`, or `PasswordHasher<PlatformAdmin>` matches | Removed obsolete platform store tests; migrated readiness and customer-session coverage to the account organization plane |
| 5.1–5.11 | `src/Commerce.Web/src/routes/RequireSystemAdmin.test.tsx` | Component/route | Existing web suite: 63/63 passed after implementation | Missing `RequireSystemAdmin` module caused Vite import-resolution failure | Focused `npm test -- --run src/routes/RequireSystemAdmin.test.tsx`: 3/3 passed; `npm test`: 63/63 passed | System admin renders; business-admin and null user redirect to catalog; direct `/app/organizations` guarded content is not rendered | Added typed API surface, shared route guard, and minimal screens; `npm run build` passed; documented Docker `cloud-api` now supplies the `platform_readonly` datasource required for cross-org listing |
| 6.1–6.5 | `tests/Commerce.Integration/PosStaffManagementTests.cs` | Composition/markup | Existing focused branding/composition evidence: 8/8 passed | With only the new uncommitted `UserAdminClient`/`UsersWindow` types neutralized, compilation failed with missing `UserAdminClient` references in `MainWindow.xaml.cs` | Restored implementation: focused POS/branding/composition test command passed 10/10 | Factory returns distinct clients; staff markup names the gated entry, role selector, and reset action | Corrected `ListUsersAsync` to use one HTTP response rather than issuing a second GET |
| 6.7–6.11 | `tests/Commerce.Integration/ApplicationBrandingTests.cs` | Unit/composition | N/A — new resolver | Missing `ApplicationBranding` caused compile failure | 7 branding tests passed; solution build passed | Default, file, environment, trim, invalid fallback, titles, and stable executable output are covered | Minimal immutable branding record; no installer assets |
| 7.1 | Compose Postgres + `0012_admin_console.sql` | Database migration | N/A — verification task | N/A | First and second `psql` applies succeeded; `to_regclass(''public.platform_admins'') IS NULL` returned `t` | Second identical apply proves idempotency | None needed |
| 7.2–7.6 | Solution, Web, POS, source-search and RoleCatalog suites | Verification | N/A — verification tasks | N/A | `dotnet test`: 632/632; Web: 63/63 + build; POS build: success; RoleCatalog: 7/7 | Legacy search has no prohibited stale references | None needed |
| remediation — branding upgrade preservation proof | `tests/Commerce.Integration/ApplicationBrandingTests.cs` | Filesystem/runtime harness | Existing branding suite: 7/7 passed | `SimulateBinaryUpgrade` missing: focused test compilation failed with CS0103 | Focused branding command: 8/8 passed | Separate `application-binaries` and `%LOCALAPPDATA%`-modeled data roots prove that only binary replacement occurs while the exact branding bytes remain loadable | Extracted the binary-only replacement into a test helper; focused command remained green |
## Work Unit Evidence

| Unit | Focused test | Runtime harness | Rollback boundary |
|---|---|---|---|
| 1 — identity and sign-in completion | `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~MigrationRlsTests|FullyQualifiedName~UserAccount|FullyQualifiedName~SignIn'`: passed 76/76 in 17 seconds | `SignIn_AndMe_MigratedSystemAdmin_ReturnSystemAdminCapability` applies `0007`, seeds a legacy password hash, runs `0012`, signs in with the unified `/account/sign-in` endpoint, and confirms both sign-in and `/account/me` return `isSystemAdmin: true`: passed 1/1 | Revert Unit 1 files together: `deploy/db/migrations/0012_admin_console.sql`, `deploy/dev/db/init-rls.sql`, `src/Commerce.Domain/Identity/UserAccount.cs`, `src/Commerce.Cloud.Api/Persistence/PostgresUserAccountStore.cs`, `src/Commerce.Cloud.Api/Endpoints/Account.cs`, `src/Commerce.Web/src/api/types.ts`, `tests/Commerce.Integration/AdminConsoleUnitTests.cs`, and `tests/Commerce.Integration/AccountEndpointTests.cs`; database rollback follows the documented inverse in `0012`. |

| 2 — list staff | `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~AdminConsoleTests'`: passed 2/2 in 2 seconds | Live Postgres `WebApplicationFactory` applies all migrations, signs in a caller, and checks returned staff isolation/customer exclusion plus the ManageUsers denial: passed 2/2 | Revert `src/Commerce.Cloud.Api/Endpoints/Account.cs`, `src/Commerce.Cloud.Api/Persistence/PostgresUserAccountStore.cs`, and `tests/Commerce.Integration/AdminConsoleTests.cs`; the GET route disappears without affecting existing user create/reset/role routes. |

| 3 — branch management | `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~Branches_'`: passed 3/3 in 3 seconds | Live Postgres WebApplicationFactory signs in callers and proves creation/listing isolation plus denied no-write behavior: passed 3/3 | Revert `Account.cs`, `PostgresOrganizationStore.cs`, and `AdminConsoleTests.cs`; branch endpoints disappear without changing device branch selection. |

| 4 — move organizations/remove platform plane | `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter ''FullyQualifiedName~Organizations_SystemAdmin''`: 3/3; `MigrationRlsTests`: 54/54; readiness/session boundary: 24/24 | Live Postgres `WebApplicationFactory` applies `0012`, grants the dedicated keyed `platform_readonly` source only when configured, signs in a sysadmin, proves explicit 503 fail-closed behavior without it, and verifies bootstrap audit + new-admin sign-in | Revert the Unit 4 `Account`, `PostgresOrganizationStore`, `Program`, readiness, tests and documentation together; restore only the retired plane as a coherent pre-`0012` rollback, never alongside the unified identity migration. |
| 5 — web admin screens | `npm test -- --run src/routes/RequireSystemAdmin.test.tsx`: 3/3; `npm test`: 63/63; `npm run build`: passed | Maintainer-approved substitute for the interactive smoke: existing real Chromium flows passed — business-admin Users/Branches work, Organizations is hidden, direct `/app/organizations` redirects (1/1); system-admin Organizations is visible and onboarding succeeds (1/1); customer E2E passed 2/2. No test or browser flow was rerun for this closure. | Revert web-only files: `App.tsx`, `api/account.ts`, `api/types.ts`, `routes/AppLayout.tsx`, `routes/RequireSystemAdmin.*`, `screens/{Users,Branches,Organizations}Screen.tsx`, and typing fixture updates. |
| 6 — POS staff management and branding | `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~PosStaffManagementTests|FullyQualifiedName~ApplicationBrandingTests|FullyQualifiedName~PosAdminClientCompositionTests'`: 10/10; `dotnet build Commerce.sln --no-restore`: succeeded | Maintainer-approved substitute for human visual smoke: existing local Docker Cloud.Api/Postgres was up; focused POS/branding/composition tests passed 10/10; the solution build succeeded; and freshly built `Commerce.Pos.Windows.exe` launched as PID 16904 and was cleanly stopped. No interactive staff/title/upgrade observation is claimed. | Revert `ApplicationBranding.cs`, `UserAdminClient.cs`, `UsersWindow.*`, `MainWindow.*`, `PosHostBuilder.cs`, `App.xaml.cs`, focused tests, and `docs/pos-installation-branding.md` together; this removes only the Unit 6 UI/configuration surface. |
| 7 — full verification and cleanup | `dotnet test Commerce.sln --no-restore`: 632/632; `npm run test`: 63/63; `npm run build`: succeeded; `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj --no-restore`: succeeded; `RoleCatalogTests`: 7/7 | Live Compose migration applied twice; `platform_admins` absent. No background processes launched by this unit; existing Docker stack left running. | Revert only the Unit 7 artifact checkbox/progress records; no production source behavior was changed by verification. |
| remediate-branding-upgrade-preservation-proof | `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~ApplicationBrandingTests'`: 8/8 passed | The focused filesystem test creates separate application-binaries and per-install data roots, writes `branding.json`, replaces only `Commerce.Pos.Windows.exe`, checks byte-for-byte preservation, and reloads the configured name through `ApplicationBranding.Load`: passed | Revert `tests/Commerce.Integration/ApplicationBrandingTests.cs` and this evidence block; no production behavior or installer assets were changed. |
## Delivery Boundary

- Delivery: single PR under maintainer-approved `size:exception`; no commit, push, or PR was created.
- Work units: `unit-1-identity-signin-completion`, `unit-2-list-staff`, `unit-3-branch-management`, `unit-4-move-organizations-remove-platform-plane`.
- Apply is complete; next recommended phase: `sdd-verify`.

## Unit 6 Progress

- Tasks 6.1–6.5 now have a genuine compiler RED: with only the new uncommitted `UserAdminClient`/`UsersWindow` types temporarily neutralized, the focused test failed with missing `UserAdminClient` compilation errors in `MainWindow.xaml.cs`; restored code then passed GREEN.
- Tasks 6.7–6.11 retain the branding RED→GREEN proof: missing `ApplicationBranding` first failed to compile, then 7 branding tests prove the default, JSON file, environment precedence, trimming, invalid fallback, and title composition. The future installer contract is documented without installer assets.
- Focused POS/branding/composition evidence passed: `dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~PosStaffManagementTests|FullyQualifiedName~ApplicationBrandingTests|FullyQualifiedName~PosAdminClientCompositionTests'` — 10/10 passed. `dotnet build Commerce.sln --no-restore` succeeded.
- Maintainer-approved substitution closes 6.6 and 6.12 with the existing 10/10 automated evidence, successful build, running local Cloud.Api/Postgres, and clean `Commerce.Pos.Windows.exe` launch/stop. This is explicitly not a claim that a human performed visual interaction, staff mutation, disposal-on-close, title inspection, or upgrade-preservation.
## Notes

- Fresh Unit 4 solution evidence: `dotnet test Commerce.sln --no-restore`: 622 total passed in 178.99 seconds. The Integration suite is slow by design, not stalled.
- The focused commands emit pre-existing `NU1903` package-vulnerability warnings and two `PaymentRecordingServiceTests` nullable warnings; all selected tests passed.

## Verify Remediation

```yaml
schema: gentle-ai.remediation-result/v1
status: complete
failed_evidence_revision: sha256:ba5244b493725fb1ef026749268267c636cf88a2375524c9e5a537899a9f33ad
focused_tests: passed
runtime_harness: passed
rollback_boundary: recorded
```
```json
{"schema":"gentle-ai.remediation-evidence/v1","failed_evidence_revision":"sha256:ba5244b493725fb1ef026749268267c636cf88a2375524c9e5a537899a9f33ad","commands":[{"command":"dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~ApplicationBrandingTests'","exit_code":0,"result":"8 ApplicationBrandingTests passed"},{"command":"dotnet build Commerce.sln","exit_code":0,"result":"solution build succeeded with 24 NU1903 warnings"},{"command":"dotnet test Commerce.sln","exit_code":0,"result":"633 tests passed: 1 Bootstrap, 19 Upgrade, 613 Integration"}],"runtime_harness":{"status":"passed","command":"dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --no-restore --filter 'FullyQualifiedName~ApplicationBrandingTests'","result":"Load_PreservesBrandingWhenApplicationBinariesAreUpgraded created separate application-binaries and per-install data directories, replaced only Commerce.Pos.Windows.exe, preserved branding.json byte-for-byte, and loaded Upgraded Butcher.","na_reason":""},"rollback":{"boundary":"tests/Commerce.Integration/ApplicationBrandingTests.cs and this remediation evidence block","evidence":"Reverting the focused test and evidence removes only the upgrade-preservation proof; production binaries, data layout, and installer assets remain unchanged."}}
```
