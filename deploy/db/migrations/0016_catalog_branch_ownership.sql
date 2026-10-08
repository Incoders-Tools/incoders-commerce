-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- B7 U4 (organization-persistence spec "Branch-Owned Business Data",
-- catalog-item-identification spec "Branch-Owned Catalog"): products and
-- presentations move from organization-owned to branch-owned. Catalog copy
-- between branches (catalog-item-identification "Copying Catalog Between
-- Branches") is explicitly OUT of scope here — that is B7 U5b.
--
-- APPLIED AFTER: 0015_organization_branding.sql. Touches `products` and
-- `presentations` from 0009 (additively: new column, new/replaced indexes,
-- replaced RLS policy) and `branches` from 0003 (additively: one redundant
-- unique key, the same shape 0014 added to `price_lists`/
-- `rate_component_sets`). No row, grant, or unrelated policy changes.
--
-- INVERSE (rollback), shipped as comments — NOT executed by this file:
--   ALTER TABLE presentations DROP CONSTRAINT presentations_branch_org_fk;
--   ALTER TABLE products      DROP CONSTRAINT products_branch_org_fk;
--   ALTER TABLE presentations ALTER COLUMN branch_id DROP NOT NULL;
--   ALTER TABLE products      ALTER COLUMN branch_id DROP NOT NULL;
--   DROP INDEX presentations_org_branch_code_uk;
--   CREATE UNIQUE INDEX presentations_org_code_uk
--       ON presentations (organization_id, identification_code) WHERE identification_code IS NOT NULL;
--   ALTER TABLE presentations DROP COLUMN branch_id;
--   ALTER TABLE products      DROP COLUMN branch_id;
--   ALTER TABLE branches      DROP CONSTRAINT branches_org_scoped_uk;
--   -- RLS policies revert to the 0009 shape (organization_id only) --
--   see that file's DROP POLICY/CREATE POLICY statements.
-- Rolling back is lossy the moment two branches of one organization hold
-- catalog rows: a rollback has nowhere left to put the second branch's
-- rows without colliding on identification code or losing the branch
-- distinction entirely. Prefer forward-fix after first real multi-branch use.

-- ===========================================================================
-- 1. The referenced key: branches (organization_id, id)
-- ===========================================================================
--
-- Same shape as 0014's `price_lists_org_scoped_uk` / VAR
-- `rate_component_sets_org_scoped_uk`: redundant with `branches`' own
-- primary key (`id`) by design — a composite foreign key needs a unique
-- constraint over exactly its target columns.

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk'
    ) THEN
        ALTER TABLE branches
            ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

-- ===========================================================================
-- 2. New nullable column, backfilled, then NOT NULL
-- ===========================================================================
--
-- design.md / spec "The migration that adds branch_id MUST backfill every
-- existing row into its organization's earliest-created branch, and for an
-- organization that owns rows but has no branch MUST first create a branch
-- named 'Main'."

ALTER TABLE products      ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE presentations ADD COLUMN IF NOT EXISTS branch_id uuid NULL;

-- 2a. Any organization that owns a product or presentation but has NO
-- branch at all gets one, named "Main", before backfill runs — otherwise
-- the backfill below would have nothing to point those rows at.
INSERT INTO branches (id, organization_id, name)
SELECT gen_random_uuid(), missing.organization_id, 'Main'
FROM (
    SELECT DISTINCT organization_id FROM products
    UNION
    SELECT DISTINCT organization_id FROM presentations
) AS missing
WHERE NOT EXISTS (
    SELECT 1 FROM branches b WHERE b.organization_id = missing.organization_id
);

-- 2b. Backfill every existing row into its organization's EARLIEST-CREATED
-- branch (ties broken by id for determinism).
UPDATE products p
SET branch_id = earliest.id
FROM (
    SELECT DISTINCT ON (organization_id) organization_id, id
    FROM branches
    ORDER BY organization_id, created_at, id
) AS earliest
WHERE earliest.organization_id = p.organization_id
  AND p.branch_id IS NULL;

UPDATE presentations pr
SET branch_id = earliest.id
FROM (
    SELECT DISTINCT ON (organization_id) organization_id, id
    FROM branches
    ORDER BY organization_id, created_at, id
) AS earliest
WHERE earliest.organization_id = pr.organization_id
  AND pr.branch_id IS NULL;

ALTER TABLE products      ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE presentations ALTER COLUMN branch_id SET NOT NULL;

-- ===========================================================================
-- 3. Tenant-composite references onto branches (organization_id, id)
-- ===========================================================================
--
-- Same rationale as 0014 section 1: RLS filters SELECT, not a foreign-key
-- check, so a plain `branch_id uuid REFERENCES branches (id)` would let a
-- row scoped to organization A reference organization B's branch. Making
-- the tenant part of the key closes that the same way 0014 closed it for
-- price lists / rate component sets.

ALTER TABLE products      DROP CONSTRAINT IF EXISTS products_branch_id_fkey;
ALTER TABLE presentations DROP CONSTRAINT IF EXISTS presentations_branch_id_fkey;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'products_branch_org_fk'
    ) THEN
        ALTER TABLE products
            ADD CONSTRAINT products_branch_org_fk
            FOREIGN KEY (organization_id, branch_id)
            REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'presentations_branch_org_fk'
    ) THEN
        ALTER TABLE presentations
            ADD CONSTRAINT presentations_branch_org_fk
            FOREIGN KEY (organization_id, branch_id)
            REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS products_branch_idx      ON products (branch_id);
CREATE INDEX IF NOT EXISTS presentations_branch_idx ON presentations (branch_id);

-- ===========================================================================
-- 4. Identification-code uniqueness becomes PER BRANCH
-- ===========================================================================
--
-- catalog-item-identification spec "Branch-Owned Catalog": "the same code
-- MAY exist in two branches of one organization." The old org-scoped
-- partial unique index would have refused that; replaced with a
-- branch-scoped one, same partial shape (an unlabelled presentation stays
-- unconstrained).

DROP INDEX IF EXISTS presentations_org_code_uk;
CREATE UNIQUE INDEX IF NOT EXISTS presentations_org_branch_code_uk
    ON presentations (organization_id, branch_id, identification_code)
    WHERE identification_code IS NOT NULL;

-- ===========================================================================
-- 5. Row-level security: require BOTH organization AND branch to match
-- ===========================================================================
--
-- organization-persistence spec "Branch-Owned Business Data": "Row-level
-- security on each branch-owned table MUST require both organization_id
-- and branch_id to match the transaction's scoped organization and
-- branch, ... so a transaction without a scoped branch reads and writes
-- nothing (fail-closed)." `NULLIF(current_setting('app.current_branch_id',
-- true), '')::uuid` is NULL when no branch is selected, and `branch_id =
-- NULL` is never true for any real row — exactly the fail-closed shape the
-- spec calls for, with no separate "no branch selected" case to write.

DROP POLICY IF EXISTS products_tenant_isolation ON products;
CREATE POLICY products_tenant_isolation ON products
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS presentations_tenant_isolation ON presentations;
CREATE POLICY presentations_tenant_isolation ON presentations
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );
