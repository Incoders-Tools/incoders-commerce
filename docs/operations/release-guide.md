# Release guide: from `dev` to production

How a change travels from development to Vaca Verde's production, how the cloud
and the POS are versioned, and how to roll back. Deployments and migrations are
automatic since [ADR-016](../architecture/decisions/ADR-016-migrations-at-deploy-and-rollback.md):
merging is the deploy. Provisioning the environments is covered by
[the Railway runbook](../../deploy/railway-runbook.md); signing and upgrade
safety by [ADR-004](../architecture/decisions/ADR-004-release-signing-and-upgrades.md)
and [POS product updates](../pos-product-updates.md).

## What gets released

| Product part | Artifact | How it ships | Who runs it |
|---|---|---|---|
| Cloud.Api + web SPA | One Docker image, built by Railway from the root `Dockerfile` | Merge into `staging` or `main` | Railway, automatically |
| Database schema | `deploy/db/migrations/*.sql` | `Commerce.Migrator`, Railway's pre-deploy command, at every Cloud.Api deploy | Railway, automatically (runbook section 4) |
| POS (Commerce.Pos.Windows) | Signed MSIX plus a release manifest | A `v<semver>` tag, through `.github/workflows/pos-release.yml` | GitHub Actions, then the terminal's update wizard |
| Vaca Verde's public website | `sites/vaca-verde`, Railway service `vaca-verde-site` | Merge into `main` (production only) | Railway, automatically (runbook section 6c) |

Railway rebuilds a service only when a merge touches one of its watch paths.
Those, like the pre-deploy command and the healthcheck, are dashboard settings
of each service and environment (runbook sections 5 and 6c): Railway's Config
as Code (`railway.json`) is deprecated and ignored for new services.

## Branches, environments, and channels

| Branch | Gate on pull requests | Railway environment | Migrations | POS tag → channel |
|---|---|---|---|---|
| `dev` | None | None (local Docker) | Local only | `vX.Y.Z-internal.N` → `internal` |
| `staging` | `ci-gate` | `staging` | Applied there at deploy | `vX.Y.Z-rc.N` → `pilot` |
| `main` | `ci-gate` | `production` | Applied there at deploy | `vX.Y.Z` → `stable` |

`ci-gate` (`.github/workflows/release.yml`) aggregates the web tests, the
end-to-end suite, the .NET build and tests, and the `migrations` job. Each one
runs only when its paths changed. The `migrations` job rejects an edited
migration or an unapproved drop or rename, and applies every migration twice to
an empty Postgres 18 with the migrator; the second run must skip them all.
Branch protection is set up by hand with
[the required-checks runbook](./ci-required-status-checks-runbook.md).

Publication of each POS channel stays blocked until the owner flips its
`publication_authorized` flag in `.github/release-authorization.yml` and sets
the signing secrets (see [go-live requirements](../launch/go-live-requirements.md)).

## One-time setup: align `staging` before the first gated promotion

`staging` on GitHub is an ancestor of `main`, but 625 commits behind, and it has
no migrations. The `migrations` job compares a pull request against its base
branch, so the first `dev` → `staging` pull request would see every migration as
new. The guard would then reject `0012_admin_console.sql`,
`0028_core_geography.sql`, and `0029_customer_contacts.sql`: historical contract
changes that predate the approval marker.

**Done on 2026-10-16** through pull request #78 (`main` → `staging`, merge
commit `0e2471b`). `staging` already required a pull request, so a direct push
was rejected. After the merge, `staging`'s tree equals `main`'s, it holds all
60 migrations, and the guard run against it from `dev` passes.

If `staging` ever falls behind `main` again (for example after a hotfix),
repeat the same way: open a `main` → `staging` pull request and merge it with a
merge commit. From then on `staging` only moves through gated pull requests.

## Version numbers

The product carries one semantic version, `MAJOR.MINOR.PATCH`, and the tag
`v<version>` on `main` marks the commit that production runs:

- **PATCH** (`1.4.1`): fixes only, with no migration and no sync-contract change.
- **MINOR** (`1.5.0`): new behavior. It may add migrations, and the cloud keeps
  accepting older POS versions.
- **MAJOR** (`2.0.0`): a change older POS versions cannot work with. Every
  terminal must update before, or together with, the cloud.

Prereleases carry a suffix: `-internal.N` for builds from `dev`
(`v1.5.0-internal.1`) and `-rc.N` for release candidates from `staging`
(`v1.5.0-rc.2`). Semver precedence ranks `1.5.0-rc.2` below `1.5.0`, so a pilot
terminal on a candidate is offered the final release when it ships
([release channels](../pos-product-updates.md#release-channels)).

## Rules every release follows

1. **Migrations only expand.** A release may add tables, columns, constraints,
   and policies. Dropping or renaming a table or column is a separate, later
   release, once no running version uses it, and that file carries the exact
   line `-- contract-change: approved`. CI enforces this
   (`.github/scripts/check-migrations.sh`).
2. **Never edit an applied migration.** CI rejects a modified, renamed, or
   deleted migration file, and the migrator's ledger refuses an applied file
   whose checksum changed. Correct a mistake with a new migration.
3. **Contract changes take two releases.** Release 1 adds the new shape and
   stops using the old one; release 2 drops it, with the marker.
4. **Staging before production.** Every change reaches `staging` first, and
   `main` only after staging was verified.
5. **Cloud before POS.** Deploy the cloud, then publish the POS release, so a
   terminal never syncs with a cloud older than itself.

## Promote a release

### 1. Finish on `dev`

- Merge the work into `dev`. Nothing is gated there, so run the tests locally
  (`dotnet test Commerce.sln` and the web checks).
- Optional: tag `v<next>-internal.<n>` on `dev` to give the VM an internal POS
  build ([installed VM test loop](../pos-product-updates.md#installed-vm-test-loop)).

### 2. Deploy to staging

1. Open a pull request from `dev` to `staging`. Wait for `ci-gate` to pass.
2. Merge it. Railway builds the staging image, runs the migrator in a separate
   container, starts the new version, waits for `/health`, and only then
   switches traffic to it.
3. Verify at `https://app.staging.vaca-verde.com.ar` (runbook section 9):
   `/health` and `/health/ready` answer 200, a staff user can sign in, and the
   changed screens work. The deploy log lists `applied <file>` for each new
   migration.
4. Optional, if the release touches the POS: tag a release candidate on the
   merged `staging` commit for the pilot terminals:

   ```powershell
   git fetch incoders
   git tag v<version>-rc.<n> incoders/staging
   git push incoders v<version>-rc.<n>
   ```

   Terminals with `Commerce__UpdateChannel` set to `pilot` are offered it.

### 3. Deploy to production

1. Open a pull request from `staging` to `main`. Wait for `ci-gate` to pass.
2. **Before merging a release that adds migrations**, take a named backup and
   an off-Railway `pg_dump` (runbook section 7). PITR can already restore to
   any point, but a named point is faster to find in an incident:

   ```powershell
   railway postgres pitr backup create --service Postgres --environment production --name pre-v<version>
   ```

3. Merge it. Production deploys the same way staging did.
4. Verify at `https://app.vaca-verde.com.ar`: `/health/ready` answers 200, sign
   in as Vaca Verde, and confirm one POS sync.
5. Tag the release on the merged commit and push the tag:

   ```powershell
   git fetch incoders
   git tag v<version> incoders/main
   git push incoders v<version>
   ```

   The tag triggers `pos-release.yml`, which publishes the `stable` POS
   release. Terminals see the update in their update wizard.

### 4. Bring the branches back in line

- `dev` takes `main` directly (it is not protected):
  `git checkout dev; git merge incoders/main; git push incoders dev`.
- `staging` cannot be pushed to any more. After a normal release it lacks only
  the merge commit, and the next `dev` → `staging` pull request brings it.

## Hotfix

1. Branch from `main` (`hotfix/<topic>`), fix it with its test, and open a pull
   request to `main`. `ci-gate` runs as for any release.
2. Prefer a hotfix without migrations. If it needs one, it follows rules 1-3,
   and you take the backups from "Deploy to production", step 2, before merging.
3. Merge, verify, and tag the next PATCH version.
4. Merge `main` back into `dev`, and open a pull request from `main` to
   `staging` so staging runs the same fix.

## Rollback

There are no down-migrations. Reversing a schema change is a new forward
migration; reversing data is a restore.

| Failure | What happens / what to do |
|---|---|
| A migration fails | The pre-deploy command exits non-zero, Railway cancels the deployment, and the previous version keeps serving. The failed file has no ledger row: fix it in a new commit, and the next deploy retries it. |
| The new version fails its healthcheck | Railway never switches traffic to it; the previous deployment keeps serving. Fix forward on the branch. |
| A bad deployment that passed its healthcheck | In Railway, open Cloud.Api's **Deployments** in the affected environment and roll back to the previous successful deployment (its three-dot menu). The schema stays compatible because migrations only expand. Fix forward on the branch. |
| Data damaged by a bug or a migration | Restore to a point before the incident in a sibling service, verify it, and switch Cloud.Api's connection strings to it (runbook section 10). Anything written after the chosen point is lost. |
| A bad POS release | Never overwrite or delete a published release. Publish a newer version that restores the desired behavior (roll forward, [POS product updates](../pos-product-updates.md)). |

## Onboarding another client

Every client runs on the same shared deployment as its own organization
([ADR-015](../architecture/decisions/ADR-015-shared-multi-client-platform.md)).
Onboarding is operational work, not a new deployment:

1. Create the organization and its first administrator (runbook section 8, or
   `POST /account/organizations` as a system administrator), and its branches.
2. Configure its settings, branding, price lists, and roles. A client's rules
   are configuration, never a code fork.
3. Add its public website under `sites/<client>`, with its own Railway service
   and domain, as `sites/vaca-verde` does (runbook section 6c).
4. Pair its terminals with the platform origin.

Before a **second** client goes live:

- **Host-based organization resolution** must replace the deployment-wide
  `GuestOrdering__OrganizationId` and `GuestOrdering__BranchId` variables, which
  make `/order` serve a single organization.
- **The platform moves to a neutral Incoders domain** (ADR-006 amendment);
  `app.vaca-verde.com.ar` stays as a custom domain of the same service.

## File storage

There is no upload feature yet (images, invoices). When one is built, files go
to a Railway Storage Bucket, available on the Pro plan: private, S3-compatible,
served through presigned URLs, $0.015 per GB-month with free egress. Database
backups do not include bucket contents, so a bucket needs its own backup.

## Checklist per production release

- [ ] Staging ran the same commit, and its deploy and verification passed.
- [ ] The `staging` → `main` pull request's `ci-gate` is green.
- [ ] Named backup `pre-v<version>` and `pg_dump` taken (if the release adds
      migrations).
- [ ] The production deploy log shows the migrator succeeded, and
      `/health/ready` answers 200.
- [ ] Sign-in and one POS sync confirmed in production.
- [ ] `v<version>` tag pushed, and the POS release published (once publication
      is authorized).
- [ ] `dev` contains `main` again.
