-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- B7 U5 (organization-persistence spec "Branch-Owned Business Data",
-- price-list-management spec "Branch-Owned Price Lists", supplier-price-import
-- spec "Branch-Owned Supplier Mappings And Imports"): price_lists,
-- price_list_entries, rate_component_sets, rate_components,
-- supplier_price_mappings, price_import_batches and price_import_rows move
-- from organization-owned to branch-owned — the same shape 0016 gave
-- products/presentations.
--
-- APPLIED AFTER: 0016_catalog_branch_ownership.sql. Touches `price_lists`,
-- `price_list_entries` (0009), `rate_component_sets`, `rate_components`
-- (0013/0014), `supplier_price_mappings`, `price_import_batches`,
-- `price_import_rows` (0009 Part C) additively: new column, new/replaced
-- indexes, replaced RLS policy. Also adds a composite unique key on
-- `presentations (branch_id, id)` (from 0016) so `price_list_entries` can
-- carry a tenant+branch-composite reference onto it, mirroring 0014's
-- `price_lists_org_scoped_uk` pattern one level down.
--
-- INVERSE (rollback), shipped as comments — NOT executed by this file:
--   ALTER TABLE price_import_rows       DROP CONSTRAINT price_import_rows_branch_org_fk;
--   ALTER TABLE price_import_batches    DROP CONSTRAINT price_import_batches_branch_org_fk;
--   ALTER TABLE price_import_batches    DROP CONSTRAINT price_import_batches_mapping_branch_fk;
--   ALTER TABLE supplier_price_mappings DROP CONSTRAINT supplier_price_mappings_branch_org_fk;
--   ALTER TABLE rate_components         DROP CONSTRAINT rate_components_branch_org_fk;
--   ALTER TABLE rate_components         DROP CONSTRAINT rate_components_set_branch_fk;
--   ALTER TABLE rate_component_sets     DROP CONSTRAINT rate_component_sets_branch_org_fk;
--   ALTER TABLE rate_component_sets     DROP CONSTRAINT rate_component_sets_price_list_branch_fk;
--   ALTER TABLE price_list_entries      DROP CONSTRAINT price_list_entries_branch_org_fk;
--   ALTER TABLE price_list_entries      DROP CONSTRAINT price_list_entries_price_list_branch_fk;
--   ALTER TABLE price_list_entries      DROP CONSTRAINT price_list_entries_presentation_branch_fk;
--   ALTER TABLE price_lists             DROP CONSTRAINT price_lists_branch_org_fk;
--   -- drop every branch_id NOT NULL / DROP COLUMN, restore the pre-0017
--   -- indexes and RLS policies shown in 0009/0013/0014's own files --
-- Rolling back is lossy the moment two branches of one organization hold
-- pricing rows: prefer forward-fix after first real multi-branch use, same
-- caveat as 0016.

-- ===========================================================================
-- 1. Referenced keys: presentations (branch_id, id), needed as an FK target
-- ===========================================================================

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'presentations_branch_scoped_uk'
    ) THEN
        ALTER TABLE presentations
            ADD CONSTRAINT presentations_branch_scoped_uk UNIQUE (branch_id, id);
    END IF;
END $$;

-- ===========================================================================
-- 2. New nullable columns, backfilled, then NOT NULL
-- ===========================================================================
--
-- Same two-step shape as 0016: any organization that owns a pricing row but
-- has NO branch at all gets one named "Main" first, then every row backfills
-- into its organization's EARLIEST-CREATED branch.

ALTER TABLE price_lists             ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE price_list_entries      ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE rate_component_sets     ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE rate_components         ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE supplier_price_mappings ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE price_import_batches    ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE price_import_rows       ADD COLUMN IF NOT EXISTS branch_id uuid NULL;

INSERT INTO branches (id, organization_id, name)
SELECT gen_random_uuid(), missing.organization_id, 'Main'
FROM (
    SELECT DISTINCT organization_id FROM price_lists
    UNION SELECT DISTINCT organization_id FROM price_list_entries
    UNION SELECT DISTINCT organization_id FROM rate_component_sets
    UNION SELECT DISTINCT organization_id FROM rate_components
    UNION SELECT DISTINCT organization_id FROM supplier_price_mappings
    UNION SELECT DISTINCT organization_id FROM price_import_batches
    UNION SELECT DISTINCT organization_id FROM price_import_rows
) AS missing
WHERE NOT EXISTS (
    SELECT 1 FROM branches b WHERE b.organization_id = missing.organization_id
);

UPDATE price_lists t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

UPDATE price_list_entries t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

UPDATE rate_component_sets t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

UPDATE rate_components t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

UPDATE supplier_price_mappings t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

UPDATE price_import_batches t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

UPDATE price_import_rows t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

ALTER TABLE price_lists             ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE price_list_entries      ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE rate_component_sets     ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE rate_components         ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE supplier_price_mappings ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE price_import_batches    ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE price_import_rows       ALTER COLUMN branch_id SET NOT NULL;

-- ===========================================================================
-- 3. Tenant-composite references onto branches (organization_id, id)
-- ===========================================================================

CREATE INDEX IF NOT EXISTS price_lists_branch_idx             ON price_lists (branch_id);
CREATE INDEX IF NOT EXISTS price_list_entries_branch_idx      ON price_list_entries (branch_id);
CREATE INDEX IF NOT EXISTS rate_component_sets_branch_idx     ON rate_component_sets (branch_id);
CREATE INDEX IF NOT EXISTS rate_components_branch_idx         ON rate_components (branch_id);
CREATE INDEX IF NOT EXISTS supplier_price_mappings_branch_idx ON supplier_price_mappings (branch_id);
CREATE INDEX IF NOT EXISTS price_import_batches_branch_idx    ON price_import_batches (branch_id);
CREATE INDEX IF NOT EXISTS price_import_rows_branch_idx       ON price_import_rows (branch_id);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_lists_branch_org_fk') THEN
        ALTER TABLE price_lists
            ADD CONSTRAINT price_lists_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_component_sets_branch_org_fk') THEN
        ALTER TABLE rate_component_sets
            ADD CONSTRAINT rate_component_sets_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_components_branch_org_fk') THEN
        ALTER TABLE rate_components
            ADD CONSTRAINT rate_components_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'supplier_price_mappings_branch_org_fk') THEN
        ALTER TABLE supplier_price_mappings
            ADD CONSTRAINT supplier_price_mappings_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_import_batches_branch_org_fk') THEN
        ALTER TABLE price_import_batches
            ADD CONSTRAINT price_import_batches_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_import_rows_branch_org_fk') THEN
        ALTER TABLE price_import_rows
            ADD CONSTRAINT price_import_rows_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;
END $$;

-- price_list_entries carries NO organization+branch FK to `branches` of its
-- own: its `(branch_id, price_list_id)` reference below already pins it to a
-- price list that itself references `branches (organization_id, id)`, and a
-- second direct reference would be redundant, not additionally safe.

-- ===========================================================================
-- 4. Same-branch consistency: entries and rate sets reference a price list
--    (and, for entries, a presentation) of the SAME branch
-- ===========================================================================
--
-- price-list-management spec "Branch-Owned Price Lists": "A price entry or
-- rate component set MUST reference a price list and presentation of the
-- same branch." Same technique as section 3: make the branch part of the
-- foreign key so the database refuses a cross-branch reference outright.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_lists_branch_scoped_uk') THEN
        ALTER TABLE price_lists ADD CONSTRAINT price_lists_branch_scoped_uk UNIQUE (branch_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_component_sets_branch_scoped_uk') THEN
        ALTER TABLE rate_component_sets ADD CONSTRAINT rate_component_sets_branch_scoped_uk UNIQUE (branch_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'supplier_price_mappings_branch_scoped_uk') THEN
        ALTER TABLE supplier_price_mappings ADD CONSTRAINT supplier_price_mappings_branch_scoped_uk UNIQUE (branch_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_import_batches_branch_scoped_uk') THEN
        ALTER TABLE price_import_batches ADD CONSTRAINT price_import_batches_branch_scoped_uk UNIQUE (branch_id, id);
    END IF;
END $$;

ALTER TABLE price_list_entries  DROP CONSTRAINT IF EXISTS price_list_entries_price_list_id_fkey;
ALTER TABLE price_list_entries  DROP CONSTRAINT IF EXISTS price_list_entries_presentation_id_fkey;
ALTER TABLE rate_component_sets DROP CONSTRAINT IF EXISTS rate_component_sets_price_list_org_fk;
ALTER TABLE rate_components     DROP CONSTRAINT IF EXISTS rate_components_set_org_fk;
ALTER TABLE price_import_batches DROP CONSTRAINT IF EXISTS price_import_batches_supplier_mapping_id_fkey;
ALTER TABLE price_import_rows    DROP CONSTRAINT IF EXISTS price_import_rows_batch_id_fkey;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_list_entries_price_list_branch_fk') THEN
        ALTER TABLE price_list_entries
            ADD CONSTRAINT price_list_entries_price_list_branch_fk
            FOREIGN KEY (branch_id, price_list_id) REFERENCES price_lists (branch_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_list_entries_presentation_branch_fk') THEN
        ALTER TABLE price_list_entries
            ADD CONSTRAINT price_list_entries_presentation_branch_fk
            FOREIGN KEY (branch_id, presentation_id) REFERENCES presentations (branch_id, id) ON DELETE CASCADE;
    END IF;

    -- MATCH SIMPLE (the default): price_list_id IS NULL for an
    -- organization/branch default set skips this check entirely, exactly
    -- like 0014's rate_component_sets_price_list_org_fk did for the
    -- organization-only shape.
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_component_sets_price_list_branch_fk') THEN
        ALTER TABLE rate_component_sets
            ADD CONSTRAINT rate_component_sets_price_list_branch_fk
            FOREIGN KEY (branch_id, price_list_id) REFERENCES price_lists (branch_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_components_set_branch_fk') THEN
        ALTER TABLE rate_components
            ADD CONSTRAINT rate_components_set_branch_fk
            FOREIGN KEY (branch_id, set_id) REFERENCES rate_component_sets (branch_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_import_batches_mapping_branch_fk') THEN
        ALTER TABLE price_import_batches
            ADD CONSTRAINT price_import_batches_mapping_branch_fk
            FOREIGN KEY (branch_id, supplier_mapping_id) REFERENCES supplier_price_mappings (branch_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_import_rows_batch_branch_fk') THEN
        ALTER TABLE price_import_rows
            ADD CONSTRAINT price_import_rows_batch_branch_fk
            FOREIGN KEY (branch_id, batch_id) REFERENCES price_import_batches (branch_id, id) ON DELETE CASCADE;
    END IF;
END $$;

-- price_import_rows.presentation_id keeps its original single-column
-- `REFERENCES presentations (id) ON DELETE SET NULL` shape unchanged: a
-- composite `(branch_id, presentation_id)` target with `ON DELETE SET NULL`
-- would null out `branch_id` too, which is NOT NULL. The row's own
-- `branch_id` (matching its batch) already carries the tenant/branch scope
-- this table needs; the presentation match itself was computed by the
-- import endpoint through a branch-scoped catalog lookup at parse time.

-- ===========================================================================
-- 5. Uniqueness rules become PER BRANCH
-- ===========================================================================
--
-- price-list-management spec: "each branch MUST have at most one default
-- price list." supplier-price-import spec (unchanged text, same rule as
-- 0009's per-organization mapping name): now per branch.

DROP INDEX IF EXISTS price_lists_one_default;
CREATE UNIQUE INDEX IF NOT EXISTS price_lists_one_default_per_branch
    ON price_lists (organization_id, branch_id) WHERE is_default;

DROP INDEX IF EXISTS supplier_price_mappings_org_name_uk;
CREATE UNIQUE INDEX IF NOT EXISTS supplier_price_mappings_branch_name_uk
    ON supplier_price_mappings (organization_id, branch_id, supplier_name);

-- rate_component_sets: "one publication per owner per day" becomes per
-- branch too — the list-owned index already implies the branch via the FK
-- added in section 4, but the branch is folded into the key itself for the
-- same defence-in-depth reason 0014 gave for the organization column.
DROP INDEX IF EXISTS rate_component_sets_list_day_uk;
CREATE UNIQUE INDEX IF NOT EXISTS rate_component_sets_list_day_uk
    ON rate_component_sets (organization_id, branch_id, price_list_id, effective_from)
    WHERE price_list_id IS NOT NULL;

DROP INDEX IF EXISTS rate_component_sets_org_default_day_uk;
CREATE UNIQUE INDEX IF NOT EXISTS rate_component_sets_branch_default_day_uk
    ON rate_component_sets (organization_id, branch_id, effective_from)
    WHERE price_list_id IS NULL;

-- ===========================================================================
-- 6. Row-level security: require BOTH organization_id AND branch_id
-- ===========================================================================

DROP POLICY IF EXISTS price_lists_tenant_isolation ON price_lists;
CREATE POLICY price_lists_tenant_isolation ON price_lists
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS price_list_entries_tenant_isolation ON price_list_entries;
CREATE POLICY price_list_entries_tenant_isolation ON price_list_entries
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS rate_component_sets_tenant_isolation ON rate_component_sets;
CREATE POLICY rate_component_sets_tenant_isolation ON rate_component_sets
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS rate_components_tenant_isolation ON rate_components;
CREATE POLICY rate_components_tenant_isolation ON rate_components
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS supplier_price_mappings_tenant_isolation ON supplier_price_mappings;
CREATE POLICY supplier_price_mappings_tenant_isolation ON supplier_price_mappings
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS price_import_batches_tenant_isolation ON price_import_batches;
CREATE POLICY price_import_batches_tenant_isolation ON price_import_batches
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS price_import_rows_tenant_isolation ON price_import_rows;
CREATE POLICY price_import_rows_tenant_isolation ON price_import_rows
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );
