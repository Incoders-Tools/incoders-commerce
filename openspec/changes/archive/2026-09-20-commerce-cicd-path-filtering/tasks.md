# Tasks: Commerce Per-App Path-Filtered CI/CD (Phase G)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~230 (design.md estimate: ~90 `release.yml`, ~70 scripts, ~60 tests, ~2 config.yaml) |
| 400-line budget risk | Low |
| Chained PRs recommended | No |
| Suggested split | Single PR |
| Delivery strategy | ask-on-risk |
| Chain strategy | pending (no decision needed — under budget) |

Decision needed before apply: No
Chained PRs recommended: No
Chain strategy: pending
400-line budget risk: Low

Scope is YAML + two hand-rolled `bash` scripts + one xUnit structural test + a
2-line config edit — one deliverable unit, no cross-cutting integration
points, no migrations. Design's own forecast (Low/No) is honored, not
overridden.

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Path-filtered pre-merge gate: classifier + aggregator scripts, `changes`/`ci-gate` jobs, `pull_request` trigger, structural test | PR 1 | `dotnet test Commerce.sln --filter FullyQualifiedName~CiPipelineGating` + `.github/scripts/tests/*.sh` | 4 evidence PRs (Web-only, .NET-only, docs-only, both) into `dev`, post-merge | Revert `release.yml`/`config.yaml`; un-require `ci-gate` first if branch protection was already patched |

## Phase 1: RED — Fixture-Driven Script Tests

- [x] 1.1 RED: `.github/scripts/tests/classify-changes.test.sh` — fixtures
      for STEP 1-6 against not-yet-existing `.github/scripts/classify-changes.sh`:
      Web-only, .NET-only, docs-only, both, `Dockerfile` (shared-root),
      `deploy/db/migrations/0012_x.sql` (shared-root), unknown root path
      `newdir/x.txt` (drift ⇒ both), `README.md` alone (docs-only),
      `README.md + src/Commerce.Domain/X.cs` (dotnet only), API-failure input
      (⇒ both `true`), empty file list (⇒ both `true`).
      (Spec: Web-Touching/`.NET`-Touching/Both-Groups/Documentation-Only
      Requirements; Design Threat Matrix "Documentation-like paths" and "PR
      commands" rows)
- [x] 1.2 RED: `.github/scripts/tests/aggregate-results.test.sh` — fixtures
      against not-yet-existing `.github/scripts/aggregate-results.sh`:
      `changes=skipped`⇒fail, `changes=failure`⇒fail, `web-tests=skipped,
      build=success`⇒pass, all three `skipped`⇒pass, any `failure`⇒fail, any
      `cancelled`⇒fail.
      (Spec: Legitimately-Skipped-Job-Satisfies-Required-Check Requirement,
      both scenarios; Design "Keeping `build` as one job" / ci-gate step)

## Phase 2: GREEN — Classifier and Aggregator Scripts

- [x] 2.1 GREEN: `.github/scripts/classify-changes.sh` create — implement
      STEP 1-6 glob classification exactly per design's Path Glob Groups;
      include the 3000-file truncation guard (compare `gh api` returned
      filename count against `github.event.pull_request.changed_files`,
      force `web=true dotnet=true` on mismatch). Run 1.1 green.
      (Design Open Questions — "3000-file cap...recommended for sdd-tasks to
      include as a cheap guard"; not scope creep beyond that recommendation)
- [x] 2.2 GREEN: `.github/scripts/aggregate-results.sh` create — implement
      the `ci-gate` decision matrix (changes must be exactly `success`;
      `success`/`skipped` pass, else fail) per design's `ci-gate` step. Run
      1.2 green.

## Phase 3: Workflow Wiring

- [x] 3.1 Modify `.github/workflows/release.yml` — add `pull_request:
      branches: [dev, staging, main]` trigger (push trigger unchanged); add
      `pull-requests: read` permission.
      (Spec: Pull-Requests-Trigger-Pipeline Requirement; Design "`pull_request`
      scope" decision)
- [x] 3.2 Modify `release.yml` — add `changes` job (`ubuntu-latest`) that
      calls `.github/scripts/classify-changes.sh`, sets `web`/`dotnet`
      outputs, and short-circuits to `web=true dotnet=true` when
      `GITHUB_EVENT_NAME != pull_request`.
      (Spec: Web/.NET/Both/Documentation-Only Requirements; Design "Filter
      mechanism" and "Push-side behaviour" decisions)
- [x] 3.3 Modify `release.yml` — add `if: github.event_name == 'push'` to
      `determine-channel` and `verify-protected-source`.
      (Design Verified 1; "Keeping `build` as one job" decision)
- [x] 3.4 Modify `release.yml` — add `if: needs.changes.outputs.web ==
      'true'` to `web-tests`/`web-e2e`; add `if: always() &&
      needs.changes.outputs.dotnet == 'true' &&
      needs.determine-channel.result != 'failure' &&
      needs.verify-protected-source.result != 'failure'` to `build`; scope
      the packaging placeholder step and the publication-authorization gate
      step inside `build` to `if: github.event_name == 'push'`.
      (Spec: web-e2e-Runs-on-Every-Web-Touching-PR, Release-Semantics-Unchanged
      Requirements; Design Decision 4 byte-equivalence)
- [x] 3.5 Modify `release.yml` — add `ci-gate` job, `needs: [changes,
      web-tests, web-e2e, build]`, `if: always()`, calling
      `.github/scripts/aggregate-results.sh` against `toJSON(needs)`; no
      other `if:`/`paths:` predicate.
      (Spec: Legitimately-Skipped-Job-Satisfies-Required-Check,
      Required-Checks-Blocking-Not-Advisory Requirements)

## Phase 4: Structural Regression Test

- [x] 4.1 RED: xUnit test in `tests/Commerce.Integration` asserting
      `release.yml` has no `paths:`/`paths-ignore:` key, the `ci-gate` job
      has no `if:` predicate beyond `always()`, and `railway.json` /
      `.github/release-authorization.yml` are byte-identical to their
      pre-change contents. Fails before Phase 3 (no `ci-gate` job exists
      yet).
      (Design Testing Strategy "Unit (structural)" row; Success Criteria
      "railway.json byte-identical")
- [x] 4.2 GREEN: confirmed by Phase 3 wiring — no additional production
      edit; re-run the test to confirm pass.

## Phase 5: Bookkeeping and Runbook

- [x] 5.1 Modify `openspec/config.yaml` — `testing.ci: {available: true,
      command: ".github/workflows/release.yml"}`.
      (Proposal Decision 5)
- [x] 5.2 Create `docs/operations/ci-required-status-checks-runbook.md` (or
      equivalent) documenting, verbatim from design.md: the `gh api`
      command to record each of `dev`/`staging`/`main`'s prior
      `{strict, checks}` (404 = inconclusive, record raw — never "none");
      the PATCH to `{"strict": false, "checks":[{"context":"ci-gate"}]}`,
      run only after `release.yml` merges; and the rollback ordering
      (un-require `ci-gate` before reverting the workflow). This is a
      documented step for the repo admin to execute manually — no `gh api`
      PATCH is run by any task here.
      (Design "Required-Status-Check Contract"; Proposal Decision 3/answered
      question (d); Rollback Plan operator caveat)
- [x] 5.3 Record, unmodified, the two accepted residual risks from
      design.md's Open Questions: (a) a .NET-only PR gets no pre-merge
      `web-e2e` signal — accepted per the proposal's own success criterion,
      not closed here; (b) the `pulls/{n}/files` 3000-file cap — mitigated
      by the 2.1 guard, not eliminated. Neither gets additional scope
      beyond what design.md states. No PR was opened for this change
      (apply-only instruction); recorded instead in
      `docs/operations/ci-required-status-checks-runbook.md`'s "Accepted
      residual risks" section, to be copied into the PR description when
      this change is actually delivered.

## Phase 6: Verification

- [x] 6.1 `dotnet test Commerce.sln` full pass, including the Phase 4
      structural test and any classifier/aggregator fixture harness wired
      into the build. Result: 607/608 passed in `Commerce.Integration`
      (plus 1/1 `Commerce.Bootstrap.Tests`, 19/19 `Commerce.Upgrade`),
      1 pre-existing unrelated failure:
      `PublicRateLimitTests.WithGuestOrderingConfigAbsent_EveryPublicRoute_IsUnreachable_AndAppStillStarts`
      (guest-ordering rate-limit config test — unrelated to this change,
      matches the known pre-existing baseline failure). All 4
      `CiPipelineGatingStructuralTests` pass; the shell fixture harnesses
      (`.github/scripts/tests/*.test.sh`) pass standalone (12/12 and 6/6).
- [x] 6.2 `dotnet build Commerce.sln` clean — 0 errors, 24 pre-existing
      NU1903 advisory warnings (unrelated to this change).
- [x] 6.3 `git diff --stat -- railway.json .github/release-authorization.yml`
      confirms zero changes (non-goal boundary respected).
- [x] 6.4 Confirmed the Threat Matrix rows marked N/A in design.md (Git
      repository selection, Commit state, Push state) remain untouched — no
      `git`, staging, commit, or push operation is introduced by this
      change; `classify-changes.sh`/`aggregate-results.sh` only call `gh
      api`/`jq` and read env/stdin.
- [x] 6.5 Post-merge manual evidence, confirmed via real GitHub Actions
      runs (not simulated): the `pull_request`-triggered run for PR #51
      ("fix(ci): seed guest-ordering org/branch and fix stale catalog E2E
      test", mixed Web+.NET+workflow diff) shows `changes`, `web-tests`,
      `web-e2e`, `build`, and `ci-gate` all `success`
      (run 35524616527) — the first fully green run, after an earlier
      attempt on the same PR (run 35523772248) failed on `changes`/
      `ci-gate` due to a since-fixed executable-bit bug. The subsequent
      `push`-triggered run on `dev` (run 35525889490) shows all real
      tests green (`Run full test suite` step: success) with only the
      intentional, by-design `Publication gate (publication_authorized)`
      step failing — expected per ADR-004, does not affect `ci-gate`
      required-check semantics for PR merges. Branch protection requiring
      `ci-gate` has since been configured on `dev`/`staging`/`main` via
      the GitHub dashboard by the repo owner (required-approvals left at
      0, since there is currently one active developer). The originally
      planned 4 synthetic evidence PRs (Web-only/.NET-only/docs-only/
      both) were superseded by this real, higher-fidelity evidence from
      actual working PRs during the same session.
