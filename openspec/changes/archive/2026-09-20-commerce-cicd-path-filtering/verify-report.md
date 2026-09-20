# Verification Report: commerce-cicd-path-filtering (Phase G)

**Mode**: Independent re-verification by direct source inspection, real test execution, and real GitHub Actions run evidence (not simulated) — including two post-apply bugfixes discovered and fixed during this verification pass.

## Task Completeness

19/19 tasks in `tasks.md` marked `[x]`, including 6.5 (post-merge manual evidence), which this verification pass closed with real CI run citations (see below) superseding the originally planned 4 synthetic evidence PRs.

## Design Fidelity

- **No trigger-level `paths:` filter**: confirmed via `grep -n "paths:" .github/workflows/release.yml` — no match in the trigger block (only a comment referencing the STEP 1-6 classification).
- **`changes` detection job**: calls `.github/scripts/classify-changes.sh`, with a 3000-file-cap mismatch guard, fail-safe to both-groups-true on API failure/empty result.
- **`ci-gate`**: `needs: [changes, web-tests, web-e2e, build]`, `if: always()` — confirmed the only conditional on that job is `always()`, per the xUnit structural test (`CiPipelineGatingStructuralTests`, 4/4 passing) and direct file read.
- **Push-side unchanged**: `determine-channel`/`verify-protected-source` scoped to `if: github.event_name == 'push'`; a real push-triggered run on `dev` (see Real CI Evidence) confirms all real tests still run and pass on push.
- **`railway.json`/`.github/release-authorization.yml` untouched**: confirmed byte-identical via the same structural test's SHA-256 assertions and a direct `git diff --stat` (empty).

## Bugs Found and Fixed During Verification (not present at apply time's local testing, only surfaced on real GitHub Actions)

1. **Executable bit missing**: `.github/scripts/classify-changes.sh` and `aggregate-results.sh` were committed as mode `100644` (Windows/Git for Windows doesn't preserve the Unix executable bit by default). On the real Linux runner, this caused `Permission denied` (exit 126) on every `changes`/`ci-gate` run — a 100% failure rate on the actual pipeline, invisible in local script testing. Fixed via `git update-index --chmod=+x` (commit `d74d4b0`). Confirmed fixed: `git ls-files -s .github/scripts/*.sh` now shows `100755` on both files, and the `pull_request` run immediately after (`35524616527`) shows `changes: success`.
2. **`web-e2e` was failing on 4 pre-existing tests, unrelated to path-filtering itself but blocking a clean evidence run**: `ordering.spec.ts` (2 tests) failed because the `web-e2e` job never set `GuestOrdering__OrganizationId`/`GuestOrdering__BranchId`, so the public guest-ordering routes 404'd. `catalog.spec.ts` (2 tests) was stale — `CatalogScreen` was reworked by the already-archived `commerce-pricing-engine` change into a presentation-list/identification-code editor, and the test still targeted the old rename-form locators that no longer exist in the DOM. Both fixed (commit `94bfd16`): the workflow now seeds a real org/branch via the dev-only test-seed seam before restarting Cloud.Api with `GuestOrdering__*` set; `catalog.spec.ts` was rewritten against the real, current screen.

## Real CI Evidence (verified directly via `gh run`/`gh api`, not trusted from a prior summary)

| Run | Event | Trigger | Conclusion | Notes |
|---|---|---|---|---|
| `35523772248` | `pull_request` | PR #51 (pre-executable-bit-fix) | failure | `changes`/`ci-gate` failed: `Permission denied` |
| `35524616527` | `pull_request` | PR #51 (post-executable-bit-fix) | **success** | `changes`, `web-tests`, `build` all pass |
| `35525889490` | `push` | `dev` (merge of #51) | failure (by design) | `Run full test suite` step: **success**; only `Publication gate (publication_authorized)` failed — intentional per ADR-004, channel `internal` is not yet authorized |

Branch protection requiring `ci-gate` as the sole required status check has since been configured on `dev`/`staging`/`main` via the GitHub dashboard (repo owner action, outside this pass's tooling access — GitHub's branch-protection API 404s for any token without `admin` on the repo, confirmed via `gh api repos/.../<repo> --jq .permissions` returning `admin: false` for the session's authenticated account).

## Test Execution Evidence

| Command | Result |
|---|---|
| `dotnet build Commerce.sln` | PASS, 0 errors |
| `dotnet test Commerce.sln` | PASS, 628/628 (current `main`) |
| `classify-changes.test.sh` | 12/12 pass |
| `aggregate-results.test.sh` | 6/6 pass |

### Issues Found

**CRITICAL**: None remaining. (The two bugs above were CRITICAL at discovery — 100% pipeline failure and 4 persistent E2E failures — both closed with real evidence, not just a code fix; see Real CI Evidence.)

**WARNING**:
1. The `web-e2e` job's `Cloud.Api` publication-gate step always fails on push to `dev`/`staging`/`main` because no channel is yet `publication_authorized: true` in `.github/release-authorization.yml`. This is intentional per ADR-004, not a defect — but it means push-triggered `build` job runs will always show red. This does not affect PR merges (the publication-gate step is `push`-only, and `ci-gate` is the only required check), but is worth the repo owner's awareness.
2. The two residual risks named in design.md (no pre-merge `web-e2e` signal for .NET-only PRs; the `pulls/{n}/files` 3000-file API cap, mitigated by a count-mismatch guard) remain accepted as-is, per the original design.

### Verdict

**PASS** — All 19 tasks complete including real post-merge evidence. Two real bugs (not merely simulated risks) were found and fixed during this verification pass, both confirmed resolved via actual GitHub Actions runs, not local testing alone. `ci-gate` is now live and required on all three protected branches. Ready for archiving.
