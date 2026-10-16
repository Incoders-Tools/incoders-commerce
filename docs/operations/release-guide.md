# Release guide: from `dev` to production

How a change travels from development to Vaca Verde's production, how the cloud
and the POS are versioned, and how to roll back. Provisioning the environments
is covered by [the Railway runbook](../../deploy/railway-runbook.md); signing
and upgrade safety by [ADR-004](../architecture/decisions/ADR-004-release-signing-and-upgrades.md)
and [POS product updates](../pos-product-updates.md).

## What gets released

| Product part | Artifact | How it ships | Who runs it |
|---|---|---|---|
| Cloud.Api + web SPA | One Docker image, built by Railway from `railway.json` | Push to the environment's branch | Railway, automatically |
| Database schema | `deploy/db/migrations/*.sql` | Applied by hand before the code that needs it | The operator (runbook section 4) |
| POS (Commerce.Pos.Windows) | Signed MSIX plus a release manifest | A `v<semver>` tag, through `.github/workflows/pos-release.yml` | GitHub Actions, then the terminal's update wizard |

## Branches, environments, and channels

| Branch | Railway environment | POS channel | Purpose |
|---|---|---|---|
| `dev` | none (local Docker) | `internal` (prerelease tags) | Day-to-day integration |
| `staging` | `staging` | `internal` prereleases for pilot terminals | Validate a release candidate with real infrastructure |
| `main` | `production` | `stable` | What Vaca Verde runs |

`pos-release.yml` maps a tag without a suffix on a commit reachable from `main`
to `stable`, and everything else to `internal`. There is no separate `pilot`
POS channel yet: pilot terminals install `internal` prereleases built from
`staging`.

Publication is blocked until the owner flips `publication_authorized` in
`.github/release-authorization.yml` and sets the signing secrets (see
[go-live requirements](../launch/go-live-requirements.md)).

## Version numbers

The product carries one semantic version, `MAJOR.MINOR.PATCH`, and the tag
`v<version>` marks the `main` commit that production runs:

- **PATCH** (`1.4.1`): fixes only, with no migration and no sync-contract change.
- **MINOR** (`1.5.0`): new behavior. It may add migrations, and the cloud keeps
  accepting older POS versions.
- **MAJOR** (`2.0.0`): a change older POS versions cannot work with. Every
  terminal must update before, or together with, the cloud.

Prereleases for internal or pilot terminals use a suffix, for example
`v1.5.0-internal.1`, `v1.5.0-internal.2`.

## Rules every release follows

1. **Migrations go first and only expand.** Apply a release's migrations before
   its code deploys, and write them so the version still running keeps working
   (add tables and columns; remove or rename only in a later release, once no
   running code uses them). `/health/ready` fails closed while the schema is
   behind the code.
2. **Staging before production.** Each migration and each build reaches staging
   first, and production only after staging was verified.
3. **Cloud before POS.** Deploy the cloud, then publish the POS release, so a
   terminal never syncs with a cloud older than itself.
4. **Back up before migrating production.** Take a named backup and an
   off-Railway `pg_dump` (runbook section 7) right before applying migrations.

## Promote a release

### 1. Prepare on `dev`

- All work is merged into `dev`, and the full test suite passes locally
  (`dotnet test Commerce.sln` and the web checks).
- Optional: tag `v<next>-internal.<n>` on `dev` to give the VM an internal POS
  build (see the installed VM test loop in
  [POS product updates](../pos-product-updates.md#installed-vm-test-loop)).

### 2. Deploy to staging

1. List the migrations that are new since the last production release:
   `git diff --name-only main dev -- deploy/db/migrations`.
2. Apply them to staging with the runbook's migration loop (section 4,
   `--environment staging`). The loop is idempotent, so running all migrations
   is fine.
3. Move the code: `git checkout staging`, `git merge --ff-only dev`,
   `git push`. Railway deploys the staging environment.
4. Verify: `/health` and `/health/ready` answer 200 at the staging origin, sign
   in, and walk through the changed screens. If the release touches the POS,
   point a pilot terminal at staging and sync.

### 3. Deploy to production

1. Open a pull request from `staging` to `main`. The CI gates in
   `.github/workflows/release.yml` run on it: the web tests and the end-to-end
   suite when the web changed, and the full .NET test suite when .NET code
   changed.
2. Before merging, if the release adds migrations:
   - Take a named backup:
     `railway postgres pitr backup create --service Postgres --environment production --name pre-v<version>`.
   - Take an off-Railway `pg_dump`.
   - Apply the migrations with the runbook loop, `--environment production`.
3. Merge the pull request. Railway deploys production from `main`.
4. Verify `/health/ready`, sign in as Vaca Verde, and confirm one POS sync.
5. Tag the release on the merged commit and push the tag:

   ```powershell
   git checkout main; git pull
   git tag v<version>
   git push origin v<version>
   ```

   The tag triggers `pos-release.yml`, which publishes the stable POS release.
   Terminals see the update in their update wizard.

### 4. Bring the branches back in line

After the release, `staging` and `dev` must contain `main`:
`git checkout dev; git merge main; git push`, then fast-forward `staging` the
same way.

## Hotfix

1. Branch from `main` (`hotfix/<topic>`), fix it with its test, and open a pull
   request to `main`.
2. If it needs a migration, apply it to staging and production first (rules 1
   and 4).
3. Merge, verify, and tag the next PATCH version.
4. Merge `main` back into `staging` and `dev`.

## Rollback

| What broke | How to go back |
|---|---|
| Cloud code (bad deploy, no data damage) | In Railway, open Cloud.Api's **Deployments** in the affected environment and roll back to the previous successful deployment (its three-dot menu). This is safe because migrations only expand (rule 1). Then fix forward on `main`. |
| Data (wrong writes, deleted rows) | Restore to a point in time before the incident in a sibling service, check it, and switch Cloud.Api's connection strings to it (runbook section 10). The newest restorable point is minutes old, and anything written after the chosen point is lost. |
| A migration | Never edit an applied migration. Write a new migration that corrects it, or restore the data as in the previous row. |
| POS release | Never overwrite or delete a published release. Publish a newer PATCH version that restores the desired behavior (roll forward), as [POS product updates](../pos-product-updates.md) requires. |

## Checklist per production release

- [ ] Staging ran the same commit and passed verification.
- [ ] The `staging` → `main` pull request's checks are green.
- [ ] Named backup and `pg_dump` taken (if the release adds migrations).
- [ ] Migrations applied to production before the merge.
- [ ] `/health/ready` answers 200 in production after the deploy.
- [ ] `v<version>` tag pushed, and the POS release published (once publication
      is authorized).
- [ ] `dev` and `staging` contain `main` again.
