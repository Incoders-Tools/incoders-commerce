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

Since ADR-016 (`docs/architecture/decisions/ADR-016-migrations-at-deploy-and-rollback.md`)
the workflow runs on pull requests into `staging` and `main` only, and
`ci-gate` also aggregates the `migrations` job (expand-only guard plus the
migrator applied twice to an empty Postgres 16). Merging into `staging` or
`main` deploys that Railway environment and migrates its database, so those
two branches are the gated ones.

**The one required status check is `ci-gate`, on `staging` and `main`
only.** `changes`, `web-tests`, `web-e2e`, `build`, `migrations`,
`determine-channel`, and `verify-protected-source` MUST NOT be named as
required checks — naming a path-filtered job directly would
let GitHub treat its `skipped` conclusion as a passing required check even
when the filter was wrong, defeating the point of `ci-gate`'s explicit
`changes` must be `success` assertion.

| Branch | Required check | Require a pull request before merging | Direct pushes |
|---|---|---|---|
| `staging` | `ci-gate` | Yes | Blocked by the PR requirement |
| `main` | `ci-gate` | Yes | Blocked by the PR requirement |
| `dev` | None | No | Allowed |

**Why `dev` is excluded:** the owner's rule is that `dev` is pure
development, with no gates and no migration policies (ADR-016). The workflow
does not run on pull requests into `dev`, so a required `ci-gate` there would
never be reported and every PR into `dev` would hang Pending. `dev` gets no
protection rule that blocks direct pushes either.

## Step 1 — record the prior required-check list (do this FIRST)

Run this before touching branch protection. Record all three branches,
`dev` included: Step 4 may remove `dev`'s protection, and rollback restores
it from this record. Rollback depends on this
output; a 404 is **inconclusive**, per `release.yml:41-44`'s existing
"never treat inconclusive as proof of absence" principle — record the raw
status, never record it as "no checks were required":

```bash
for b in dev staging main; do
  gh api "repos/Incoders-Tools/incoders-commerce/branches/$b/protection/required_status_checks" \
    --jq '{strict, checks}' || echo "$b: endpoint inconclusive"
  gh api "repos/Incoders-Tools/incoders-commerce/branches/$b/protection" \
    || echo "$b: protection endpoint inconclusive"
done
```

Save this output somewhere durable (a comment on the tracking issue/PR, a
local note, etc.) before proceeding to Step 2.

## Step 2 — merge the workflow change first

Apply the `required_status_checks` PATCH below **only after**
`.github/workflows/release.yml` (this change) has merged into the base
branch being protected. Patching branch protection before `ci-gate` exists
on that branch means every PR hangs on a required check that never runs.

Recommended rollout order:

1. Merge the `release.yml` change to `dev` (no CI runs there).
2. Once, before the first `dev -> staging` PR, fast-forward `staging` to
   `main`:

   ```powershell
   git fetch incoders
   git push incoders incoders/main:refs/heads/staging
   ```

   `staging` has none of the migrations, so the `migrations` guard would see
   every file as added and reject the historical contract changes in `0012`,
   `0028`, and `0029`. The push fails unless it is a fast-forward. Details in
   the release guide's
   [one-time setup](release-guide.md#one-time-setup-align-staging-before-the-first-gated-promotion).
3. Open the promotion PR `dev -> staging`. A pull request runs the workflow
   as it exists in the PR, so this first PR already runs `ci-gate`, including
   `migrations`. Confirm it is green.
4. Merge it, then apply Step 3 to `staging`.
5. Promote `staging -> main` the same way, then apply Step 3 to `main`.
6. Apply Step 4 to `dev`.

## Step 3 — protect `staging` and `main`

For each of `staging` and `main`, in GitHub → Settings → Branches, edit (or
add) the branch protection rule and enable:

- **Require a pull request before merging.**
- **Require status checks to pass before merging**, with `ci-gate` as the
  only check.

Then set the required check with the API (the endpoint needs the rule from
the step above to exist):

```bash
for b in staging main; do
  gh api --method PATCH \
    "repos/Incoders-Tools/incoders-commerce/branches/$b/protection/required_status_checks" \
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

## Step 4 — leave `dev` ungated

If Step 1 shows any required check or protection rule on `dev`, remove the
rule in GitHub → Settings → Branches, or with the API:

```bash
gh api --method DELETE "repos/Incoders-Tools/incoders-commerce/branches/dev/protection"
```

Never add `ci-gate` to `dev`: the workflow does not run on pull requests into
`dev`, so the check would never be reported.

## Rollback ordering (inverts the apply order)

1. Remove `ci-gate` from required checks on `staging` and `main` **first**.
2. Restore the exact prior lists and protection recorded in Step 1, `dev`
   included.
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
