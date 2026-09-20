# Proposal: Commerce Per-App Path-Filtered CI/CD (Phase G)

## Intent

**The pipeline exists and runs everything, every time.** `.github/workflows/release.yml` (261 lines, five jobs) is triggered exclusively by `push: branches: [dev, staging, main]`. There is **no `pull_request` trigger at all**, and **no job carries a `paths:`/`paths-ignore:` filter**. Consequences, verified in that file:

- A CSS-only edit in `src/Commerce.Web` still starts `build` on `runs-on: windows-latest` (line ~186) and runs `dotnet restore/build/test Commerce.sln` over the whole solution.
- A `Commerce.Domain` edit still runs `web-tests` (line ~64, `npm ci && npm test && npm run build`) and `web-e2e` (line ~94), which spins up a live Cloud.Api plus a Postgres service container and Playwright.
- A `docs/**`, `openspec/**` or root `*.md`-only commit runs all five jobs.
- Because there is no `pull_request` trigger, **no CI signal exists on a PR at all** — every job result arrives only after merge to `dev`/`staging`/`main`. The pipeline is a post-merge report, not a gate.

The `web-tests` job's own comment already states it "never blocks or is blocked by" the .NET build job — the independence ADR-007 asks for is partially acknowledged in code but never expressed as job selection.

**Why now.** ADR-007 line 17 defers this explicitly: "Per-app path-filtered CI/CD is deferred to its own change." It is the last unstarted phase of the platform roadmap, and the cost is paid on every push today.

**Fact to correct as part of this change**: `openspec/config.yaml` declares `testing.ci: {available: false, command: null}`. That is **stale and inaccurate** — a real five-job pipeline exists. The field must be updated to reflect reality when this change lands; that correction is bookkeeping, not a scope item to design.

This change is **planning only** (`openspec/config.yaml` `approval_scope: planning-only`).

## Scope

### In Scope

1. **Add a `pull_request` trigger** to `.github/workflows/release.yml` (or a companion workflow) so CI runs as a pre-merge gate, not only post-merge. Locked by the user as part of this phase, not a later one: path filtering is worth most on a PR.
2. **Path-filter the two independently-buildable unit groups** as they exist today:
   - **Web group** (`web-tests`, `web-e2e`) — triggered by `src/Commerce.Web/**`.
   - **.NET group** (`build`) — triggered by the shared `Commerce.sln` surface (`src/Commerce.*` excluding Web, `tests/**`, `Directory.*.props`, `Commerce.sln`, `global.json` and equivalents).
3. **Skip all code jobs for documentation-only changes** (`docs/**`, `openspec/**`, root `*.md`, and comparable non-build paths), while keeping any required-check contract satisfied (see Decision 3).
4. **Define the required-status-check contract** under path filtering — which checks a PR must have green, and how a legitimately skipped job satisfies them (the GitHub skipped-job footgun, Decision 3).
5. **Preserve the channel and authorization semantics unchanged**: `determine-channel` (ADR-004 branch→channel mapping), `verify-protected-source`, and the publication-authorization gate reading `.github/release-authorization.yml` keep their current behavior on push to `dev`/`staging`/`main`.
6. **Decide the trigger matrix explicitly**: which jobs run on `pull_request`, which on `push`, and which on both — including whether `web-e2e` (the most expensive job) belongs on every PR.

### Out of Scope (non-goals)

- **Splitting `Commerce.sln` into per-project CI jobs.** ADR-001 establishes one solution and one `dotnet test Commerce.sln` harness; ADR-007 explicitly retains it ("`Commerce.sln` remains the single build/test entry point"). This change filters *whether* the .NET group runs, never *which subset* of it runs. Not re-litigated here.
- **`railway.json` `build.watchPatterns`.** It already path-filters Cloud.Api **deployment**, and it is **missing `src/Commerce.Web/**` even though Web ships inside the Cloud.Api Docker image** (ADR-007 line 9). That is a real, separate defect in *deploy-trigger* filtering, a different mechanism from *CI-job* filtering. **Named here, deliberately not solved here** — it warrants its own change so a deploy-trigger decision is not smuggled in under a CI proposal.
- **MSIX/MSI signing and distribution for `Commerce.Pos.Windows` / `Commerce.Updater`.** Packaging inside the shared `build` job is a placeholder with no real distribution pipeline; ADR-005 owns signing/distribution. Out of scope.
- **Changing what any job does internally** — no test-suite restructuring, no `npm test` or `dotnet test` command changes, no runner-image migration off `windows-latest`.
- **Caching, matrix builds, concurrency groups, self-hosted runners, or pipeline speed work beyond job selection.**
- **Merge-queue adoption, branch-protection rule authorship, or repository-settings changes** beyond documenting the required-check contract this change implies.
- **Moving to a multi-workflow or reusable-workflow architecture** as an end in itself; structural splitting is admissible only if it is the mechanism chosen to implement the filters (see deferred items).

## Decisions

### Locked here

1. **`pull_request` trigger ships in this phase, together with path filtering.** User decision: path filtering is valuable primarily as a PR gate; delivering filters against a push-only pipeline would leave the main benefit unrealized.
2. **Two unit groups, not N.** The only independently-buildable units today are `src/Commerce.Web` (own `package.json`, npm/Vite/React, **not** in `Commerce.sln`) and everything else (one `Commerce.sln`). Filters are authored against exactly these two groups plus a docs-only exclusion. Inventing a finer split contradicts ADR-001/ADR-007.
3. **The GitHub skipped-job/required-check interaction is a named design constraint, not a discovery.** A job skipped by a path filter does **not** report a conclusion that satisfies a required status check; a PR can hang "waiting" forever. Any required-check policy implied by the new `pull_request` trigger MUST be resolved explicitly — e.g. an always-run gate/placeholder job that aggregates the filtered jobs' results and reports success when they were legitimately skipped. Design must choose a mechanism; it may not leave this to runtime discovery.
4. **Release semantics stay byte-equivalent on push.** `determine-channel`, `verify-protected-source`, and the `.github/release-authorization.yml` publication gate are untouched in behavior. Filtering must never cause a release-channel push to skip a job that authorizes or gates publication. When in doubt on a push to `dev`/`staging`/`main`, **run the job** — filtering aggressiveness is a PR-side optimization first.
5. **`openspec/config.yaml` `testing.ci.available: false` is factually wrong and gets corrected** when this change lands (to `available: true` with the real workflow reference). Bookkeeping, not a design decision.

### Explicitly deferred to `sdd-design`

- Filter mechanism: workflow-level `paths:` vs. job-level `dorny/paths-filter`-style change detection vs. splitting `release.yml` into multiple workflows. Each interacts differently with Decision 3.
- The exact glob sets per group, including shared-root files (`Dockerfile`, `.github/workflows/**`, `deploy/**`, lockfiles) that should conservatively trigger **both** groups.
- Whether `web-e2e` runs on every PR, on a label/path condition, or only on push to release branches — it is the heaviest job (live Cloud.Api + Postgres service container + Playwright).
- The concrete always-run gate-job shape for Decision 3, and whether it also reports for `push` events.
- Whether `pull_request` runs against all branches or only PRs targeting `dev`/`staging`/`main`, and how `pull_request` interacts with `determine-channel`, which is branch-mapped by design.
- Whether documentation-only detection uses `paths-ignore` on the trigger or an explicit docs-only job condition (the two differ exactly in the required-check behavior of Decision 3).

### Product questions — answered by the user 2026-09-20

- (a) **PR checks are required (a true gate), not advisory.** Confirms Decision 3's always-run gate/aggregator job is mandatory infrastructure, not a nice-to-have, and that a branch-protection required-check update is squarely in this change's delivery path.
- (b) **`web-e2e` runs on every PR that touches Web**, not restricted to release-branch PRs or behind a label. Accepted tradeoff: higher CI-minutes cost in exchange for catching regressions pre-merge rather than post-merge.
- (c) **Both motivating pains matter equally** — CI-minutes waste and missing pre-merge signal are not ranked against each other. This means filtering aggressiveness (path-scoping) and gate strictness (required checks) are co-equal goals; neither should be softened to protect the other.
- (d) **The user (Patricio/montesgp) owns branch-protection settings.** Changing required checks is assumed as part of this change's delivery path, not deferred to an external operator runbook — the success criteria for the required-check contract are directly actionable by the same person driving this change.

## Capabilities

### New Capabilities

- `ci-pipeline-gating`: which pipeline jobs run for a given change, on which events (`pull_request` vs. `push`), how change paths select the Web and .NET unit groups, how documentation-only changes skip code jobs, and how a legitimately skipped job satisfies a required status check.

### Modified Capabilities

None. `cloud-deployment` (Railway push-to-deploy, `watchPatterns`) and `safe-release-upgrades` (per-channel deploy, signed artifacts, publication authorization) are **referenced and preserved unchanged** — this change selects which CI jobs execute, it does not alter deploy or release requirements.

## Approach

Express job selection as a first-class, written contract before touching YAML. The pipeline already contains the independence signal in prose (`web-tests` "never blocks or is blocked by" the .NET build) — this change turns that comment into an enforced trigger condition.

Order of reasoning: (1) enumerate the two real unit groups and their path surfaces; (2) add `pull_request` so filtering has a consumer that matters; (3) resolve the required-check contract **before** the filters are authored, because the filter mechanism choice (trigger-level `paths:` vs. in-job change detection) is largely *determined* by which required-check behavior is acceptable; (4) keep the push-to-release-branch path conservative — a release push runs what it runs today unless a job is provably irrelevant.

Documentation-only skipping is included because it is cheap, unambiguous, and shares the same mechanism as (3); the two adjacent findings (Railway `watchPatterns`, MSIX signing) are not, and are cited out rather than silently absorbed.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `.github/workflows/release.yml` | Modified | `pull_request` trigger; path filters on the Web and .NET job groups; docs-only skip; always-run gate job per Decision 3 |
| `.github/workflows/*` (additional) | New (possible) | Only if design selects a multi-workflow split as the filter mechanism |
| `.github/release-authorization.yml` | **Unchanged** | Decision 4: publication authorization semantics preserved |
| `openspec/config.yaml` | Modified | Correct stale `testing.ci: {available: false, command: null}` |
| `railway.json` | **Unchanged** | Deploy-trigger filtering (and its missing `src/Commerce.Web/**`) is an explicit non-goal, cited for a separate change |
| `src/Commerce.Web/**`, `src/Commerce.*`, `tests/**` | **Unchanged** | Only referenced as filter path surfaces; no source change |
| `docs/architecture/decisions/ADR-007-*.md` | Referenced | Line 17 deferral implemented, not amended |
| Repository branch-protection settings | Documented | Required-check contract written down; settings change is operator action, not code |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| A path-skipped job leaves a PR permanently "waiting" on a required check | **High** | Decision 3 is locked as a design constraint; an always-run aggregating gate job is required before filters ship |
| A release-branch push skips a job that gated publication, silently weakening the release path | Medium | Decision 4: push-side filtering stays conservative; release/authorization jobs are never filtered |
| Filters drift out of sync with a new project or moved directory, silently under-testing | Medium | Shared-root/fallback globs trigger both groups; a filter-coverage assertion is a success criterion |
| Adding `pull_request` doubles workflow runs (PR + push) and raises minutes cost | Medium | Path filtering is the offset; `web-e2e` trigger scope is an explicit design question |
| Scope creeps into per-project `Commerce.sln` splitting | Medium | Explicit non-goal citing ADR-001 and ADR-007; this change filters group membership only |
| The Railway `watchPatterns` Web gap gets "fixed while we're here", mixing deploy and CI decisions | Medium | Explicitly cited as out of scope with reasoning; a separate change owns it |
| `web-e2e` becomes a flaky PR blocker (live Postgres + Playwright) | Medium | Its PR trigger scope is a deferred design decision, not assumed |
| Docs-only skip accidentally skips a change that does affect the build (e.g. a Dockerfile-adjacent doc path) | Low | Docs globs enumerated conservatively; anything ambiguous triggers both groups |

## Rollback Plan

Revert the commit: `.github/workflows/release.yml` returns to push-only, unfiltered, five-job behavior, and `openspec/config.yaml`'s `testing.ci` block returns to its (stale) prior value. No source, migration, or persisted state is involved — CI configuration is stateless, so rollback is complete and immediate with no data caveat.

**Operator caveat**: if branch-protection required checks were updated to name the new gate job, reverting the workflow leaves protection referencing a check that no longer runs, which blocks PRs. Rollback MUST therefore be paired with restoring the prior required-check list; design must record the exact prior list so this is mechanical.

Narrower rollback: keep the `pull_request` trigger but remove the path filters (everything runs on every PR — costly but safe), or keep filters and remove the `pull_request` trigger.

## Dependencies

- ADR-007 (monorepo retained; independent deployability as a goal; line 17 defers this change) — accepted, implemented here, not amended.
- ADR-001 (single `Commerce.sln` / one `dotnet test` harness) — constrains this change; per-project splitting is out of scope.
- ADR-004 (branch→release channel mapping, consumed by `determine-channel`) — preserved unchanged.
- ADR-005 (signing/distribution) — owns the MSIX/MSI placeholder; out of scope here.
- Existing `.github/workflows/release.yml` and `.github/release-authorization.yml` — the artifacts being modified and preserved respectively.
- **GitHub repository branch-protection settings** — an operator-owned prerequisite for any required-check contract this change defines; code alone cannot enforce it.
- Product questions (a)–(d) answered 2026-09-20 (see Decisions/Proposal question round); specs may proceed.

## Success Criteria

- [ ] A PR containing only `src/Commerce.Web/**` changes runs `web-tests` (and `web-e2e` per the decided policy) and does **not** run the `windows-latest` `build` job.
- [ ] A PR containing only .NET source/test changes runs `build` and does **not** run `web-tests`/`web-e2e`.
- [ ] A PR containing only `docs/**`, `openspec/**`, or root `*.md` changes runs **no** code job.
- [ ] In all three cases above, the PR reaches a mergeable state — every required status check is satisfied, with no check stuck pending because a job was skipped (Decision 3, demonstrated per case).
- [ ] A PR touching both groups runs both groups.
- [ ] A push to `dev`/`staging`/`main` still produces the current channel determination, protected-source check, and publication-authorization behavior, unchanged (Decision 4).
- [ ] The trigger matrix (job × event × path condition) is written down in the change artifacts, not only encoded in YAML.
- [ ] The required-check contract, and the exact prior required-check list needed for rollback, are both recorded.
- [ ] `openspec/config.yaml` `testing.ci` reflects the real pipeline (`available: true` with the workflow reference).
- [ ] `railway.json` is byte-identical after this change (non-goal boundary respected).
- [ ] `dotnet test Commerce.sln` and `dotnet build Commerce.sln` pass.

## Proposal question round

**Round 1 (answered)**: Decision 1 — the `pull_request` trigger ships in this phase, together with path filtering.

**Round 2 (answered 2026-09-20)**: (a) PR checks are required, not advisory; (b) `web-e2e` runs on every Web-touching PR; (c) CI-minutes waste and missing pre-merge signal are co-equal motivations; (d) the user owns branch-protection settings directly.

**Assumptions needing user review** (currently baked into scope, correctable):
1. Documentation-only skipping is in scope because it is cheap and shares Decision 3's mechanism — it could be dropped without harming the rest.
2. Push-side filtering stays conservative; the optimization is PR-side first (Decision 4).
3. The Railway `watchPatterns` Web gap is real but handled by a separate change, not folded in here.
