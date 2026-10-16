-- Repo-owned, idempotent per-environment migration.
--
-- Adds `platform_admins`, `audit_log`, and the `platform_readonly` login
-- (commerce-role-taxonomy proposal.md "Platform Admin" / "Audit logging" /
-- design.md "Interfaces / Contracts"). Append-only: 0001-0006 are NOT
-- modified.
--
-- `platform_admins` is NOT tenant data (no `organization_id` column at all)
-- and carries deliberately asymmetric, column-scoped grants: SELECT, INSERT,
-- and UPDATE (last_sign_in_at_utc) ONLY — no UPDATE on password_hash/email
-- and no DELETE, because this slice has no platform password-change or
-- deletion flow. The INSERT policy is genesis-only: `NOT EXISTS (SELECT 1
-- FROM platform_admins)` makes a SECOND platform admin structurally
-- unrepresentable, not merely unimplemented.
--
-- `audit_log` is append-only: GRANT INSERT ONLY to app_runtime (no SELECT,
-- no UPDATE, no DELETE). The single INSERT policy lets an org actor write
-- history only for its OWN organization (or NULL, for platform sign-in /
-- genesis, which have no owning organization) — a platform write passes
-- because its transaction is already scoped to the explicit target org
-- before this INSERT runs. Reading audit rows later requires an explicit,
-- separately reviewed policy — see the commented statements below.
--
-- `platform_readonly` is the ONLY cross-organization read capability in the
-- system: a second, least-privilege Postgres LOGIN whose entire privilege
-- set is a column-level `GRANT SELECT (id, name, created_at) ON
-- organizations`, paired with a `TO platform_readonly` policy that applies
-- to nobody else — `app_runtime`'s isolation is completely untouched.
-- `__PLATFORM_READONLY_PASSWORD__` follows 0001's `__APP_RUNTIME_PASSWORD__`
-- placeholder convention: replace immediately before piping, per environment.

CREATE TABLE IF NOT EXISTS platform_admins (
    id                  uuid PRIMARY KEY,
    email               text NOT NULL UNIQUE,   -- normalized (lower/trim)
    password_hash       text NOT NULL,
    created_at_utc      timestamptz NOT NULL DEFAULT now(),
    last_sign_in_at_utc timestamptz NULL
);                                  -- no organization_id: NOT tenant data

CREATE TABLE IF NOT EXISTS audit_log (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at_utc timestamptz NOT NULL DEFAULT now(),
    actor_kind      text NOT NULL,   -- 'org-user' | 'platform-admin'
    actor_id        uuid NOT NULL,
    organization_id uuid NULL,       -- NULL for platform sign-in / genesis
    entity_type     text NOT NULL,   -- 'user' | 'organization' | 'platform-admin'
    entity_id       uuid NOT NULL,
    action          text NOT NULL,   -- 'user.created' | 'user.roles.assigned' | ...
    old_value       jsonb NULL,
    new_value       jsonb NULL
);
CREATE INDEX IF NOT EXISTS audit_log_entity_idx ON audit_log (entity_type, entity_id, occurred_at_utc DESC);
CREATE INDEX IF NOT EXISTS audit_log_org_idx    ON audit_log (organization_id, occurred_at_utc DESC);

ALTER TABLE platform_admins ENABLE ROW LEVEL SECURITY;
ALTER TABLE platform_admins FORCE ROW LEVEL SECURITY;
ALTER TABLE audit_log       ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_log       FORCE ROW LEVEL SECURITY;
REVOKE ALL ON platform_admins, audit_log FROM PUBLIC;

GRANT SELECT, INSERT ON platform_admins TO app_runtime;
GRANT UPDATE (last_sign_in_at_utc) ON platform_admins TO app_runtime;  -- column-scoped
GRANT INSERT ON audit_log TO app_runtime;                              -- append-only; NO SELECT

DROP POLICY IF EXISTS platform_admins_lookup ON platform_admins;
CREATE POLICY platform_admins_lookup ON platform_admins FOR SELECT USING (true);
DROP POLICY IF EXISTS platform_admins_touch ON platform_admins;
CREATE POLICY platform_admins_touch  ON platform_admins FOR UPDATE USING (true) WITH CHECK (true);
-- Genesis-only: a SECOND platform admin is unrepresentable through the app.
DROP POLICY IF EXISTS platform_admins_genesis ON platform_admins;
CREATE POLICY platform_admins_genesis ON platform_admins FOR INSERT
    WITH CHECK (NOT EXISTS (SELECT 1 FROM platform_admins));

-- An org actor can only write history for its OWN organization; a platform
-- write passes because its tx is already scoped to the explicit target org.
DROP POLICY IF EXISTS audit_log_append ON audit_log;
CREATE POLICY audit_log_append ON audit_log FOR INSERT
    WITH CHECK (organization_id IS NULL
                OR organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
-- No SELECT policy and no SELECT grant, by design. Reading audit rows is a
-- future, explicitly reviewed change:
--   GRANT SELECT ON audit_log TO <reader>;
--   CREATE POLICY audit_log_read ON audit_log FOR SELECT TO <reader> USING (...);

-- The ONLY cross-organization read capability in the system.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'platform_readonly') THEN
        CREATE ROLE platform_readonly WITH LOGIN PASSWORD '__PLATFORM_READONLY_PASSWORD__';
    ELSE
        ALTER ROLE platform_readonly WITH LOGIN PASSWORD '__PLATFORM_READONLY_PASSWORD__';
    END IF;
END $$;
GRANT USAGE ON SCHEMA public TO platform_readonly;
GRANT SELECT (id, name, created_at) ON organizations TO platform_readonly;  -- and nothing else, anywhere
DROP POLICY IF EXISTS organizations_platform_read ON organizations;
CREATE POLICY organizations_platform_read ON organizations
    FOR SELECT TO platform_readonly USING (true);   -- TO-scoped: app_runtime unaffected

-- Rollback: DROP POLICY organizations_platform_read ON organizations;
--           DROP OWNED BY platform_readonly; DROP ROLE platform_readonly;
--           DROP TABLE audit_log, platform_admins;
