# CI Pipeline Gating Specification

## Purpose

Define which jobs in the release pipeline (`.github/workflows/release.yml`:
`determine-channel`, `verify-protected-source`, `web-tests`, `web-e2e`,
`build`) execute for a given change, on which trigger event
(`pull_request` vs `push`), how the changed paths select the Web and .NET
unit groups, how a documentation-only change skips all code jobs, and how
a job that is legitimately skipped by a path filter still satisfies a
required status check on the pull request. This capability governs job
*selection* only — it does not change what any job does internally, and it
does not alter release-channel, publication-authorization, or deployment
semantics, which are referenced and preserved unchanged (planning-only,
`approval_scope: planning-only`).

## Requirements

### Requirement: Pull Requests Trigger the Pipeline as a Pre-Merge Gate

The pipeline MUST run on the `pull_request` event, in addition to the
existing `push` event on `dev`/`staging`/`main`. A pull request's checks
MUST be evaluated before merge, not only after merge, so CI is a pre-merge
gate rather than a post-merge report.

#### Scenario: Opening a PR produces CI signal before merge

- GIVEN a pull request is opened against a protected branch
- WHEN the pull request is created or updated
- THEN pipeline jobs run against that pull request and report status on
  the PR itself, before any merge occurs

#### Scenario: No CI signal existed pre-merge before this change

- GIVEN the pipeline as it existed before this change (push-only, no
  `pull_request` trigger)
- WHEN a pull request is opened
- THEN no job runs and no check reports until after merge

  This scenario documents the prior gap this requirement closes; it MUST
  NOT remain true after this capability is implemented.

### Requirement: Web-Touching Changes Trigger the Web Job Group Only

A pull request whose changed paths are confined to `src/Commerce.Web/**`
MUST trigger the Web job group (`web-tests` and `web-e2e`) and MUST NOT
trigger the `.NET` job group (`build`).

#### Scenario: Web-only PR runs Web jobs, not build

- GIVEN a PR touching only paths under `src/Commerce.Web/**`
- WHEN it is opened or updated
- THEN `web-tests` and `web-e2e` run, `build` does not run, and the PR
  reaches a mergeable state once the Web jobs and any always-run gate
  succeed

### Requirement: .NET-Touching Changes Trigger the .NET Job Group Only

A pull request whose changed paths fall within the shared `Commerce.sln`
surface (`src/Commerce.*` excluding `Commerce.Web`, `tests/**`,
`Directory.*.props`, `Commerce.sln`, `global.json`, and equivalents) MUST
trigger the `.NET` job group (`build`) and MUST NOT trigger the Web job
group (`web-tests`, `web-e2e`).

#### Scenario: .NET-only PR runs build, not Web jobs

- GIVEN a PR touching only paths within the shared `Commerce.sln` surface
  (e.g. `src/Commerce.Domain/**`)
- WHEN it is opened or updated
- THEN `build` runs, `web-tests` and `web-e2e` do not run, and the PR
  reaches a mergeable state once `build` and any always-run gate succeed

### Requirement: Changes Touching Both Groups Run Both Groups

A pull request whose changed paths span both `src/Commerce.Web/**` and the
shared `Commerce.sln` surface MUST trigger both the Web job group and the
`.NET` job group. A path outside both defined surfaces (shared-root files
such as `Dockerfile`, `.github/workflows/**`, `deploy/**`, or lockfiles
affecting both toolchains) MUST conservatively trigger both groups rather
than being silently excluded from either.

#### Scenario: Cross-cutting PR runs every code job

- GIVEN a PR touching both `src/Commerce.Web/**` and a path within the
  shared `Commerce.sln` surface
- WHEN it is opened or updated
- THEN `web-tests`, `web-e2e`, and `build` all run

#### Scenario: Shared-root or ambiguous path triggers both groups

- GIVEN a PR touching a shared-root path not clearly owned by either the
  Web or the .NET surface (e.g. a root `Dockerfile` or
  `.github/workflows/**` change)
- WHEN it is opened or updated
- THEN both the Web job group and the `.NET` job group run; the change is
  never treated as build-irrelevant by default

### Requirement: Documentation-Only Changes Skip All Code Jobs

A pull request whose changed paths are confined to `docs/**`,
`openspec/**`, root `*.md` files, or comparable non-build paths MUST NOT
trigger `web-tests`, `web-e2e`, or `build`. The PR MUST still reach a
mergeable state through the required-status-check contract defined below.

#### Scenario: Docs-only PR runs no code job and still becomes mergeable

- GIVEN a PR touching only `docs/**`, `openspec/**`, or root `*.md` paths
- WHEN it is opened or updated
- THEN `web-tests`, `web-e2e`, and `build` do not run, and the PR reaches a
  mergeable state with no required check left stuck pending

### Requirement: A Legitimately Skipped Job Satisfies Its Required Status Check

Every job named in branch-protection required checks MUST resolve to a
non-pending status for every pull request, whether that job ran or was
skipped by a path filter. A path-filtered pull request MUST NOT be left
permanently "waiting" on a required check whose underlying job was
legitimately skipped for that change. The pipeline MUST expose an
always-evaluated gate (aggregator) whose result reflects the actual
outcome across the filtered jobs — success when every triggered job
succeeded and every skipped job was legitimately skipped, failure when any
triggered job failed.

#### Scenario: Skipped job does not block merge

- GIVEN a PR for which `build` was legitimately skipped because no path in
  the `.NET` surface changed
- WHEN branch-protection required checks are evaluated for that PR
- THEN no required check remains pending indefinitely because of the
  skipped `build` job, and the PR is mergeable once the jobs that did run,
  plus the always-evaluated gate, succeed

#### Scenario: A genuine job failure still blocks merge

- GIVEN a PR for which `web-tests` ran (because it was triggered by the
  path filter) and failed
- WHEN branch-protection required checks are evaluated for that PR
- THEN the PR is not mergeable, and the always-evaluated gate reports
  failure rather than masking the underlying failure as a skip

### Requirement: Required Checks Are Blocking, Not Advisory

Every check named in the required-status-check contract MUST be
enforced as a blocking gate on merge, not surfaced as advisory-only
status. A pull request MUST NOT be mergeable while any required check is
failing, pending, or unresolved.

#### Scenario: A failing required check blocks the merge button

- GIVEN a PR with a required check in a failing state
- WHEN a user attempts to merge that PR
- THEN the merge is blocked until the failing required check either
  passes or is legitimately superseded by a rerun that passes

### Requirement: web-e2e Runs on Every Web-Touching Pull Request

`web-e2e` MUST run on every pull request whose changed paths trigger the
Web job group (i.e. every PR that also triggers `web-tests` under the Web
path-group requirement). `web-e2e` MUST NOT be restricted to
release-branch pull requests, gated behind a label, or otherwise made
conditional beyond the same path trigger that runs `web-tests`.

#### Scenario: Every Web-touching PR runs web-e2e, not only release-targeted ones

- GIVEN a PR touching `src/Commerce.Web/**` and targeting any branch
- WHEN the PR is opened or updated
- THEN `web-e2e` runs unconditionally alongside `web-tests`, with no label
  or target-branch precondition required to trigger it

### Requirement: Release and Publication-Authorization Semantics Are Unchanged on Push

On a `push` event to `dev`, `staging`, or `main`, `determine-channel`,
`verify-protected-source`, and the `build` job's publication-authorization
gate (reading `.github/release-authorization.yml`) MUST run and MUST
behave exactly as they did before this change, regardless of which paths
changed in that push. Path filtering introduced by this capability applies
to the `pull_request` event; it MUST NOT cause a `push` to a release
branch to skip `determine-channel`, `verify-protected-source`, or the
publication-authorization gate.

#### Scenario: Push to a release branch runs the full release sequence regardless of paths

- GIVEN a push lands on `dev`, `staging`, or `main` touching only
  `src/Commerce.Web/**`, only the .NET surface, or only documentation
  paths
- WHEN the pipeline runs for that push
- THEN `determine-channel`, `verify-protected-source`, and `build`
  (including its publication-authorization gate) all run exactly as they
  did before path filtering was introduced

#### Scenario: Publication authorization gate is never bypassed by filtering

- GIVEN a push to a release branch for a channel whose
  `publication_authorized` flag in `.github/release-authorization.yml` is
  `false`
- WHEN the pipeline runs for that push
- THEN the publication gate still fails the run exactly as before,
  independent of which paths changed
