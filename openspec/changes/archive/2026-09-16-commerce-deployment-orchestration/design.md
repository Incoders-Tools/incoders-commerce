# Design: Commerce deployment orchestration

## Technical Approach

Additive hosting layer over commerce-foundation's 7 libraries. Each deployable gains an entry point plus environment-driven configuration; no domain rewrite and no change to the 62 xUnit tests. `Commerce.Cloud.Api` becomes `Microsoft.NET.Sdk.Web` and gains a real Npgsql adapter behind an extracted `ICloudInboxStore` port, so the existing in-memory RLS-equivalent double survives as the test implementation. `Commerce.Web` stops being a C# pass-through and becomes the React SPA, served same-origin from the API. `Commerce.Pos.Windows` becomes `Microsoft.NET.Sdk.Wpf` and hosts `Commerce.BranchNode` in-process. Railway + Supabase apply only to the API service; the WPF client is never deployed.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| HTTP style | Minimal API endpoint groups (`Endpoints/Sync.cs`, `Endpoints/Catalog.cs`, `Endpoints/Ordering.cs`) called from `Program.cs`. `CatalogManagementService`, `TenantAuthorizationService`, and `CustomerOrderingAccessService` are synchronous methods over explicit request records; controllers add model-binding and convention machinery that buys nothing over a thin `Results.*` mapping, and endpoint filters express the tenant-scope concern more directly than action filters. | MVC controllers — extra ceremony, no reuse gain. |
| `Commerce.Web` shape | **Delete** `Commerce.Web.csproj`, `Management/WebCatalogManagementAdapter.cs`, `Ordering/WebOrderSubmissionAdapter.cs`, and its `Commerce.sln` entry. `src/Commerce.Web/` becomes the SPA root (`package.json`, `vite.config.ts`, `tsconfig.json`, `src/`, `components.json`). Those two adapters were compile-time proof that the web channel calls the shared contract; the real web channel now calls it over HTTP, where `Cloud.Api` invokes `CloudCatalogManagementAdapter`/`CloudOrderSubmissionService` server-side. Keeping them creates a second, unreachable in-process call path that no deployed process executes. Directory name preserves the screaming-architecture capability name. | Keeping a parallel `web/` directory — two homes for one capability, and the dead C# adapters still rot. |
| BranchNode hosting | `Commerce.Pos.Windows` takes a `ProjectReference` to `Commerce.BranchNode` and composes it in `App.xaml.cs` via `HostApplicationBuilder`: `BranchSyncStore` (SQLite file under `%LOCALAPPDATA%\Incoders\Commerce\branch.db`), `BranchNodeService`, `TenantAuthorizationService`, `IAuditSink`, and an `HttpClient`-based `CloudSyncClient`. No IPC, no localhost socket, no service host. | Windows Service + loopback HTTP — explicit non-goal, deferred to the multi-station change. |
| Persistence access | Raw `NpgsqlDataSource` + parameterized commands in `Persistence/PostgresCloudInboxStore.cs`. The repo has zero EF Core and `BranchSyncStore` is already hand-written ADO.NET; `init-rls.sql` is hand-authored DDL. Introducing EF here adds a mapping layer whose change tracking must still be forced into explicit transactions for RLS scoping. | EF Core + `DbContext` — new dependency, fights the per-transaction `SET LOCAL` requirement. |
| Pooling + tenant scope | Supabase **transaction-mode** pooler (Supavisor, port `6543`). `SET LOCAL` / `set_config(..., is_local: true)` is *transaction*-scoped by definition, so it cannot leak to the next client that borrows the server connection — transaction pooling is therefore **safe, and in fact safer than session mode**, provided every tenant-scoped operation opens an explicit `NpgsqlTransaction` and issues the scope as its first statement. The dangerous shape (session-level `SET` on a connection returned to a shared pool) is the one we forbid. Still flagged as the highest-risk item: tasks MUST carry a runnable PoC asserting isolation, not trust this reasoning. Fallback if the PoC fails: session-mode/direct port `5432` with the same explicit-transaction wrapper. | Session-mode by default — fewer available connections, no isolation benefit. |
| Schema/RLS application | Repo-owned idempotent raw SQL at `deploy/db/migrations/0001_init_rls.sql` (promoted from `deploy/dev/db/init-rls.sql`, which compose keeps consuming), applied once per environment via `psql` against the Supabase direct connection, documented in `deploy/README.md`. Cloud.Api *verifies* at readiness (table exists, RLS forced, policy present) and fails `/health/ready` otherwise — it never applies DDL at runtime. The repo has no migration tooling; adding EF migrations or the Supabase CLI imports a whole toolchain for one file, and Supabase CLI is BaaS-oriented tooling we are otherwise refusing. | EF migrations / Supabase CLI — disproportionate tooling for one idempotent script. |
| SPA delivery | The SPA is built in the API Dockerfile's Node stage and copied into `wwwroot`; Cloud.Api serves it with `UseDefaultFiles` + `UseStaticFiles` + `MapFallbackToFile("index.html")`. One Railway service, one origin, so Identity's auth cookie is first-party — no CORS, no `SameSite=None`. | Separate Railway static service — second origin forces cross-site cookies or a token scheme, directly worsening the auth decision below. |
| Browser auth | ASP.NET Core Identity **cookie** scheme (`HttpOnly`, `Secure`, `SameSite=Lax`) — sole identity provider per ADR-002, no Supabase Auth, no `service_role`, no anon key. The `org_id` claim is stamped at sign-in and is the only source of `CloudTenantScope`. | JWT in browser storage — XSS-exfiltratable, needs refresh rotation, and buys nothing same-origin. |
| Device auth | Pos.Windows → Cloud.Api sync uses a second scheme: installation-bound bearer credential resolved from `InstallationIdentityService`. A desktop process cannot carry a browser cookie; endpoints select scheme by authorization policy. | Reusing the cookie — no browser, no cookie jar. |
| Environment config | `appsettings.json` + `appsettings.{Environment}.json` keyed off `ASPNETCORE_ENVIRONMENT` injected by Railway, with `ConnectionStrings__Commerce` and `Commerce__CloudApiBaseUrl` as env vars. No literal `"staging"` anywhere in code or `railway.json`; adding production means provisioning resources and setting variables. | Per-environment code branches. |
| Deploy mechanism | Railway GitHub push-to-deploy driven by in-repo `railway.json` (`build.builder: "DOCKERFILE"`, `build.dockerfilePath`, `build.watchPatterns`, `deploy.startCommand`, `deploy.healthcheckPath`, `deploy.healthcheckTimeout`, `deploy.restartPolicyType`, `deploy.restartPolicyMaxRetries`) plus a Dockerfile. Railway's top-level `build` object has **no `dockerContext` property** outside a multi-service `services[]` array, so the build context is always the repo root — every `COPY` is repo-root-relative (`COPY src/Commerce.Cloud.Api/Commerce.Cloud.Api.csproj src/Commerce.Cloud.Api/`). Kestrel binds `0.0.0.0` on the injected `PORT`. | A GitHub Actions Railway-CLI deploy step — duplicates Railway's own trigger and adds a deploy token secret. |
| CI scope | `release.yml` stays build/test/stage/channel-labeling on `windows-latest`; it performs **no** Railway action, because Railway's GitHub integration plus `watchPatterns` already owns deploy. The stale `openspec/changes/commerce-foundation/state.yaml -> release_publication_authorized` gate is replaced by a long-lived repo policy file `.github/release-authorization.yml` (per-channel `publication_authorized: true|false`) read by the gate step. SDD change state is ephemeral and archives away; publication authorization is permanent repo policy and must not live inside a closed change. | Removing the gate — loses ADR-004's authorization requirement. |
| Local dev | `deploy/dev/compose.yaml` keeps Postgres as the default service for the fast `dotnet run` inner loop, and adds a `cloud-api` service behind the `full` profile that builds the same repo-root Dockerfile, so `docker compose --profile full up` gives a true end-to-end stack that exercises the exact production image. | API-only-in-Docker (slow inner loop) or `dotnet run`-only (never exercises the shipped image). |

## Data Flow

```text
Browser SPA (src/Commerce.Web, same origin)
   -> Identity cookie -> Cloud.Api Minimal API endpoint
      -> tenant-scope endpoint filter: org_id claim -> CloudTenantScope
         -> CatalogManagementService / CloudOrderSubmissionService
            -> BEGIN; set_config('app.current_org_id', $org, true); <query>; COMMIT
               -> Supabase Postgres (FORCE RLS, app_runtime role)

WPF Pos.Windows process
   ├─ in-process: BranchNodeService -> BranchSyncStore -> local SQLite (always, every environment)
   └─ HTTP + installation bearer -> Cloud.Api /sync -> ICloudInboxStore -> Postgres -> ACK
```

Pos.Windows local persistence is SQLite in every environment; only Cloud.Api's Postgres target varies by environment (local Docker vs. Supabase per environment).

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `src/Commerce.Cloud.Api/Commerce.Cloud.Api.csproj` | Modify | `Microsoft.NET.Sdk.Web`, Npgsql + Identity packages |
| `src/Commerce.Cloud.Api/Program.cs`, `Endpoints/*.cs` | Create | Host, endpoint groups, `/health`, `/health/ready` |
| `src/Commerce.Cloud.Api/Tenancy/TenantScopeEndpointFilter.cs` | Create | Claim → `CloudTenantScope`, 401/403 |
| `src/Commerce.Cloud.Api/ICloudInboxStore.cs` | Create | Port extracted from existing double |
| `src/Commerce.Cloud.Api/CloudInboxStore.cs` | Modify | Implements the port; stays the test double |
| `src/Commerce.Cloud.Api/Persistence/PostgresCloudInboxStore.cs` | Create | Real Npgsql adapter, explicit-transaction scoping |
| `src/Commerce.Cloud.Api/appsettings*.json` | Create | Environment-driven config |
| `Dockerfile`, `railway.json`, `.dockerignore` | Create | Repo-root-context build + Railway deploy |
| `src/Commerce.Web/Commerce.Web.csproj`, `Management/`, `Ordering/` | Delete | Superseded by the SPA + server-side adapters |
| `src/Commerce.Web/package.json`, `vite.config.ts`, `src/**` | Create | React + TS + Tailwind + shadcn/ui SPA |
| `src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` | Modify | WPF SDK, `ProjectReference` to BranchNode |
| `src/Commerce.Pos.Windows/App.xaml(.cs)`, `MainWindow.xaml(.cs)` | Create | Shell + DI composition root |
| `deploy/db/migrations/0001_init_rls.sql`, `deploy/README.md` | Create | Per-environment schema/RLS application |
| `deploy/dev/compose.yaml` | Modify | `full` profile with containerized Cloud.Api |
| `.github/release-authorization.yml`, `.github/workflows/release.yml` | Create/Modify | Live authorization source replacing archived state file |
| `Commerce.sln` | Modify | Drop `Commerce.Web` project entry |

## Interfaces / Contracts

```csharp
public interface ICloudInboxStore
{
    InboundApplyResult TryApplyInbound(CloudTenantScope scope, SyncEnvelope envelope);
    bool Acknowledge(CloudTenantScope scope, Guid operationId);
    SyncOperationStatus? GetStatus(CloudTenantScope scope, Guid operationId);
    IReadOnlyList<SyncEnvelope> GetInboxFor(CloudTenantScope scope);
}
```

Tenant scoping is always the first statement inside the same transaction as the work it protects; `SET` cannot be parameterized, so use `set_config` with `is_local: true`:

```csharp
await using var tx = await connection.BeginTransactionAsync(ct);
await using (var scopeCmd = new NpgsqlCommand(
    "SELECT set_config('app.current_org_id', $1, true)", connection, tx))
{
    scopeCmd.Parameters.AddWithValue(scope.OrganizationId.ToString());
    await scopeCmd.ExecuteNonQueryAsync(ct);
}
// ... tenant-scoped commands on the SAME transaction ...
await tx.CommitAsync(ct);
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | Claim → `CloudTenantScope` derivation; rejection of body/route-supplied org IDs; endpoint→service mapping | xUnit against the filter and endpoint handlers |
| Integration | `PostgresCloudInboxStore` deny/allow parity with `CloudInboxStore`; cross-org read returns zero rows; **pooler PoC**: concurrent transaction-pooled clients never observe another client's `app.current_org_id` | Live Postgres from `deploy/dev/compose.yaml`, then repeated against the Supabase pooler endpoint |
| Integration | `/health` liveness without DB; `/health/ready` fails when RLS/policy missing | `WebApplicationFactory` + toggled schema fixture |
| E2E | SPA sign-in → catalog rename → order submit against a running API; Pos.Windows launches, commits an offline sale to SQLite, syncs to the API | Manual scripted run on Windows + staging URL, recorded in `deploy/README.md` |

Existing 62 xUnit tests must pass unchanged; the extracted port keeps their in-memory implementation intact.

## Threat Matrix

| Native row | Applicability |
|---|---|
| Documentation-like paths | N/A — no file-classification boundary in this change |
| Git repository selection | N/A — no product code runs Git |
| Commit state | N/A — no commit automation added |
| Push state | Applicable — Railway deploys on push to a linked branch. Safe behavior: deploy is triggered by Railway's own integration and constrained by `build.watchPatterns`; the repo adds no push automation and CI holds no deploy token. RED test: `railway.json` schema/watch-pattern assertion in CI. |
| PR commands | N/A — no PR automation added |

The release gate reads a static YAML policy file; it executes no shell-interpolated user input. Dockerfile `COPY` paths are literal and repo-root-relative.

## Migration / Rollout

Forward-only. Per environment: create the Supabase project, run `0001_init_rls.sql`, grant `app_runtime`, set Railway variables, push to the linked branch. Only staging is provisioned in this change; production is the same procedure with different variable values and no code change. Rollback is per unit — deleting the entry points, Dockerfile, `railway.json`, and SPA returns the projects to library-only state; Railway rolls back to the prior image from deployment history; the SQL script reverts by dropping the added policy/role/table.

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | Release gate fix: `.github/release-authorization.yml` + `release.yml` step | ~80 | CI run on a branch push shows gate reading the live file | Revert one commit; CI returns to stale reference |
| 2 | Cloud.Api host: Web SDK, `Program.cs`, endpoints, Identity + tenant filter, `ICloudInboxStore`, `PostgresCloudInboxStore`, appsettings, Dockerfile, `railway.json` | ~600 | `dotnet run` serves `/health`; integration + pooler PoC green against compose Postgres | Revert to library-only csproj; 62 tests unaffected |
| 3 | `Commerce.Web` SPA: delete C# project, Vite/TS/Tailwind/shadcn scaffold, auth + catalog/order screens, wwwroot copy stage in Dockerfile | ~450 | `npm run build` + SPA loads and completes a catalog/order flow against Unit 2's API | Restore deleted csproj from Git; API still serves API-only |
| 4 | `Commerce.Pos.Windows` WPF shell: WPF SDK, `App.xaml(.cs)` composition root, `MainWindow`, in-process BranchNode + SQLite + sync client | ~350 | Launches on Windows, commits an offline sale, syncs to local or staging API | Revert to library-only csproj |
| 5 | `deploy/db/migrations/0001_init_rls.sql`, compose `full` profile, `deploy/README.md` staging provisioning runbook | ~250 | `docker compose --profile full up` serves the whole stack; cross-org read denied on Supabase staging | Revert; dev compose returns to Postgres-only |

Decision needed before apply: Yes
Chained PRs recommended: Yes
400-line budget risk: High

Units ship in order; each targets the previous unit's branch in a Feature Branch Chain. Unit 1 is independent and can ship first against the tracker branch.

## Open Questions

- [ ] Supabase transaction-pooler behavior with `set_config(..., true)` is reasoned-safe but unproven — Unit 2's PoC decides whether the session-mode fallback is needed.
- [ ] Installation-bound device credential issuance/rotation for Pos.Windows is scoped here as a bearer scheme; its lifecycle policy may deserve its own ADR.
