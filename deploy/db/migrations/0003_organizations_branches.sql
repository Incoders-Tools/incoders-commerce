-- Repo-owned, idempotent per-environment migration.
--
-- Adds `organizations` and `branches` — the tenancy roots that
-- `commerce-organization-persistence` design.md fulfils. Append-only:
-- `0001_init_rls.sql` and `0002_users.sql` are NOT modified. `app_runtime`
-- already exists from 0001; this file only extends its grants to the two new
-- tables.
--
-- Both tables follow 0001/0002's exact symmetric tenant-isolation policy
-- shape, including the `NULLIF(..., '')::uuid` pooler-safety hardening.
-- NEVER regress that fix — see 0001's comment for the pooler GUC-reversion
-- rationale.
--
-- IMPORTANT shape difference: `organizations_tenant_isolation` compares
-- `id` (the organization row itself IS the tenant), NOT `organization_id`.
-- `branches_tenant_isolation` is symmetric with `users_tenant_isolation`,
-- comparing its own denormalized `organization_id` column directly — no
-- subquery through `organizations` (design.md "branches RLS policy").
--
-- Deliberately NOT touching `users`, `user_directory`, or `sync_inbox`: no
-- FK retrofit onto existing tables in this change (design.md "No FK
-- retrofit onto existing tables").

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
GRANT SELECT, INSERT, UPDATE ON organizations TO app_runtime;  -- app_runtime already exists from 0001
GRANT SELECT, INSERT, UPDATE ON branches      TO app_runtime;

-- Symmetric isolation, identical in shape to users_tenant_isolation, including
-- the NULLIF(..., '')::uuid pooler-safety hardening from 0001. NEVER regress it.
-- Note: this policy compares `id`, not `organization_id` — the organization
-- row IS the tenant.
DROP POLICY IF EXISTS organizations_tenant_isolation ON organizations;
CREATE POLICY organizations_tenant_isolation ON organizations
    USING (id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- branches carries its OWN organization_id so its policy is a direct column
-- comparison, never a subquery through organizations.
DROP POLICY IF EXISTS branches_tenant_isolation ON branches;
CREATE POLICY branches_tenant_isolation ON branches
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
