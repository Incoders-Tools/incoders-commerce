# Design: Commerce Per-App Path-Filtered CI/CD (Phase G)

## Technical Approach

The proposal's five locked decisions are not re-opened. This design resolves its
six deferrals and records the **verified facts in the real workflow** that force
each answer.

**Verified 1 — `determine-channel` cannot survive a `pull_request` event.**
`release.yml:27-35` switches on `GITHUB_REF_NAME` and `exit 1`s on the `*)` arm.
On a `pull_request` event that value is `<n>/merge`, so the job would fail every
PR. It must therefore be **push-only**, which in turn forces a solution for
`build` (`:187`) whose `needs: [determine-channel, verify-protected-source]`
would otherwise skip `build` on every PR.

**Verified 2 — `web-e2e` is not a pure Web job.** It runs
`psql ... -f deploy/dev/db/init-rls.sql` (`:134`) and
`dotnet run --project src/Commerce.Cloud.Api` (`:154`). Its dependency surface
already crosses into `deploy/**` and the .NET tree, which is why `deploy/**` and
`Commerce.sln` land in the **shared-root** set rather than in the .NET set.

**Verified 3 — the repo's house style is hand-written `bash`, not marketplace
actions.** The publication gate hand-rolls an `awk` YAML lookup (`:237-245`)
rather than adding a YAML-parsing action, and `verify-protected-source` calls
`gh api` directly (`:51`). Change detection follows the same idiom.

**Verified 4 — "inconclusive is never a green light" is an existing repo
principle.** `release.yml:41-44` and `:58-59` refuse to read a 404 as proof of
absence. The gate job below applies the same rule: an *unknown* job result is a
failure, only a *legitimately filtered* one is a pass.

**Verified 5 — `Commerce.Web` is outside `Commerce.sln`.** The solution
(`Commerce.sln:8-26`) lists ten projects, none of them `Commerce.Web`. The two
groups of proposal Decision 2 are therefore a real structural boundary, not a
convention.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternatives |
|---|---|---|
| **Filter mechanism** (deferral 1) | **One workflow, one always-run `changes` detection job, job-level `if:` conditions.** `release.yml` keeps a single `on:` block with **no `paths:`/`paths-ignore:` anywhere**. A first job `changes` computes the PR's changed-file set and emits `web` / `dotnet` booleans; `web-tests`, `web-e2e` and `build` gain `if: needs.changes.outputs.<group> == 'true'`. This is the only mechanism under which a filtered-out job still **reports a `skipped` conclusion to `needs`**, which is the raw material the gate job needs. | **Workflow-level `paths:`** — a path-filtered *workflow* never starts, so its check runs are never created and a required check stays **Pending forever**; the gate job would be inside the same skipped workflow and would not run either. This is precisely proposal Decision 3's footgun, and it is unfixable from inside the workflow. **Splitting into multiple workflows** — reproduces the same Pending failure per workflow, duplicates `determine-channel`/`verify-protected-source`/the publication gate across files (Decision 4 risk), and fragments the gate into several required names each individually subject to the footgun. **`dorny/paths-filter@v3`** — correct behaviour, but adds a third-party action to a repo that deliberately hand-rolls `awk`/`gh api` (Verified 3), and its `push`-event `before..after` comparison is unreliable on force-push/first-push — a reliability problem this design avoids entirely by not filtering on push at all. |
| **Detection source** | **`gh api repos/${GITHUB_REPOSITORY}/pulls/${PR}/files --paginate --jq '.[].filename'`**, matching the existing `gh api` idiom. No `fetch-depth: 0` checkout, no merge-base arithmetic. **If the call fails, is empty, or returns non-zero, both groups are set `true`** — inconclusive means *run everything* (Verified 4). | **`git diff --name-only`** — needs a full-history checkout on every PR and a correct `merge-base` three-dot form; more moving parts for the same answer. |
| **Push-side behaviour** (deferral 5) | **Filtering is `pull_request`-only.** The `changes` job short-circuits: `if [ "${GITHUB_EVENT_NAME}" != "pull_request" ]; then web=true; dotnet=true; fi`. A push to `dev`/`staging`/`main` therefore runs **all five existing jobs exactly as today** — `determine-channel`, `verify-protected-source`, `web-tests`, `web-e2e`, `build` including the publication gate. No release-path job can ever observe a filtered-away dependency. | **Symmetric filtering on push** — directly violates proposal Decision 4 ("when in doubt on a push, run the job") and would put the `.github/release-authorization.yml` gate behind a path condition. |
| **`pull_request` scope** (deferral 5) | **`branches: [dev, staging, main]`** (base-branch filter, default `types`). ADR-004 names exactly these three as the authorized/protected refs; branch protection and therefore required checks exist only on them, so a PR into any other base has nothing to satisfy. This also stops stacked/chained SDD PRs (child PR → previous feature branch) from paying full CI twice with no gate to satisfy. | **All base branches** — doubles CI-minutes on feature-to-feature PRs for zero gating value, working against motivating pain (c). **Head-branch filtering** — `pull_request` `branches:` filters the *base*; head filtering is not expressible and would be a naming convention, not a rule. |
| **Keeping `build` as one job** (deferral 5 corollary) | **One `build` job for both events.** `needs: [changes, determine-channel, verify-protected-source]` with `if: always() && needs.changes.outputs.dotnet == 'true' && needs.determine-channel.result != 'failure' && needs.verify-protected-source.result != 'failure'`. `always()` is what lets `build` run on a PR where the two push-only jobs are `skipped`. Restore/build/test run on both events; the **packaging placeholder and the publication gate step each carry `if: github.event_name == 'push'`**, so `.github/release-authorization.yml` semantics stay byte-equivalent and are never evaluated with an empty `RELEASE_CHANNEL`. | **A separate `build-pr` job** — duplicates a 25-line `windows-latest` job, creates a second check name, and doubles the drift surface. **Running `determine-channel` on PRs** — Verified 1: it fails by construction on `<n>/merge`. |
| **`web-e2e` on PRs** (deferral 3) | **Runs on every Web-touching PR**, per answered question (b) — same condition as `web-tests` (`needs.changes.outputs.web == 'true'`), no label gate, no release-branch restriction. | **Label/`workflow_dispatch` gating** — explicitly rejected by (b); also re-introduces "no pre-merge signal" for the exact regressions E2E exists to catch. |
| **Docs-only detection** (deferral 6) | **Derived, not a trigger filter.** Doc paths are *subtracted* from the changed set first; if nothing remains, both groups are `false` and all three code jobs skip. Docs-only is thus a computed state of the same `changes` job — it reaches the gate as `skipped`, which the gate passes. | **`paths-ignore:` on the trigger** — same Pending-forever failure as any trigger-level filter, and it is exactly the case proposal Decision 3 says must not differ in required-check behaviour. |
| **Drift fail-safe** | **Any changed path matching no group is treated as matching BOTH.** New top-level directories and moved projects therefore over-test rather than silently under-test (proposal's Medium drift risk). | **A closed docs whitelist as the default bucket** — an unrecognised path would skip all code jobs, which is the under-testing failure mode. |

## Path Glob Groups

Evaluated in order. Matching is on POSIX paths as returned by the PR files API.

```text
STEP 1 — subtract documentation-only paths (never select a group on their own)
  **/*.md                     docs/**              openspec/**
  LICENSE                     .github/ISSUE_TEMPLATE/**
  .github/PULL_REQUEST_TEMPLATE.md

STEP 2 — shared root: any match sets BOTH web=true AND dotnet=true
  Dockerfile                  .github/workflows/**       deploy/**
  Commerce.sln                railway.json               .editorconfig
  global.json                 Directory.*.props          Directory.*.targets
  nuget.config                NuGet.config
    (the last six do not exist today; pre-covering them means adding one is
     never invisible to CI)

STEP 3 — Web group  -> web=true
  src/Commerce.Web/**         (includes package.json, package-lock.json,
                               vite/vitest/playwright configs, .oxlintrc.json)

STEP 4 — .NET group -> dotnet=true
  src/**  AND NOT src/Commerce.Web/**     tests/**

STEP 5 — anything still unmatched -> web=true AND dotnet=true   (drift fail-safe)

STEP 6 — if after STEP 1 the remaining set is empty -> docs-only:
         web=false, dotnet=false; ci-gate still runs and still passes.
```

Rationale for the non-obvious placements:

- **`deploy/**` is shared, not .NET.** `web-e2e` applies `deploy/dev/db/init-rls.sql` (`release.yml:134`); a migration edit can break E2E without touching Web.
- **`Commerce.sln` is shared, not .NET-only**, despite `Commerce.Web` being outside it: `web-e2e` boots `src/Commerce.Cloud.Api` through the solution graph (`:154`), so a project added or removed in the solution can break the E2E backend.
- **`railway.json` is shared and read-only here.** It is a *filter input*, never modified (proposal non-goal; success criterion "byte-identical").
- **`.github/workflows/**` is shared** — a change to the pipeline must be validated by the whole pipeline.

## Data Flow

```text
pull_request (base in dev|staging|main)
  changes  [ALWAYS RUNS, ubuntu, no filter]
    gh api pulls/<n>/files --paginate  --(failure/empty)-->  web=true dotnet=true
    STEP1..STEP6  -> outputs.web, outputs.dotnet
      |                |                        |
      v                v                        v
   web-tests        web-e2e                  build
   if web==true     if web==true              if always() && dotnet==true
                                              && channel/protection != failure
                                              (packaging + publication gate
                                               steps: if event_name == 'push')
   determine-channel / verify-protected-source: if event_name == 'push'
                                                -> skipped on every PR
      \              |              |          /
       ------------> ci-gate  [ALWAYS RUNS, if: always()] <------
                     changes  must be exactly success
                     others   success | skipped -> pass
                              failure | cancelled | unknown -> FAIL
                     ci-gate is the ONLY required status check.

push to dev|staging|main
  changes -> event_name != pull_request -> web=true dotnet=true (no API call)
  => all five existing jobs run, in today's order, with today's steps.
     determine-channel -> verify-protected-source -> build -> publication gate
     Behaviour on the release path is unchanged.  [proposal Decision 4]
```

## Interfaces / Contracts (workflow YAML shape)

```yaml
on:
  push:
    branches: [dev, staging, main]     # UNCHANGED
  pull_request:
    branches: [dev, staging, main]     # NEW; default types

permissions:
  contents: read
  pull-requests: read                  # NEW: required by `gh api pulls/*/files`
```

```yaml
  changes:
    runs-on: ubuntu-latest
    outputs:
      web: ${{ steps.detect.outputs.web }}
      dotnet: ${{ steps.detect.outputs.dotnet }}
    steps:
      - id: detect
        shell: bash
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          PR_NUMBER: ${{ github.event.pull_request.number }}
        run: |
          set -uo pipefail
          # Conservative default; a non-PR event never consults the API.
          if [ "${GITHUB_EVENT_NAME}" != "pull_request" ]; then
            echo "web=true"    >> "$GITHUB_OUTPUT"
            echo "dotnet=true" >> "$GITHUB_OUTPUT"; exit 0
          fi
          files=$(gh api "repos/${GITHUB_REPOSITORY}/pulls/${PR_NUMBER}/files" \
                    --paginate --jq '.[].filename') || files=""
          if [ -z "$files" ]; then
            echo "::warning::Changed-file set is INCONCLUSIVE. Running both groups."
            echo "web=true" >> "$GITHUB_OUTPUT"; echo "dotnet=true" >> "$GITHUB_OUTPUT"
            exit 0
          fi
          # STEP 1..6 classification over "$files" -> $web / $dotnet
```

```yaml
  ci-gate:
    # NO path condition and NO event condition may ever be added to this job:
    # it is the single required status check, so it must be queued on every
    # run or a PR hangs Pending forever (proposal Decision 3).
    needs: [changes, web-tests, web-e2e, build]
    if: always()
    runs-on: ubuntu-latest
    steps:
      - shell: bash
        env:
          NEEDS: ${{ toJSON(needs) }}
        run: |
          set -euo pipefail
          # The detector itself must be conclusive: skipped/failed detection
          # means the filter decision is UNKNOWN, and unknown is never green.
          detector=$(jq -r '.changes.result' <<<"$NEEDS")
          [ "$detector" = "success" ] || {
            echo "::error::changes -> ${detector}; filter decision unknown."; exit 1; }
          rc=0
          for job in web-tests web-e2e build; do
            r=$(jq -r --arg j "$job" '.[$j].result' <<<"$NEEDS")
            case "$r" in
              success) echo "PASS    ${job}" ;;
              skipped) echo "FILTERED ${job} (legitimately not selected)" ;;
              *) echo "::error::${job} -> ${r}"; rc=1 ;;   # failure|cancelled|null
            esac
          done
          exit "$rc"
```

**Why this closes the footgun, concretely.** (1) `ci-gate` carries no trigger
filter and no `if:` predicate other than `always()`, so GitHub *always* creates
its check run for every PR — the Pending-forever state is structurally
unreachable. (2) `if: always()` is load-bearing: without it, a `skipped` or
failed `needs` entry would skip `ci-gate` itself and re-create the footgun. (3)
Filtered jobs report `skipped`, which the gate maps to pass — but only for the
three work jobs; `changes` must be a hard `success`. (4) `cancelled` and `null`
map to **fail**, so a cancelled or never-created dependency cannot slip a green
gate. (5) Branch protection names **exactly one** check, `ci-gate`, so no
path-filtered job name is ever required and filtering can never block a merge.

## Required-Status-Check Contract

**The one required check is `ci-gate`**, on `dev`, `staging` and `main`.
`changes`, `web-tests`, `web-e2e`, `build`, `determine-channel` and
`verify-protected-source` **must not** be named required.

Runbook for the repo admin (answered question (d)); `<owner>/<repo>` =
`Patricio-Montes/incoders-commerce`:

```bash
# 1. RECORD THE PRIOR LIST FIRST (rollback depends on it). Per release.yml:41-44,
#    a 404 is INCONCLUSIVE — record the raw status, never record "none".
for b in dev staging main; do
  gh api "repos/<owner>/<repo>/branches/$b/protection/required_status_checks" \
    --jq '{strict, checks}' || echo "$b: endpoint inconclusive"
done

# 2. AFTER the workflow change is merged into the base branch (never before, or
#    ci-gate does not exist yet and every PR hangs):
for b in dev staging main; do
  gh api --method PATCH \
    "repos/<owner>/<repo>/branches/$b/protection/required_status_checks" \
    --input - <<'JSON'
{ "strict": false, "checks": [ { "context": "ci-gate" } ] }
JSON
done
```

`strict: false` is deliberate: `strict: true` forces every PR to re-run the full
gate after each base-branch push, which contradicts motivating pain (c). If step
1 shows `strict: true` today, preserve that value instead.

**Rollback ordering (inverts step order):** remove `ci-gate` from required checks
**before** reverting `release.yml`, then restore the exact list captured in step
1. Reverting the workflow first leaves protection referencing a check that no
longer runs, blocking all PRs — the operator caveat named in the proposal.

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `.github/workflows/release.yml` | **Modify** | Add `pull_request: branches: [dev, staging, main]`; add `pull-requests: read`; add `changes` job; add `if: github.event_name == 'push'` to `determine-channel` and `verify-protected-source`; add `if:` filters to `web-tests`/`web-e2e`/`build`; scope the packaging and publication-gate **steps** to `push`; add `ci-gate`. **No step's `run:` body is otherwise edited.** |
| `openspec/config.yaml` | **Modify** | `testing.ci: {available: true, command: ".github/workflows/release.yml"}` (proposal Decision 5, bookkeeping). |
| `.github/release-authorization.yml` | **Unchanged** | Decision 4. A structural test asserts byte-identity. |
| `railway.json` | **Unchanged** | Non-goal boundary; success criterion asserts byte-identity. |
| `src/**`, `tests/**`, `Commerce.sln`, `Dockerfile` | **Unchanged** | Referenced only as filter surfaces. |
| `docs/architecture/decisions/ADR-007-*.md` | **Referenced** | Line 17 deferral implemented, not amended. |

## Testing Strategy

No .NET or Vitest production code changes, so the test surface is the workflow
contract itself. `dotnet test Commerce.sln` and `dotnet build Commerce.sln` must
stay green (unchanged inputs) as a regression floor.

| Layer | What to test | Approach |
|---|---|---|
| Unit (classification) | STEP 1–6 over fixture path lists: Web-only, .NET-only, docs-only, both, `Dockerfile`, `deploy/db/migrations/0012.sql`, an unknown root path (`newdir/x.txt` ⇒ both) | Extract the classifier into `.github/scripts/classify-changes.sh` and drive it with a `bats`-style or plain-`bash` fixture runner invoked from a repo test script; the `changes` job calls the same script, so CI and the test exercise one implementation |
| Unit (structural) | `release.yml` contains **no** `paths:`/`paths-ignore:` key; `ci-gate` has no `if:` other than `always()`; `determine-channel`'s `run:` body and the publication-gate `run:` body are byte-identical to the pre-change file; `railway.json` and `.github/release-authorization.yml` unchanged | xUnit test in `tests/Commerce.Integration` parsing the YAML/text, so the guarantee is enforced by `dotnet test`, the project's real harness |
| Unit (gate logic) | The aggregation matrix: `changes=skipped ⇒ fail`; `changes=failure ⇒ fail`; `web-tests=skipped, build=success ⇒ pass`; all three `skipped` ⇒ pass; any `failure` ⇒ fail; any `cancelled` ⇒ fail | Same `bash` fixture runner against an extracted `.github/scripts/aggregate-results.sh` fed synthetic `needs` JSON |
| Integration (live, manual-evidence) | The four proposal success-criteria PRs (Web-only, .NET-only, docs-only, both) each reach a mergeable state with `ci-gate` green | Four throwaway PRs into `dev`; record run URLs and the `skipped`/`success` matrix as delivery evidence |
| Integration (push) | A push to `dev` still runs all five jobs and still evaluates the publication gate for channel `internal` | Compare the post-change `dev` run's job list and gate log line against the last pre-change run |

## Threat Matrix

| Boundary | Applicability | Design response | Planned RED tests |
|---|---|---|---|
| Documentation-like paths | **Applicable** — STEP 1 classifies paths as "documentation", and a misclassification skips real build jobs. `**/*.md` is subtracted *before* group matching, but executable-ish doc neighbours are **not** in the doc list: `Dockerfile`, `deploy/**`, `*.props`, `*.sln`, `package.json`, lockfiles, `.github/workflows/**` all select a group. `src/Commerce.Web/README.md` is subtracted by STEP 1 and, if it is the only change, correctly yields docs-only. | Doc subtraction is an enumerated allowlist, never a fallback bucket; STEP 5 sends anything unrecognised to **both** groups. | Fixtures: `Dockerfile` ⇒ both; `deploy/db/migrations/0012_x.sql` ⇒ both; `README.md` alone ⇒ docs-only; `README.md + src/Commerce.Domain/X.cs` ⇒ dotnet; `newdir/build.sh` ⇒ both. |
| Git repository selection | **N/A** — no `git` invocation is added. Detection uses `gh api`; `actions/checkout@v4` usage is unchanged and implicitly repo-scoped. |  |  |
| Commit state | **N/A** — nothing in this change stages, commits, or inspects an index/worktree. |  |  |
| Push state | **N/A** — no job pushes a ref. `permissions: contents: read` is unchanged and grants no write. |  |  |
| PR commands | **Applicable** — a new `gh api repos/${GITHUB_REPOSITORY}/pulls/${PR_NUMBER}/files` call. `GITHUB_REPOSITORY` and the PR number come from the event context, never from PR-author-controlled text; both are passed via `env:`, never interpolated into the `run:` body as `${{ }}` (script-injection boundary). Token is `secrets.GITHUB_TOKEN` with `pull-requests: read` only. Failure is **fail-safe**: both groups `true`. | No composed shell from PR titles/branch names; no `--head`; no third-party action receives the token. | Fixtures: API failure ⇒ both groups `true` and a `::warning::`; empty file list ⇒ both `true`; a branch/title containing `$(...)`/backticks changes no classification (values never reach a shell as code). |

Note: a `pull_request` (not `pull_request_target`) trigger is deliberate — fork
PRs run with a read-only token and no secret access, so the detection step
degrades to the fail-safe "run both groups" path rather than leaking anything.

## Migration / Rollout

No data migration. CI configuration is stateless; the only ordered, non-code
step is branch protection.

1. Merge the `release.yml` + `openspec/config.yaml` change to `dev` (at this
   point `ci-gate` runs but is not required — PRs are unblocked either way).
2. Confirm one push run on `dev` still shows all five original jobs and the
   publication-gate log line for channel `internal`.
3. Open the four evidence PRs; confirm `ci-gate` green in all four.
4. Only then apply the `required_status_checks` PATCH above to `dev`, then
   `staging`, then `main`.
5. Promote `dev → staging → main` by the project's existing branch flow.

**Rollback**: remove `ci-gate` from required checks first, restore the recorded
prior list, then revert the commit. Narrower rollbacks remain available: drop
the three `if:` filters (everything runs on every PR — costly, safe) while
keeping `pull_request` and `ci-gate`; or drop `pull_request` and keep the push
pipeline, in which case `ci-gate` must also be un-required.

## Review Workload Forecast

Decision needed before apply: No
Chained PRs recommended: No
400-line budget risk: Low

Estimated ~230 authored lines: ~90 in `release.yml` (the `changes` and `ci-gate`
jobs plus eight `if:` lines), ~70 across the two extracted `.github/scripts/*.sh`
classifiers, ~60 of fixture/structural tests, and a 2-line `config.yaml` edit.
One deliverable unit, one PR, mechanical rollback.

## Open Questions

- [ ] Non-blocking, for `sdd-apply`: **a .NET-only PR loses pre-merge E2E
      signal.** `web-e2e` boots the real `Commerce.Cloud.Api` (`:154`), so a
      Cloud.Api contract break is exactly what it would catch — yet the
      proposal's success criterion explicitly requires a .NET-only PR **not** to
      run `web-e2e`, and that criterion is followed here. Residual coverage: the
      push to `dev` after merge still runs `web-e2e` (Decision 4), so the signal
      is delayed, not lost. Adding `src/Commerce.Cloud.Api/**` to the `web-e2e`
      condition would close it at the cost of that criterion; it is a one-line
      follow-up, not a silent reinterpretation.
- [ ] Non-blocking: the `pulls/{n}/files` endpoint caps at 3000 files. A PR
      above that cap returns a truncated list; the classifier cannot detect
      truncation from the `--jq` output alone. Current mitigation is the drift
      fail-safe (unknown ⇒ both groups), which does not fire on *omission*. A
      belt-and-braces option is to compare the list length against
      `github.event.pull_request.changed_files` and force both groups on
      mismatch — recommended for `sdd-tasks` to include as a cheap guard.
- [ ] Non-blocking: GitHub treats a job-level `skipped` conclusion as passing a
      required check, so naming `web-tests`/`build` required would *appear* to
      work. It is excluded anyway, because that behaviour makes an
      over-aggressive filter indistinguishable from a real pass — `ci-gate`'s
      explicit `changes must be success` assertion is the discriminator.
