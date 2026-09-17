-- Development/reference PostgreSQL RLS policy for the cloud sync inbox.
--
-- This is the REAL production policy shape (ADR-002 / design.md: tenant keys,
-- claim-derived filters, RLS with default-deny, non-owner runtime role). It
-- is applied automatically by deploy/dev/compose.yaml for local/dev
-- validation and CI RLS runs when a real PostgreSQL instance is available.
--
-- `Commerce.Cloud.Api.CloudInboxStore` proves the same deny/allow semantics
-- in-memory for environments (such as this apply session) where no live
-- PostgreSQL instance is reachable. Wiring the real Npgsql-backed adapter
-- against this schema is tracked as follow-up production work.

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

-- Row Level Security defaults to deny once enabled: with FORCE and no policy,
-- even the table owner gets zero rows back until an explicit policy exists.
ALTER TABLE sync_inbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE sync_inbox FORCE ROW LEVEL SECURITY;

-- Non-owner runtime role: the application connects as this role, never as
-- the table owner, so RLS cannot be bypassed by owner privilege.
--
-- LOGIN + a fixed password here is a LOCAL-DEV-ONLY convenience so
-- PostgresCloudInboxStoreTests / PoolerScopingTests can connect as this role
-- directly against `deploy/dev/compose.yaml`. Each real environment
-- (staging/production) provisions its OWN `app_runtime` password as a
-- Railway/Supabase secret per deploy/README.md — never this literal value.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_runtime') THEN
        CREATE ROLE app_runtime WITH LOGIN PASSWORD 'dev-only-password';
    ELSE
        ALTER ROLE app_runtime WITH LOGIN PASSWORD 'dev-only-password';
    END IF;
END
$$;

REVOKE ALL ON sync_inbox FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON sync_inbox TO app_runtime;

-- Tenant isolation policy: every row read/write is scoped to the
-- authenticated claim's organization, never a caller-submitted value.
--
-- NULLIF(..., ''): discovered during Unit 2's pooler PoC. Once a session has
-- ever run a transaction-local `set_config('app.current_org_id', v, true)`
-- (is_local = true, i.e. `SET LOCAL` semantics) and that transaction
-- committed, PostgreSQL does NOT revert the custom placeholder GUC back to
-- "unset"/NULL for the rest of the session — it reverts to an empty string
-- ''. On a connection-pooled/reused session (exactly PgBouncer transaction
-- pooling's shape), a query that forgets to re-apply tenant scope before
-- running would otherwise hit `''::uuid`, which RAISES A POSTGRES ERROR
-- rather than filtering to zero rows. `PostgresCloudInboxStore` always sets
-- scope as the first statement of every transaction, so this never happens
-- on the real code path — but the policy itself is hardened here so an
-- unscoped query fails CLOSED (zero rows) instead of failing with a
-- confusing cast error, regardless of caller discipline.
CREATE POLICY sync_inbox_tenant_isolation ON sync_inbox
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Commerce user credentials (deploy/db/migrations/0002_users.sql), hand-synced
-- verbatim here per the existing 0001/init-rls.sql convention. See 0002 for
-- the asymmetric user_directory rationale.

CREATE TABLE IF NOT EXISTS users (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    email           text NOT NULL,
    password_hash   text NOT NULL,
    branch_scope    uuid[] NOT NULL DEFAULT '{}',
    roles           jsonb NOT NULL DEFAULT '[]',
    is_revoked      boolean NOT NULL DEFAULT false,
    created_at_utc  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS users_org_email_unique ON users (organization_id, email);

CREATE TABLE IF NOT EXISTS user_directory (
    email_normalized text PRIMARY KEY,
    organization_id  uuid NOT NULL,
    user_id          uuid NOT NULL
);

ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE ROW LEVEL SECURITY;
ALTER TABLE user_directory ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_directory FORCE ROW LEVEL SECURITY;

REVOKE ALL ON users, user_directory FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON users TO app_runtime;
GRANT SELECT, INSERT ON user_directory TO app_runtime;

DROP POLICY IF EXISTS users_tenant_isolation ON users;
CREATE POLICY users_tenant_isolation ON users
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS user_directory_lookup ON user_directory;
CREATE POLICY user_directory_lookup ON user_directory
    USING (true)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Commerce organization/branch persistence
-- (deploy/db/migrations/0003_organizations_branches.sql), hand-synced
-- verbatim here per the existing 0001/0002/init-rls.sql convention. Note the
-- organizations policy compares `id`, not `organization_id` — the
-- organization row IS the tenant; branches carries its own organization_id
-- for a direct-column-comparison policy, symmetric with users_tenant_isolation.

CREATE TABLE IF NOT EXISTS organizations (
    id         uuid PRIMARY KEY,
    name       text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS branches (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS branches_org_name_unique ON branches (organization_id, name);
CREATE INDEX IF NOT EXISTS branches_organization_id_idx ON branches (organization_id);

ALTER TABLE organizations ENABLE ROW LEVEL SECURITY;
ALTER TABLE organizations FORCE ROW LEVEL SECURITY;
ALTER TABLE branches      ENABLE ROW LEVEL SECURITY;
ALTER TABLE branches      FORCE ROW LEVEL SECURITY;

REVOKE ALL ON organizations, branches FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON organizations TO app_runtime;
GRANT SELECT, INSERT, UPDATE ON branches      TO app_runtime;

DROP POLICY IF EXISTS organizations_tenant_isolation ON organizations;
CREATE POLICY organizations_tenant_isolation ON organizations
    USING (id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS branches_tenant_isolation ON branches;
CREATE POLICY branches_tenant_isolation ON branches
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Commerce POS installation identity (deploy/db/migrations/0004_device_credentials.sql),
-- hand-synced verbatim here per the existing 0001/0002/0003/init-rls.sql
-- convention. See 0004 for the asymmetric RLS rationale (same class of
-- problem as user_directory_lookup: verification resolves the credential
-- BEFORE any tenant scope is known).

CREATE TABLE IF NOT EXISTS device_credentials (
    token_hash             text PRIMARY KEY,
    id                     uuid NOT NULL UNIQUE,
    organization_id        uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    branch_id              uuid NOT NULL REFERENCES branches (id) ON DELETE CASCADE,
    installation_id        uuid NOT NULL,
    issued_to_user_id      uuid NOT NULL,
    replaces_credential_id uuid NULL,
    is_revoked             boolean NOT NULL DEFAULT false,
    issued_at              timestamptz NOT NULL DEFAULT now(),
    revoked_at             timestamptz NULL
);
CREATE INDEX IF NOT EXISTS device_credentials_installation_idx
    ON device_credentials (installation_id) WHERE NOT is_revoked;

ALTER TABLE device_credentials ENABLE ROW LEVEL SECURITY;
ALTER TABLE device_credentials FORCE ROW LEVEL SECURITY;

REVOKE ALL ON device_credentials FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON device_credentials TO app_runtime;

DROP POLICY IF EXISTS device_credentials_lookup ON device_credentials;
CREATE POLICY device_credentials_lookup ON device_credentials
    FOR SELECT USING (true);

DROP POLICY IF EXISTS device_credentials_issue ON device_credentials;
CREATE POLICY device_credentials_issue ON device_credentials
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS device_credentials_revoke ON device_credentials;
CREATE POLICY device_credentials_revoke ON device_credentials
    FOR UPDATE USING (true) WITH CHECK (is_revoked);
