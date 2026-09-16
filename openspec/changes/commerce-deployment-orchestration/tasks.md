# Tasks: Commerce Deployment Orchestration

## Review Workload Forecast

| Field | Value |
|---|---|
| Estimated changed lines | ~1,730 (80+600+450+350+250) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 -> PR 2 -> PR 3 -> PR 4 -> PR 5 (per unit) |
| Delivery strategy | exception-ok |
| Chain strategy | feature-branch-chain |
| Review budget | 1,500 lines/PR (raised for this change). Unit 2 alone (~600) would already exceed the smaller 400-line default; under the 1,500 budget it needs no forced split. |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: feature-branch-chain
400-line budget risk: High

### Component Reuse Policy

Reuse, never reimplement: `TenantAuthorizationService`, `IAuditSink`, `CatalogManagementService`, `CustomerOrderingAccessService`, `InstallationIdentityService`, `BranchSyncStore`/`BranchNodeService`. The existing 62 xUnit tests must keep passing unchanged; only Unit 2's `ICloudInboxStore` extraction touches existing code, as a behavior-preserving refactor.

### Suggested Work Units

| Unit | Goal | PR | Focused test command | Runtime harness | Rollback boundary |
|---|---|---|---|---|---|
| 1 | Release gate reads `.github/release-authorization.yml` | PR 1 (base: tracker) | `dotnet test Commerce.sln` (unaffected) + CI gate-step assertion | CI run showing gate reads live file | Revert `.github/release-authorization.yml` + one `release.yml` step |
| 2 | Cloud.Api real host + Postgres adapter | PR 2 (base: PR 1) | `dotnet test Commerce.sln --filter CloudApiTests` | `dotnet run` serves `/health`,`/health/ready`; pooler PoC against Supabase/pooled Postgres | Revert Cloud.Api to library-only csproj |
| 3 | Commerce.Web SPA | PR 3 (base: PR 2) | `npm run build && npm test` | SPA sign-in/catalog/order flow against Unit 2 endpoints | `git revert` restores deleted `Commerce.Web.csproj` |
| 4 | Pos.Windows WPF shell | PR 4 (base: PR 3) | `dotnet test Commerce.sln --filter PosWindowsTests` (composition-root only) | N/A in CI — Windows machine/VM manual run script `deploy/pos-manual-verify.md` | Revert Pos.Windows to library-only csproj |
| 5 | DB migration, compose `full` profile, staging runbook | PR 5 (base: PR 4) | `docker compose --profile full up` + cross-org denial check | Compose `full` profile against Unit 2 image | Revert `deploy/db/migrations/0001_init_rls.sql` + compose diff |

## Unit 1: Release Gate Fix

- [x] 1.1 RED: add CI assertion (or script test) that `.github/workflows/release.yml` fails/flags when `.github/release-authorization.yml` is missing or malformed.
- [x] 1.2 GREEN: create `.github/release-authorization.yml` with per-channel `publication_authorized: true|false` (dev/staging/main); update `release.yml`'s gate step to read this file instead of the archived `openspec/changes/commerce-foundation/state.yaml`.
- [x] 1.3 REFACTOR/verify: confirm gate blocks when `publication_authorized: false` and passes when `true`; confirm `dotnet test Commerce.sln` (62 tests) is unaffected.

## Unit 2: Cloud.Api Real Host

- [x] 2.1 RED `tests/Commerce.Integration/CloudApiHostTests.cs`: `/health` liveness without DB, `/health/ready` unhealthy on broken DB, `TenantScopeEndpointFilter` maps claim -> `CloudTenantScope` and rejects missing/spoofed org claims.
- [x] 2.2 GREEN: convert `Commerce.Cloud.Api.csproj` to `Microsoft.NET.Sdk.Web`; add `Program.cs`, `Endpoints/Sync.cs`, `Endpoints/Catalog.cs`, `Endpoints/Ordering.cs`, `Tenancy/TenantScopeEndpointFilter.cs`, `appsettings*.json`.
- [x] 2.3 REFACTOR: extract `ICloudInboxStore` port from existing `CloudInboxStore`; `CloudInboxStore` becomes the port's in-memory test-double implementation with no behavior change (`dotnet test Commerce.sln --filter SyncTests` still green).
- [x] 2.4 RED `tests/Commerce.Integration/PostgresCloudInboxStoreTests.cs`: deny/allow parity with `CloudInboxStore`; cross-org read returns zero rows against a live/pooled Postgres.
- [x] 2.5 GREEN `Persistence/PostgresCloudInboxStore.cs`: raw Npgsql, explicit `BeginTransaction` -> `set_config('app.current_org_id', $1, true)` -> query -> `Commit` per operation.
- [x] 2.6 RED/PoC `tests/Commerce.Integration/PoolerScopingTests.cs`: concurrent transaction-pooled connections issuing different `app.current_org_id` values must never observe another org's rows. Pass = zero cross-contamination across N concurrent scoped transactions; fail = any leak, triggering session/direct-connection fallback per design.
- [x] 2.7 GREEN: wire PoC against realistic pooled connection (Supabase pooler or local pgbouncer); record pass/fail and chosen connection mode in `deploy/README.md`.
- [x] 2.8 GREEN: add `Dockerfile` (repo-root build context, repo-root-relative `COPY`) and `railway.json` (`build.builder: DOCKERFILE`, `dockerfilePath`, `watchPatterns`, `deploy.startCommand/healthcheckPath/healthcheckTimeout/restartPolicyType/restartPolicyMaxRetries`).
- [x] 2.9 REFACTOR/verify: full suite `dotnet test Commerce.sln` stays 62/62 green (plus 12 new Unit 2 tests, 74/74 total); new Cloud.Api tests green; Kestrel binds `0.0.0.0` + env `PORT` (verified via built Docker image).

## Unit 3: Commerce.Web SPA

- [x] 3.1 GREEN (removal): delete `Commerce.Web.csproj`, `Management/WebCatalogManagementAdapter.cs`, `Ordering/WebOrderSubmissionAdapter.cs`, and the `Commerce.sln` entry.
- [x] 3.2 GREEN: scaffold Vite + React + TypeScript + Tailwind + shadcn/ui at `src/Commerce.Web/`.
- [x] 3.3 RED (component/e2e test): catalog rename and order submission flows fail against a mocked-down API (proves the SPA calls real HTTP, no hardcoded data).
- [x] 3.4 GREEN: implement cookie-based same-origin auth flow, catalog management screen, order submission screen calling Unit 2 endpoints.
- [x] 3.5 GREEN: wire wwwroot copy stage (Node build stage) into Unit 2's `Dockerfile`.
- [x] 3.6 REFACTOR/verify: `npm run build` succeeds; SPA completes catalog/order flow against a running Cloud.Api; visible error state when API is unreachable.

## Unit 4: Commerce.Pos.Windows WPF Shell

- [ ] 4.1 RED `tests/Commerce.Integration/PosCompositionRootTests.cs`: `App.xaml.cs` composition root resolves `BranchNodeService`, `TenantAuthorizationService`, `IAuditSink` from `HostApplicationBuilder` (the only unit-testable surface here).
- [ ] 4.2 GREEN: convert `Commerce.Pos.Windows.csproj` to `Microsoft.NET.Sdk.Wpf`; add `ProjectReference` to `Commerce.BranchNode`; implement `App.xaml(.cs)` wiring `BranchSyncStore` at `%LOCALAPPDATA%\Incoders\Commerce\branch.db`.
- [ ] 4.3 GREEN: `MainWindow.xaml(.cs)` minimal shell; `CloudSyncClient` (HttpClient, installation-bound bearer auth) against Unit 2's `/sync`.
- [ ] 4.4 Manual verification (not CI-verifiable): write `deploy/pos-manual-verify.md` run script — launch on Windows machine/VM, confirm shell renders and BranchNode initializes in-process, commit one offline sale, verify sync against a running Cloud.Api. "Done" = checklist executed once and results recorded in the PR description, not a CI job.

## Unit 5: DB Migration, Compose, Staging Runbook

- [ ] 5.1 GREEN `deploy/db/migrations/0001_init_rls.sql`: idempotent, matches `deploy/dev/db/init-rls.sql` policy shape (FORCE RLS, `app_runtime` role, `current_setting('app.current_org_id')`).
- [ ] 5.2 RED/GREEN: apply migration against a scratch Postgres, verify cross-org read denial and table-owner denial (reuses Unit 2's `PostgresCloudInboxStoreTests` fixture pattern).
- [ ] 5.3 GREEN `deploy/README.md`: document one-time-per-environment `psql` migration apply and `app_runtime` role provisioning.
- [ ] 5.4 GREEN `deploy/dev/compose.yaml`: add containerized Cloud.Api service under a `full` profile, building Unit 2's Dockerfile.
- [ ] 5.5 GREEN `deploy/staging-runbook.md`: document manual out-of-repo steps only (Railway project creation, Supabase project creation, required env vars) — no task executes these steps; they remain the user's manual action.
- [ ] 5.6 REFACTOR/verify: `docker compose --profile full up` serves Cloud.Api + Postgres + Web locally; POS runs without Docker requirement.
