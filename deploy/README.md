# Deploy notes

## Unit 2 — Transaction-pooler proof-of-concept outcome

**Result: PASS (for the tested local approximation) — transaction-mode pooling accepted, with a documented gap.**

`design.md`'s "Pooling + tenant scope" decision requires a runnable PoC before
trusting `set_config('app.current_org_id', v, true)` (`SET LOCAL` semantics)
under a transaction-mode connection pooler, rather than assuming it from
reasoning alone. A real Supabase project is manual, out-of-repo provisioning
and was not available in this environment, so this PoC ran against the
**strongest available local approximation**: PgBouncer (`pool_mode =
transaction`) in front of the same Postgres used by `deploy/dev/compose.yaml`
(see the `pgbouncer` service and `pgbouncer-userlist.txt` added in Unit 2).

### What was proven

- `tests/Commerce.Integration/PoolerScopingTests.cs`
  (`ConcurrentPooledTransactions_NeverObserveAnotherOrganizationsRows`): 12
  organizations concurrently applying and repeatedly reading through the same
  pooled endpoint (port 6543), each opening its own explicit
  `PostgresCloudInboxStore` transaction, observed **zero cross-organization
  rows** across all runs (verified stable over multiple consecutive full
  suite runs).
- `tests/Commerce.Integration/PostgresCloudInboxStoreTests.cs`: deny/allow
  parity between `PostgresCloudInboxStore` (real Npgsql adapter) and the
  in-memory `CloudInboxStore` test double, against the SAME live schema, both
  directly (port 5432) and through the pooler.
- A genuine RLS hardening bug was found and fixed during this PoC: PostgreSQL
  does not revert a custom placeholder GUC back to "unset"/`NULL` after a
  transaction-local `set_config(..., true)` commits — it reverts to `''`
  (empty string) for the rest of that session/pooled-connection lifetime. The
  original policy (`organization_id = current_setting(...)::uuid`) would
  raise a Postgres cast error instead of a clean zero-row deny if any query
  ever ran on a reused connection without re-applying scope first. The policy
  in `deploy/dev/db/init-rls.sql` now uses
  `NULLIF(current_setting(...), '')::uuid` so an unscoped query fails closed
  (zero rows) instead of erroring. `PostgresCloudInboxStore` always sets
  scope as the first statement of every transaction, so this path is not hit
  in production, but the policy is hardened regardless of caller discipline.
- A first PgBouncer configuration attempt hardcoded a single backend role
  (`commerce_owner`, a Postgres **superuser** in the stock `postgres` Docker
  image) for every client connection. That silently bypassed RLS for
  everyone and looked exactly like a real cross-tenant leak in the test
  output until diagnosed. The fix (`deploy/dev/pgbouncer-userlist.txt` +
  omitting a fixed `DATABASES_USER`) makes PgBouncer pass each client's own
  authenticated role through to Postgres, matching Supabase's real per-role
  Supavisor connection shape. This is recorded here because it is exactly
  the class of pooler-configuration mistake this PoC exists to catch.

### What remains unproven

Supavisor (Supabase's actual transaction-mode pooler) is a different
implementation from PgBouncer. TLS termination behavior, connection draining
under real production load, and Supavisor-specific release-back-to-pool
timing were **not** exercised here and remain unproven pending a real
Supabase staging project. Per design.md's explicit fallback: if a future run
against the real Supavisor pooler reveals leakage or unreliable `SET LOCAL`
behavior, fall back to session-mode or the direct (non-pooled) connection
port, with the same explicit-transaction-scoping code unchanged.

### Chosen connection mode

**Transaction-mode pooling accepted** for staging, pending re-validation of
this exact test against the real Supabase Supavisor endpoint before
production sign-off. `ConnectionStrings__Commerce` points at the pooler
endpoint (Supabase port `6543`) in each environment's Railway variables.

### Running the PoC locally

```bash
docker compose -f deploy/dev/compose.yaml up -d
dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --filter "PoolerScopingTests|PostgresCloudInboxStoreTests"
```

If `deploy/dev/compose.yaml` is not running, these tests print a `SKIPPED`
line naming the unreachable target and return without asserting pass/fail —
they never silently report false confidence, and they never hard-fail a
runner that was never given the infrastructure to begin with.

## Unit 5 — One-time-per-environment schema/RLS migration

`deploy/db/migrations/0001_init_rls.sql` is the repo-owned, idempotent
migration that must be applied ONCE per real environment (staging now,
production later) directly against that environment's own Supabase project —
never auto-applied by Cloud.Api. `Program.cs`'s `/health/ready` only
*verifies* the table/RLS/policy already exist and fails readiness otherwise
(design.md "Schema/RLS application").

### Applying the migration

1. In the Supabase dashboard for the target project, copy the **direct**
   (non-pooled, port `5432`) Postgres connection string — not the transaction
   pooler (`6543`) one used at runtime by Cloud.Api. Applying DDL through the
   pooler is unnecessary and the direct connection is the standard tool for
   one-off admin operations.
2. Generate a fresh, strong password for the `app_runtime` role (a password
   manager or `openssl rand -base64 32`); this becomes a Railway environment
   secret in the next step, never a repo commit.
3. Substitute the migration's `__APP_RUNTIME_PASSWORD__` placeholder with
   that password and pipe the result into `psql`, without ever writing the
   substituted file to disk:

   ```bash
   # macOS/Linux/WSL:
   sed "s/__APP_RUNTIME_PASSWORD__/$APP_RUNTIME_PASSWORD/" \
     deploy/db/migrations/0001_init_rls.sql \
     | psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres"
   ```

   ```powershell
   # Windows PowerShell:
   (Get-Content deploy/db/migrations/0001_init_rls.sql) `
     -replace '__APP_RUNTIME_PASSWORD__', $env:APP_RUNTIME_PASSWORD |
     psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres"
   ```

4. Confirm success: `psql ... -c "SELECT rolname FROM pg_roles WHERE rolname = 'app_runtime';"`
   returns one row, and `SELECT polname FROM pg_policies WHERE tablename = 'sync_inbox';`
   returns `sync_inbox_tenant_isolation`.
5. Re-running the same command later (e.g. to rotate the password, or as a
   drift check) is safe — every statement in the migration is idempotent.

The `app_runtime` connection string (`Host=<pooler-host>;Port=6543;Database=postgres;Username=app_runtime;Password=<the-password-from-step-2>`)
becomes that environment's `ConnectionStrings__Commerce` Railway variable —
see `deploy/staging-runbook.md` for the full per-environment provisioning
checklist and the complete list of required Railway variables.

### commerce-user-credentials — `0002_users.sql`

`deploy/db/migrations/0002_users.sql` adds the `users` and `user_directory`
tables (real credential persistence, replacing the walking-skeleton sign-in).
It has NO password placeholder to substitute — `app_runtime` already exists
from `0001` — so it is applied directly:

```bash
psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres" \
  -f deploy/db/migrations/0002_users.sql
```

**Migrate-before-deploy ordering, same as `0001`**: apply `0002_users.sql`
to the target environment's database BEFORE deploying the Cloud.Api image
that expects it. `/health/ready` verifies `users`/`user_directory`
(FORCE RLS + policies) in addition to `sync_inbox`, so a deploy that runs
ahead of the migration fails closed at readiness rather than serving
requests against a missing schema. After migrating, deploy, then read the
one-time bootstrap token from `railway logs` (`POST /account/bootstrap/request-token`
followed by `POST /account/bootstrap` — see
`openspec/changes/commerce-user-credentials/design.md` "Data Flow") to
create the first admin user and confirm sign-in.

Idempotency was confirmed locally by applying `0002_users.sql` twice against
`deploy/dev/compose.yaml`'s Postgres container — the second apply is a clean
no-op (`CREATE TABLE IF NOT EXISTS`, `DROP POLICY IF EXISTS` before
`CREATE POLICY`), exactly like `0001`.

### commerce-organization-persistence — `0003_organizations_branches.sql`

`deploy/db/migrations/0003_organizations_branches.sql` adds the
`organizations` and `branches` tables — the persisted tenancy roots that
`/account/bootstrap` now creates transactionally alongside the first admin.
It has NO password placeholder to substitute — `app_runtime` already exists
from `0001` — so it is applied directly:

```bash
psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres" \
  -f deploy/db/migrations/0003_organizations_branches.sql
```

**Migrate-before-deploy ordering, same as `0001`/`0002`**: apply
`0003_organizations_branches.sql` to the target environment's database
BEFORE deploying the Cloud.Api image that expects it. `/health/ready`
verifies `organizations`/`branches` (FORCE RLS + policies) in addition to
`sync_inbox`/`users`/`user_directory`, so a deploy that runs ahead of the
migration fails closed at readiness rather than serving requests against a
missing schema.

Idempotency was confirmed locally by applying `0003_organizations_branches.sql`
twice against `deploy/dev/compose.yaml`'s Postgres container — the second
apply is a clean no-op, exactly like `0001`/`0002`.

## Local full stack via Docker Compose

`deploy/dev/compose.yaml` gained a `full` profile (Unit 5) that also
containerizes Cloud.Api, building the same repo-root `Dockerfile` Unit 2
already created, alongside the existing `postgres` + `pgbouncer` services:

```bash
docker compose -f deploy/dev/compose.yaml --profile full up
```

This serves Cloud.Api (with the SPA it embeds in `wwwroot`) at
`http://localhost:8080`, backed by the same containerized Postgres, with zero
`dotnet run` / `npm run dev` needed. The default (no `--profile full`)
behavior — Postgres + PgBouncer only, for the fast `dotnet run` inner loop —
is unchanged; `full` is strictly additive.

Commerce.Pos.Windows never requires Docker: it only needs an already-running
Cloud.Api (local, containerized, or staging) to sync against.
