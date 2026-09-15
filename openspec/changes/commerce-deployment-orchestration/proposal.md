# Proposal: Commerce Deployment Orchestration

## Intent

commerce-foundation delivered 7 class libraries with **zero runnable entry points**. Nothing can be started, clicked, or deployed. This change turns the walking skeleton into an end-to-end-testable product: a real HTTP API, a real SPA, a real WPF POS, plus staging/production cloud infrastructure for the cloud-facing pieces.

## Scope

### In Scope
- **Commerce.Cloud.Api**: `Microsoft.NET.Sdk.Web` host, `Program.cs`, health endpoint, HTTP surface over existing services; Npgsql adapter replacing the in-memory `CloudInboxStore` test double; `appsettings` + env-var config; Dockerfile (Kestrel binds `0.0.0.0` + Railway `PORT`).
- **Commerce.Web**: new React + TypeScript + Tailwind + shadcn/ui SPA consuming Cloud.Api over HTTP (delivers ADR-001's accepted decision).
- **Commerce.Pos.Windows**: real WPF shell (`Microsoft.NET.Sdk.Wpf`, App.xaml), with **Commerce.BranchNode hosted in-process**; run unsigned locally against local or staging Cloud.Api.
- **Local dev**: extend `deploy/dev/compose.yaml` (Postgres stays dev-only; clients never require Docker).
- **Cloud**: Railway staging + production for Cloud.Api and Web; Supabase as managed Postgres (+ pooler) only.
- **CI/CD**: extend `.github/workflows/release.yml` with a Linux container build/push + Railway deploy job on the existing dev→internal / staging→pilot / main→stable mapping; fix the stale `openspec/changes/commerce-foundation/state.yaml → release_publication_authorized` gate reference.

### Out of Scope
- Standalone BranchNode **Windows Service** host (ADR-001/ADR-005 target — deferred to a later multi-station change).
- MSIX/MSI signed packaging and distribution of Pos.Windows (local unsigned run is in scope; distribution is not).
- Supabase Auth / PostgREST / `service_role` / anon keys — ADR-002 keeps ASP.NET Core Identity as sole identity provider; `service_role` bypasses RLS and would defeat tenant isolation.
- Deploying BranchNode or Pos.Windows to Railway — impossible by architecture; they are validated on a Windows machine or VM.
- Rewriting commerce-foundation domain logic. The 62 xUnit tests keep passing unchanged; this change is additive hosting/infrastructure.

## Capabilities

### New Capabilities
- `application-hosting`: runnable process hosts (ASP.NET Core API, React SPA, WPF POS with in-process branch node) and their configuration/health contracts.
- `cloud-deployment`: environment topology (staging/production), container build, Railway deploy, Supabase-backed persistence, secret/connection configuration.

### Modified Capabilities
- `safe-release-upgrades`: release channel gate must reference a live authorization source (not the archived commerce-foundation state file) and must now cover cloud deploy per channel.

## Approach

Additive hosting layer over existing libraries. Each project gains an entry point and configuration; no domain rewrite. Persistence swaps the RLS test double for a real Npgsql adapter that sets tenant scope per request, preserving `init-rls.sql`'s FORCE-RLS / non-owner-role / `current_setting('app.current_org_id')` design on Supabase as vanilla Postgres. CI gains an `ubuntu-latest` job alongside the existing `windows-latest` job.

**Connection strategy is deliberately unresolved**: design must run a spike on Supabase transaction-pooler behaviour with `SET LOCAL app.current_org_id` before committing (fallback: session/direct connection).

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Commerce.Cloud.Api` | Modified | Web SDK, Program.cs, HTTP endpoints, Npgsql adapter, Dockerfile |
| `src/Commerce.Web` | New | React/TS/Tailwind/shadcn SPA replacing adapter-only project shape |
| `src/Commerce.Pos.Windows` | Modified | WPF SDK, App.xaml, UI shell, in-process BranchNode wiring |
| `deploy/dev/compose.yaml` | Modified | Local dev stack extension |
| `.github/workflows/release.yml` | Modified | Container build/push, Railway deploy, gate fix |

## Manual / Out-of-Repo Actions (user-owned, not agent work)

- Railway account/project/environment creation + GitHub repo linking **or** deploy token generation.
- Supabase project creation + connection-string/pooler retrieval + provisioning the non-owner runtime role.
- GitHub Actions secrets: Railway token, Supabase connection string.
- Custom domain / DNS configuration, if wanted.
- Windows code-signing certificate acquisition (ADR-004/ADR-005) — only needed if distribution is later pulled in; not required for this change's local unsigned run.

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Supabase pooler + `SET LOCAL` tenant scoping unverified | High | Spike in design before choosing connection strategy; session/direct connection fallback |
| Scope size (API + SPA + WPF + CI/CD) exceeds 400-line review budget | High | Chained work units like commerce-foundation; slice per host |
| Supabase BaaS features leak in and bypass RLS | Medium | Explicit non-goal; design forbids `service_role`/anon keys |
| Stale release.yml gate silently references archived change | High | Fix independently of whatever else lands |
| Railway .NET support requires Dockerfile (no Nixpacks) | Low | Dockerfile is in scope; documented constraint |

## Rollback Plan

All work is additive. Revert per unit: deleting new entry points/Dockerfile/SPA returns projects to library-only state; the 62 existing tests are untouched. Railway deploys roll back to the previous image via Railway's deployment history; Supabase schema changes are forward-only SQL applied from `init-rls.sql` and revertible by dropping the added objects. Reverting the release.yml job restores build/test-only CI.

## Dependencies

- User-completed manual actions above (Railway, Supabase, GitHub secrets) block cloud deploy verification.
- A Windows machine or VM for Pos.Windows validation.

## Success Criteria

- [ ] `dotnet run` on Commerce.Cloud.Api serves a health endpoint backed by real Postgres.
- [ ] Commerce.Web SPA loads and performs a catalog/order flow against Cloud.Api.
- [ ] Commerce.Pos.Windows launches on Windows with in-process BranchNode and reaches local or staging Cloud.Api.
- [ ] Push to `staging` deploys Cloud.Api + Web to Railway staging; push to `main` deploys production.
- [ ] Tenant isolation verified against the deployed database (cross-org read returns nothing).
- [ ] release.yml no longer references the archived commerce-foundation state file.
- [ ] All 62 existing xUnit tests still pass.

## Proposal question round

Exploration ambiguities 1 (Commerce.Web stack), 2 (BranchNode hosting), and 4 (what "staging" means) were resolved by explicit user decision and are recorded above as fixed inputs. Remaining open items for user review: (a) one Supabase project per environment vs. one project with schema-per-environment; (b) Railway GitHub-native auto-deploy vs. a CLI-based GitHub Action; (c) whether a `production` Railway environment should be provisioned now or only after staging proves out.
