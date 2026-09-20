# CI Required-Status-Check Runbook (Phase G — Path-Filtered CI/CD)

Source of truth for this runbook is `openspec/changes/commerce-cicd-path-filtering/design.md`
("Required-Status-Check Contract"). This document is **operator instructions
only** — nothing here is executed automatically by any task, script, or CI
job. The repo admin (per the proposal's answered question (d), the user
owns branch-protection settings) runs these commands manually, by hand,
after the steps below are satisfied.

## Why this exists

Before this change, `.github/workflows/release.yml` ran only on `push` and
had no path filtering. After this change, the pipeline runs on
`pull_request` too, with three jobs (`web-tests`, `web-e2e`, `build`)
conditionally skipped by path. A path-filtered job still reports a `skipped`
conclusion, which the new always-run `ci-gate` job (see design.md)
aggregates into a single pass/fail signal.

**The one required status check is `ci-gate`.** `changes`, `web-tests`,
`web-e2e`, `build`, `determine-channel`, and `verify-protected-source` MUST
NOT be named as required checks — naming a path-filtered job directly would
let GitHub treat its `skipped` conclusion as a passing required check even
when the filter was wrong, defeating the point of `ci-gate`'s explicit
`changes` must be `success` assertion.

## Step 1 — record the prior required-check list (do this FIRST)

Run this before touching branch protection. Rollback depends on this
output; a 404 is **inconclusive**, per `release.yml:41-44`'s existing
"never treat inconclusive as proof of absence" principle — record the raw
status, never record it as "no checks were required":

```bash
for b in dev staging main; do
  gh api "repos/Patricio-Montes/incoders-commerce/branches/$b/protection/required_status_checks" \
    --jq '{strict, checks}' || echo "$b: endpoint inconclusive"
done
```

Save this output somewhere durable (a comment on the tracking issue/PR, a
local note, etc.) before proceeding to Step 2.

## Step 2 — merge the workflow change first

Apply the `required_status_checks` PATCH below **only after**
`.github/workflows/release.yml` (this change) has merged into the base
branch being protected. Patching branch protection before `ci-gate` exists
on that branch means every PR hangs on a required check that never runs.

Recommended rollout order (per design.md "Migration / Rollout"):

1. Merge `release.yml` + `openspec/config.yaml` to `dev`.
2. Confirm one push run on `dev` still shows all five original jobs and the
   publication-gate log line for channel `internal`.
3. Open the four evidence PRs (Web-only, .NET-only, docs-only, both);
   confirm `ci-gate` is green in all four.
4. Only then apply the PATCH below — to `dev`, then `staging`, then `main`.
5. Promote `dev -> staging -> main` by the project's existing branch flow.

## Step 3 — apply the PATCH

```bash
for b in dev staging main; do
  gh api --method PATCH \
    "repos/Patricio-Montes/incoders-commerce/branches/$b/protection/required_status_checks" \
    --input - <<'JSON'
{ "strict": false, "checks": [ { "context": "ci-gate" } ] }
JSON
done
```

`strict: false` is deliberate: `strict: true` would force every PR to
re-run the full gate after each base-branch push, which contradicts the
proposal's motivating pain (c) (CI-minutes waste is a co-equal concern, not
subordinate to gate strictness). **If Step 1's output shows `strict: true`
today, preserve that value instead of overwriting it to `false`.**

## Rollback ordering (inverts the apply order)

1. Remove `ci-gate` from required checks **first**.
2. Restore the exact prior list recorded in Step 1.
3. Only then revert the `release.yml` commit.

Reverting the workflow before un-requiring `ci-gate` leaves branch
protection referencing a check that no longer runs, which blocks every PR
on that branch (the operator caveat named in `proposal.md`'s Rollback Plan).

## Accepted residual risks (design.md Open Questions)

These two risks were evaluated during design and are accepted as-is for
this change; neither is closed here, and neither should be silently
reinterpreted or expanded in scope by a later change without a fresh
decision:

1. **A .NET-only PR gets no pre-merge `web-e2e` signal.** `web-e2e` boots
   the real `Commerce.Cloud.Api`, so a Cloud.Api contract break is exactly
   what it would catch — but the proposal's own success criterion requires
   a .NET-only PR to *not* run `web-e2e`, and that criterion is honored.
   Residual coverage: the push to `dev` after merge still runs `web-e2e`
   (Decision 4), so the signal is delayed, not lost. Adding
   `src/Commerce.Cloud.Api/**` to the `web-e2e` trigger condition would
   close this at the cost of that criterion — a one-line follow-up, not a
   silent reinterpretation made here.
2. **The `pulls/{n}/files` endpoint caps at 3000 files.** A PR above that
   cap returns a truncated list, and the classifier cannot detect
   truncation from `--jq` output alone. Mitigated, not eliminated, by the
   3000-file-cap guard in `.github/scripts/classify-changes.sh`: it
   compares the returned filename count against
   `github.event.pull_request.changed_files` and forces both groups `true`
   on any mismatch.
