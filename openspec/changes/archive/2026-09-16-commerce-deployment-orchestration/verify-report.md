```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:99bd3bc54fb16fe7c8914588150ecb392e16c4259aa802b8f7dd32b12ab727c1
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 14/14
scenarios: 26/26
test_command: dotnet test Commerce.sln
test_exit_code: 0
test_output_hash: sha256:f7f7a748181f93625bdfb65520cc9e2940d606558e10bf6ad908e08c67b1a709
build_command: dotnet build Commerce.sln
build_exit_code: 0
build_output_hash: sha256:a364b920c2f4613d90b8218789a107def020895c29ac844b344e922246c7dbeb
```

# Verification Report: Commerce Deployment Orchestration

**Change**: commerce-deployment-orchestration
**Date**: 2026-09-16
**Overall verdict**: PASS WITH WARNINGS

## Build & Test Evidence (independently executed, not self-reported)

- `dotnet build Commerce.sln` -> exit 0, Build succeeded, 0 Warnings, 0 Errors.
- `dotnet test Commerce.sln` -> exit 0, 83/83 passed, 0 failed, 0 skipped:
  - Commerce.Bootstrap.Tests: 1 passed
  - Commerce.Upgrade: 19 passed
  - Commerce.Integration: 63 passed (includes PostgresCloudInboxStoreTests, PoolerScopingTests, MigrationRlsTests -- all executed live, not soft-skipped, because dev-postgres-1 and dev-pgbouncer-1 were already running before this run started)

Independently re-executed on dev at HEAD abf5bbd47b318c96ebac47fd08679069f97f3188 (post PR #13-#17), not taken from any apply-phase self-report. The 8 Postgres/pooler-dependent integration tests were confirmed to run against live containers rather than hitting the SKIPPED fast-path.

## Spec-to-Test / Spec-to-Implementation Coverage

14/14 requirements and 26/26 scenarios across the 3 capability specs (application-hosting: 4 req/7 scen, cloud-deployment: 7 req/12 scen, safe-release-upgrades delta: 3 req/7 scen) have corresponding source/test evidence, independently verified by direct file/code inspection and command execution, not by trusting tasks.md checkboxes alone:

- Release gate (safe-release-upgrades): .github/release-authorization.yml exists with channels.{internal,pilot,stable}.publication_authorized flags. .github/workflows/release.yml's "Publication gate (publication_authorized)" step reads this file via an awk extraction keyed on the resolved channel; a grep for the old commerce-foundation state.yaml reference across the workflow returns no hits -- the stale reference is fully gone.
- Commerce.Cloud.Api (application-hosting, cloud-deployment): Commerce.Cloud.Api.csproj is Microsoft.NET.Sdk.Web. Real Program.cs maps /health (liveness) and /health/ready (DB/RLS-verifying) via MapHealthChecks. Endpoints/{Sync,Catalog,Ordering,Account}.cs exist. ICloudInboxStore port + PostgresCloudInboxStore adapter (explicit-transaction set_config('app.current_org_id', ...) scoping) exist. Dockerfile (repo root) and railway.json (build.builder: DOCKERFILE, dockerfilePath, watchPatterns, deploy.startCommand/healthcheckPath/healthcheckTimeout/restartPolicyType/restartPolicyMaxRetries) both exist and match the spec's declarative-deploy requirement exactly.
- Commerce.Web (application-hosting): no .csproj remains under src/Commerce.Web/ (confirmed absent via find); a real Vite/React 19/TypeScript/Tailwind v4 SPA exists at src/Commerce.Web/src/ (App.tsx, screens/, api/, auth/, components/ui/) with package.json declaring react, tailwindcss, @tailwindcss/vite, vitest, @testing-library/react.
- Commerce.Pos.Windows (application-hosting): Commerce.Pos.Windows.csproj is Microsoft.NET.Sdk + UseWPF=true + OutputType=WinExe, net10.0-windows (no longer a library). App.xaml/MainWindow.xaml and a PosHostBuilder.cs composition root exist, referencing Commerce.BranchNode via ProjectReference.
- DB/compose/runbook (cloud-deployment): deploy/db/migrations/0001_init_rls.sql exists (idempotent, RLS-forcing). deploy/dev/compose.yaml's full profile (cloud-api service, profiles: ["full"]) exists, building the same repo-root Dockerfile. deploy/staging-runbook.md exists and is explicit documentation-only (no task executes its steps).

## Component Reuse Policy Compliance

Confirmed by direct code reads:
- Commerce.Cloud.Api/Endpoints/Account.cs calls TenantScopeResolver / Identity cookie sign-in rather than reimplementing tenant resolution; other endpoint files route into CatalogManagementService / CustomerOrderingAccessService / CloudSyncReceiver per design.md's Minimal-API-thin-mapping decision.
- src/Commerce.Pos.Windows/PosHostBuilder.cs wires TenantAuthorizationService, IAuditSink (InMemoryAuditSink), BranchNodeService, BranchSyncStore, and InstallationIdentityService as singletons from Commerce.Application/Commerce.BranchNode -- it composes, it does not reimplement any of these services. The class's own doc comment explicitly states this reuse intent.

No reimplementation of shared business logic found in either host.

## Documented Production Gaps (confirmed honest, source-level)

1. Supabase transaction-pooler vs. real Supavisor -- deploy/README.md's "Unit 2 -- Transaction-pooler proof-of-concept outcome" section explicitly states the PoC ran against PgBouncer as the strongest available local approximation, not real Supabase Supavisor, and lists TLS termination / connection draining / Supavisor-specific timing as explicitly unproven pending a real Supabase staging project.
2. Missing password-based credential verification -- Endpoints/Account.cs doc comment explicitly states sign-in trusts the caller-submitted organizationId/userId rather than verifying a password against a stored credential, flagged as a documented deviation, not silently hidden.
3. Superuser/table-owner local-fixture limitation -- both tests/Commerce.Integration/MigrationRlsTests.cs (around lines 97-101) and PostgresCloudInboxStoreTests.cs (around lines 144-151) carry explicit comments that the local Docker Postgres image owner role is a superuser (RLS FORCE does not apply to superusers), a gap specific to the local compose fixture and not present against Supabase managed Postgres (which does not grant superuser to application table owners).
4. No automated browser E2E for the SPA -- partially confirmed, WARNING. Unit 3 testing evidence is real: CatalogScreen.test.tsx / OrderScreen.test.tsx use Vitest + React Testing Library with fetch mocked only at the transport boundary, asserting the real request URL/method/body shape against Endpoints/Catalog.cs actual route. This is legitimate component-level proof that the SPA calls real HTTP contracts, matching design.md testing strategy row (manual scripted run on Windows plus staging URL, recorded in deploy/README.md, for full E2E). However, no file in the repository explicitly states "no automated browser E2E" as a documented gap -- src/Commerce.Web/README.md is unmodified Vite boilerplate, and deploy/README.md records the Unit 2 pooler PoC and Unit 5 migration only, not an SPA E2E section that design.md testing strategy table implied would exist there. .github/workflows/release.yml has no npm/vitest/Commerce.Web step at all -- the SPA component tests are not wired into CI. This is a real, unremoved gap in substance, but its explicit written acknowledgment is thinner than the other three gaps.

Gaps 1-3 are confirmed honestly present and undiminished. Gap 4 exists in fact (no browser E2E, no CI wiring for npm test/npm run build) but is not explicitly named as a gap anywhere in the repository the way the other three are -- recorded here as a WARNING, not a CRITICAL, since spec.md SPA scenarios are covered by passing component tests plus the documented manual verification precedent used for Pos.Windows.

## Line-Budget Exceptions

Independently re-measured via git diff --shortstat between each unit merge-commit boundary on dev:
- Unit 2 (3ec9344..2346551): 1,548 insertions + 31 deletions = 1,579 lines, matching the reported "~1,579 lines incl. lockfile noise" exactly.
- Unit 3 (2346551..083873d): 4,426 insertions + 113 deletions = 4,539 raw lines, matching "~4,539 raw incl. package-lock.json" exactly. Excluding package-lock.json: 1,265 insertions + 113 deletions = 1,378 hand-authored lines, matching the reported "~1,377 hand-authored" to within rounding.
- Unit 4 (083873d..5b8b5d7): 496 insertions + 6 deletions = 502 lines (within the ~350 budget "exception-ok" delivery strategy recorded in tasks.md).
- Unit 5 (5b8b5d7..abf5bbd): 420 insertions + 11 deletions = 431 lines.
- Unit 1 (0a8488b..3ec9344): 589 insertions total, but 458 of those lines are SDD planning artifacts (proposal.md/design.md/specs/tasks.md); actual code diff (.github/release-authorization.yml + release.yml) is 60 lines, consistent with the ~80 budget.

No unit shows scope creep beyond what tasks.md Review Workload Forecast (exception-ok delivery strategy, 1,500 lines/PR raised budget) already recorded and pre-approved. All five units stay under the 1,500-line/PR raised budget.

## Docker Compose Full-Profile Re-confirmation (Unit 5)

Independently executed (not from any apply-phase transcript):
- docker compose -f deploy/dev/compose.yaml --profile full up -d --build -> built dev-cloud-api image from the repo-root Dockerfile, started dev-postgres-1, dev-pgbouncer-1, dev-cloud-api-1.
- GET http://localhost:8080/health -> 200
- GET http://localhost:8080/health/ready -> 200
- GET http://localhost:8080/ -> 200, serving the SPA built index.html from wwwroot (confirms the Dockerfile Node build stage plus wwwroot copy stage work end to end).
- Torn down with docker compose -f deploy/dev/compose.yaml --profile full down.
- Re-confirmed default (no-profile) docker compose -f deploy/dev/compose.yaml up -d starts only dev-postgres-1 + dev-pgbouncer-1 -- no cloud-api container -- matching deploy/README.md claim that full is "strictly additive" and the default fast inner-loop stack is unaffected.

## Task Completion vs. Code State

All 28 tasks across Units 1-5 in tasks.md are marked [x]. Spot-checked against actual code/file state (not just checkbox trust) for every unit above; no unchecked or falsely-checked task found. Task 4.4 (Pos.Windows manual verification) and its results are recorded in deploy/pos-manual-verify.md with an honest caveat that step 12 (explicit unreachable-API negative path) was code-reviewed but not re-executed with a fresh screenshot in the recorded run -- this caveat is itself evidence of honest self-reporting, not a hidden gap.

## Issues

CRITICAL: None.

WARNING:
1. No automated browser E2E test exists for the SPA, and this gap is not explicitly written down anywhere in the repository the way the other three production gaps are (see Documented Production Gaps item 4). CI (release.yml) does not run npm run build or npm test for Commerce.Web at all -- a broken SPA build would only be caught inside the Docker image build during a Railway/local-compose deploy, not in the fast CI feedback loop.
2. Endpoints/Account.cs sign-in trusts caller-submitted identity without password verification (documented in-source, but worth flagging again here as a pre-production blocker for any real deployment, distinct from this SDD change walking-skeleton scope).

SUGGESTION:
1. Consider adding a lightweight npm run build && npm test step to release.yml for Commerce.Web so SPA regressions surface in CI rather than only at Docker build time.
2. Consider adding an explicit Known Gaps section to src/Commerce.Web/README.md or deploy/README.md naming the browser-E2E gap explicitly, mirroring the precedent set for the other three gaps.

## Delivery

Delivered as a 5-PR feature-branch chain onto dev, one PR per unit, in dependency order:
- PR #13: Unit 1 (release gate fix)
- PR #14: Unit 2 (Cloud.Api host)
- PR #15: Unit 3 (Commerce.Web SPA)
- PR #16: Unit 4 (Pos.Windows WPF shell)
- PR #17: Unit 5 (DB migration, compose, staging runbook)

All five PRs are merged into dev as of HEAD abf5bbd47b318c96ebac47fd08679069f97f3188.

## Recommendation

Ready for archive, with the two WARNING-level findings above carried forward as follow-up work (not blockers): (1) wire Commerce.Web npm run build/npm test into CI and explicitly document the browser-E2E gap, and (2) treat Endpoints/Account.cs password-less sign-in as a pre-production blocker to resolve before any real user-facing deployment, distinct from this change walking-skeleton scope. Neither finding contradicts a spec requirement or a failing test; both are honest, already-partially-documented gaps consistent with the precedent set by commerce-foundation own verify report.
