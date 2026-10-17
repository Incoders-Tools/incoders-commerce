# ADR-016: Migrations at deploy time, branch gates, and rollback

## Status

Accepted (owner decision, 2026-10-16). Supersedes the manual migration step of
[ADR-014](./ADR-014-railway-hosted-postgres.md) and the "applied by hand" rule in `deploy/README.md` ("Unit 5").

**Amended 2026-10-17:** the pre-deploy command, healthcheck, and watch paths are Railway dashboard settings, set
once per environment ([runbook section 5](../../../deploy/railway-runbook.md#5-cloudapi-service)), not a `railway.json`.
Railway's Config as Code is deprecated: a service that never used it cannot opt in since 2026-08-28, and existing files
stop being read on 2026-12-01, so staging's first deploy ignored the file and ran no pre-deploy stage. Its successor,
Infrastructure as Code (`.railway/railway.ts` applied with the Railway CLI), is the option for versioning these
settings again; it is not adopted now, to keep the setup simple.

## Context

The owner's rules: `dev` is pure development, with no gates and no migration policies. `staging` and `main` must run
the gates and apply migrations to their own Railway environment, idempotently, and every failure, whether in the system
or in the database, must have a way back. The same applies to POS versions.

Until now migrations were applied by an operator through a tunnel, with no record of what had run. The Postgres
service has no public access, so GitHub Actions cannot reach it.

## Decision

- **The migrator runs inside Railway at every deploy.** `src/Commerce.Migrator` ships in the Cloud.Api image with the
  `deploy/db/migrations` files, and the service's Pre-deploy Command (a dashboard setting, see the amendment above)
  runs it. Railway runs it in a separate
  container with the service's variables and the private network, before the new version serves traffic. A failure
  cancels the deployment, and the previous version keeps running.
- **Idempotency.** The ledger `public.schema_migrations` records each applied file with a checksum (SHA-256, line
  endings normalized). Applied files are skipped, and an edited applied file stops the run before anything executes.
  A session advisory lock serializes concurrent runs. Each migration file is also written to be safe to re-run
  (`AGENTS.md`), so a run interrupted between a file and its ledger row repeats that file harmlessly.
- **Migrations only expand.** A release may add tables, columns, constraints and policies. Dropping or renaming a
  table or column is a separate, later release, once no running version uses it, and carries the marker comment
  `-- contract-change: approved` in that file. This keeps the previous application version compatible with the
  current schema, which is what makes a code rollback safe.
- **Branch gates.** No workflow gates `dev`. Pull requests into `staging` and into `main` run the CI gate (`ci-gate`
  in `.github/workflows/release.yml`), which now includes a `migrations` job:
  1. It applies every migration twice to an empty Postgres 18 (the production major) with the migrator, and the second run must skip
     everything.
  2. It rejects edits to existing migration files.
  3. It rejects a `DROP TABLE`, `DROP COLUMN`, or `RENAME` without the marker.

  Merging is the deploy: Railway deploys `staging` and `main` to their environments.
- **POS channels follow the branches.** `vX.Y.Z-rc.N` tags (from `staging`) publish to `pilot`, plain `vX.Y.Z` tags
  on `main` publish to `stable`, and other prereleases stay `internal`.

## Rollback

| Failure | What happens / what to do |
|---|---|
| A migration fails | The pre-deploy command exits non-zero, Railway cancels the deployment, and the previous version keeps serving. The failed file has no ledger row; fix it in a new commit, and the next deploy retries it. |
| The new version fails its healthcheck | Railway never switches traffic to it; the previous deployment keeps serving. |
| A bad deployment that passed its healthcheck | Roll back to the previous deployment in Railway (one action). The schema is compatible because migrations only expand. Fix forward on the branch. |
| Data damaged by a bug or a migration | Point-in-time restore into a sibling service, verify it, and switch the connection strings to it (`deploy/railway-runbook.md` section 10). |
| A bad POS release | Never overwrite a published release: publish a newer version (roll forward, `docs/pos-product-updates.md`). |

There are no down-migrations: reversing a schema change is a new forward migration, and reversing data is a restore.

## Consequences

- The pre-deploy command is not versioned with the code. An environment whose dashboard lacks it deploys without
  migrating, and `/health/ready` reports the missing schema; the runbook's checklist and troubleshooting steps cover
  it.
- `ConnectionStrings__Migrations` (the `postgres` superuser, set as a reference to the Postgres service's password)
  is visible to the Cloud.Api runtime container, because Railway's pre-deploy command shares the service's
  variables. Cloud.Api never reads that key. Moving the migrator to a non-superuser owner role is possible later.
- A deploy, restart or rollback does not sign anyone out. Cloud.Api keeps its ASP.NET Core Data Protection key ring
  (the keys that protect auth cookies) in `data_protection_keys` (migration `0061`), not inside the container, so
  every instance and every version reads the same keys. The deploy that introduces it signs users out one last time
  (their cookies were protected with the old container's keys). `/health/ready` fails while the table is missing. Rolling
  back to a version older than `0061` still works, but that version keeps keys inside the container again, so its
  users are signed out once. The keys are stored unencrypted, protected by the private network, the grants (only
  `app_runtime`, SELECT and INSERT) and the encrypted backups.
- The first deploy of an existing database with no ledger re-runs every migration. That is safe because each one is
  idempotent; CI proves it on every pull request.
- A deploy that finds the advisory lock taken polls for it, printing a waiting line at most every 30 seconds, and
  proceeds once the other run finishes. The wait is bounded: after `MIGRATOR_LOCK_TIMEOUT_SECONDS` (default 600) the
  migrator exits 1 with nothing applied, so a stuck lock holder cancels the deploy instead of hanging it. Migration
  statements themselves have no timeout.
