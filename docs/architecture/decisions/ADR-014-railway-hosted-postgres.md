# ADR-014: Railway-hosted PostgreSQL on Railway Pro

## Status

Accepted (owner decision, 2026-10-15). Replaces the Supabase database host
assumed by `commerce-deployment-orchestration` (`deploy/README.md` "Unit 2" and
"Unit 5"). The runtime stack in [ADR-001](./ADR-001-stack-and-harness.md) is
unchanged: the database is still PostgreSQL, accessed only through Npgsql.

## Context

The deployment design ran Cloud.Api on Railway and the database on Supabase. No
Supabase or Railway resource has been created yet, so the choice costs nothing to
change now. The owner wants one provider, the lowest predictable monthly cost,
restorable backups of client data, and a notification when spend approaches a set
amount instead of an open-ended bill.

The first client is Vaca Verde. Most load comes from the POS, which syncs with
Cloud.Api; the web console has at most 2-3 concurrent users.

Nothing in `src/` or `deploy/db/migrations` depends on Supabase: no Supabase
roles, schemas, or extensions are referenced, and the database name never
appears in a migration.

## Decision

- **Plan: Railway Pro ($20/month).** The subscription includes $20 of usage. For
  this load, staging and production together are expected to use about $9-14, so
  the bill stays at $20.
- **One Railway project, two environments.** `staging` deploys from the `staging`
  branch and `production` deploys from `main` (the ADR-004 `pilot` and `stable`
  channels). Each environment has its own Cloud.Api service, its own Postgres
  service, and its own credentials.
- **Postgres runs as a Railway service on the private network.** Cloud.Api
  connects to `postgres.railway.internal:5432`. The database has no public TCP
  proxy; administrators reach it with `railway connect --ssh`. The private network
  is isolated per environment, so staging cannot reach production.
- **Image: `ghcr.io/railwayapp-templates/postgres-ssl:18`.** That is the Railway
  image point-in-time recovery requires, on a major-version tag. Railway's
  template installs 18 (staging, 2026-10-16), and 18 is supported until 2030,
  so the database starts there instead of facing a dump-and-restore major
  upgrade later. All 60 migrations applied twice on PostgreSQL 18.6, and the CI
  `migrations` job runs on `postgres:18`. Local development
  (`deploy/dev/compose.yaml`) stays on 16 until its data volume is recreated.
- **No external pooler.** Cloud.Api connects directly and relies on Npgsql's
  built-in pool. Tenant scope is set per transaction with
  `set_config('app.current_org_id', v, true)`, which is correct with or without a
  pooler. Add PgBouncer as another Railway service only if Cloud.Api ever runs
  several replicas.
- **The runtime never uses the `postgres` user.** Railway's `postgres` user is a
  superuser, and a superuser bypasses RLS. Cloud.Api connects as `app_runtime`,
  and the platform read path as `platform_readonly`, exactly as before.
- **Backups, three layers:**
  1. Scheduled volume backups (daily, weekly, and monthly; kept 6 days, about a
     month, and about 3 months).
  2. Point-in-time recovery (WAL archived to a Railway bucket, about a 4-week
     window).
  3. A periodic `pg_dump` stored outside Railway. Deleting a volume deletes its
     backups, so the first two layers do not survive losing the volume or the
     account.
- **Cost control: an email alert, no hard limit.** The workspace usage alert is
  set at $20. Railway's hard limit takes every workload offline when reached,
  which would stop production in the middle of a business day. It stays unset
  unless the owner decides otherwise.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Supabase (the original design) | It adds a second provider, bill, and dashboard. Its auth, storage, and API features are unused because the platform owns identity. |
| Railway Hobby ($5/month) | Cheaper, but the docs do not say whether scheduled backups are available, pre-update volume backups are Pro only, volumes cap at 5 GB, and services get two custom domains each. |
| One deployment per client | It multiplies cost and the number of databases to migrate. The platform is multi-organization, with RLS per organization, so one deployment serves every client. |

## Consequences

- `deploy/railway-runbook.md` replaces `deploy/staging-runbook.md` and covers both
  environments.
- Applying a migration means piping the SQL through `railway connect --ssh`. The
  repository still owns every migration, and Cloud.Api still only verifies the
  schema at `/health/ready`.
- The Supavisor gap recorded in `deploy/README.md` ("What remains unproven") no
  longer applies, because no transaction-mode pooler sits in front of production.
- **A client with its own website** maps another custom domain onto the same
  Cloud.Api service (Pro allows 20 per service). Cloud.Api does not resolve an
  organization from the request host yet; that is a feature to build when the
  first such client appears, not an infrastructure change.
- **Restore is manual.** A point-in-time restore creates a sibling Postgres
  service, and switching Cloud.Api to it is a variable change. The runbook
  includes a restore drill that must pass before go-live.
- **Revisit triggers:** monthly usage that regularly approaches $20, a need for
  more than one Cloud.Api replica, or a requirement for managed high availability
  (Railway Postgres HA).

## Evidence

Railway documentation, read 2026-10-15 at `docs.railway.com`: `/pricing`,
`/pricing/plans`, `/pricing/cost-control`, `/volumes/backups`,
`/volumes/point-in-time-recovery`, `/networking/private-networking`,
`/networking/domains/working-with-domains`, `/cli/connect`, `/cli/postgres`, and
`/guides/isolate-staging-production`. Repository: `deploy/db/migrations`
(no Supabase dependency), `deploy/dev/compose.yaml` (`postgres:16`), and
`src/Commerce.Cloud.Api/Program.cs` (one connection string per role, readiness
verification only).
