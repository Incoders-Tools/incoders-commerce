-- Repo-owned, idempotent per-environment migration.
--
-- This is the script that gets run ONCE per REAL environment (staging, and
-- later production) directly against that environment's own Supabase
-- project, via `psql` against the Supabase DIRECT (non-pooled) connection
-- string. See deploy/README.md for the exact command.
--
-- Cloud.Api NEVER applies this DDL at startup: `Program.cs`'s
-- `/health/ready` handler only VERIFIES that the table, FORCE RLS, and the
-- policy already exist and fails readiness otherwise (design.md
-- "Schema/RLS application"). Applying schema changes is an explicit,
-- human-run, per-environment operation — never an automatic side effect of
-- deploying a new API image.
--
-- Promoted from deploy/dev/db/init-rls.sql (which deploy/dev/compose.yaml
-- keeps auto-applying for local dev/CI via docker-entrypoint-initdb.d) — the
-- policy shape below is identical, INCLUDING the `NULLIF(..., '')::uuid`
-- hardening discovered during Unit 2's pooler PoC (see deploy/README.md):
-- once a pooled/reused session has ever committed a transaction-local
-- `set_config('app.current_org_id', v, true)`, PostgreSQL reverts the GUC to
-- `''` (empty string), not NULL, for the rest of that session. Without the
-- `NULLIF` guard, an unscoped query on a reused connection would raise a
-- Postgres cast error instead of failing closed with zero rows. Do not
-- regress this fix.
--
-- Idempotency: every statement below is safe to run multiple times against
-- the same database (`CREATE TABLE IF NOT EXISTS`, a role-existence check,
-- and `DROP POLICY IF EXISTS` before `CREATE POLICY`), so re-running this
-- file after a partial apply or as a routine drift check is always safe.
--
-- Role password handling: the `__APP_RUNTIME_PASSWORD__` placeholder below
-- is NOT a committed secret. deploy/README.md documents replacing it with a
-- freshly generated, environment-specific password immediately before
-- piping this file into `psql` — the placeholder never reaches the target
-- database.

CREATE TABLE IF NOT EXISTS sync_inbox (
    operation_id       uuid PRIMARY KEY,
    organization_id    uuid NOT NULL,
    branch_id          uuid NOT NULL,
    aggregate_id       uuid NOT NULL,
    aggregate_version  bigint NOT NULL,
    actor_id           uuid NOT NULL,
    correlation_id     uuid NOT NULL,
    occurred_at_utc    timestamptz NOT NULL,
    payload_kind       text NOT NULL,
    payload            jsonb NOT NULL,
    status             text NOT NULL DEFAULT 'Pending',
    acknowledged_at_utc timestamptz NULL
);

-- FORCE RLS defaults to deny once enabled: with FORCE and no matching
-- policy, even the table owner gets zero rows back (except real Postgres
-- superusers, which Supabase never grants to application-created roles).
ALTER TABLE sync_inbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE sync_inbox FORCE ROW LEVEL SECURITY;

-- Non-owner runtime role: the application connects as this role, never as
-- the table owner, so RLS cannot be bypassed by owner privilege.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_runtime') THEN
        CREATE ROLE app_runtime WITH LOGIN PASSWORD '__APP_RUNTIME_PASSWORD__';
    ELSE
        ALTER ROLE app_runtime WITH LOGIN PASSWORD '__APP_RUNTIME_PASSWORD__';
    END IF;
END
$$;

REVOKE ALL ON sync_inbox FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON sync_inbox TO app_runtime;

-- Tenant isolation policy: every row read/write is scoped to the
-- authenticated claim's organization, never a caller-submitted value.
DROP POLICY IF EXISTS sync_inbox_tenant_isolation ON sync_inbox;
CREATE POLICY sync_inbox_tenant_isolation ON sync_inbox
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
