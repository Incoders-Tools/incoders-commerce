-- Repo-owned, idempotent per-environment migration.
--
-- Adds `users` and `user_directory` for real Postgres-backed credential
-- persistence (openspec/changes/commerce-user-credentials/design.md
-- "Interfaces / Contracts"). Append-only: `0001_init_rls.sql` is NOT
-- modified. `app_runtime` already exists from 0001; this file only extends
-- its grants to the two new tables.
--
-- `users` follows 0001's exact symmetric tenant-isolation policy shape,
-- including the `NULLIF(..., '')::uuid` pooler-safety hardening. NEVER
-- regress that fix — see 0001's comment for the pooler GUC-reversion
-- rationale.
--
-- `user_directory` is DELIBERATELY asymmetric: `USING (true)` for reads (the
-- sign-in email -> organization lookup runs before any tenant scope exists)
-- and an org-scoped `WITH CHECK` for writes. It holds no credential material
-- and is never exposed over HTTP directly.

CREATE TABLE IF NOT EXISTS users (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL,                 -- no FK: organizations are not persisted (non-goal)
    email           text NOT NULL,                 -- stored already normalized (lower/trim)
    password_hash   text NOT NULL,
    branch_scope    uuid[] NOT NULL DEFAULT '{}',
    roles           jsonb NOT NULL DEFAULT '[]',   -- [{"name":"...","permissions":<int>}]
    is_revoked      boolean NOT NULL DEFAULT false,
    created_at_utc  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS users_org_email_unique ON users (organization_id, email);

CREATE TABLE IF NOT EXISTS user_directory (
    email_normalized text PRIMARY KEY,             -- globally unique: one email, one account
    organization_id  uuid NOT NULL,
    user_id          uuid NOT NULL
);

ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE ROW LEVEL SECURITY;
ALTER TABLE user_directory ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_directory FORCE ROW LEVEL SECURITY;

REVOKE ALL ON users, user_directory FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON users TO app_runtime;         -- app_runtime already exists from 0001
GRANT SELECT, INSERT ON user_directory TO app_runtime;

DROP POLICY IF EXISTS users_tenant_isolation ON users;
CREATE POLICY users_tenant_isolation ON users
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Deliberately asymmetric: the sign-in email->organization lookup runs BEFORE
-- any org scope exists, so reads are global; writes stay org-scoped. This
-- table holds no credential material and is never exposed over HTTP.
DROP POLICY IF EXISTS user_directory_lookup ON user_directory;
CREATE POLICY user_directory_lookup ON user_directory
    USING (true)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
