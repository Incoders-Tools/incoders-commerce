-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- branch-discount-pin spec: each branch may have ONE shared discount PIN that
-- authorizes POS discounts. Only a slow salted hash is stored (PBKDF2-SHA256,
-- parameters kept beside the hash so they can be strengthened later), with a
-- monotonically increasing version and the time/actor of the last rotation.
-- The PIN itself is never stored.
--
-- APPLIED AFTER: 0018_catalog_categories.sql.
--
-- BRANCH-owned data, so the policy compares BOTH the organization and the
-- selected branch and fails closed when either is unset (same shape as
-- `products` in 0016): a scope on another branch, another organization, or no
-- branch at all sees no row.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment — NOT executed by this file:
--   DROP TABLE branch_discount_pins;

BEGIN;

CREATE TABLE IF NOT EXISTS branch_discount_pins (
    branch_id       uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    algorithm       text NOT NULL CHECK (algorithm = 'pbkdf2-sha256'),
    iterations      integer NOT NULL CHECK (iterations >= 100000),
    salt            bytea NOT NULL CHECK (octet_length(salt) >= 16),
    pin_hash        bytea NOT NULL CHECK (octet_length(pin_hash) >= 16),
    version         bigint NOT NULL CHECK (version >= 1),
    rotated_at_utc  timestamptz NOT NULL DEFAULT now(),
    rotated_by      uuid NOT NULL,
    -- Tenant-composite reference (0014/0016): a plain branch_id reference would
    -- let a row point at another organization's branch, because RLS filters
    -- reads but not foreign-key checks.
    CONSTRAINT branch_discount_pins_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

ALTER TABLE branch_discount_pins ENABLE ROW LEVEL SECURITY;
ALTER TABLE branch_discount_pins FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON branch_discount_pins FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON branch_discount_pins TO app_runtime;

DROP POLICY IF EXISTS branch_discount_pins_tenant_isolation ON branch_discount_pins;
CREATE POLICY branch_discount_pins_tenant_isolation ON branch_discount_pins
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

COMMIT;
