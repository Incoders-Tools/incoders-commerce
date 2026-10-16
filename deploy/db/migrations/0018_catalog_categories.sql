-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- catalog-categories spec: product categories become a real,
-- ORGANIZATION-owned table (shared by every branch of the organization),
-- each with a name and an icon key from a fixed set. Until now
-- `products.category_id` (0009) was a bare uuid that referenced nothing, so
-- no category name existed anywhere and the POS had nothing to replicate.
--
-- APPLIED AFTER: 0017_pricing_branch_ownership.sql. Adds `categories`, then
-- rewrites `products.category_id` (0009/0016) and constrains it.
--
-- Products stay BRANCH-owned (0016); categories are ORGANIZATION-owned, so
-- the policy on `categories` compares only `organization_id` (fail-closed
-- when no organization is scoped) — the same shape as `customers` (0008).
--
-- The whole file runs in ONE transaction and can be re-run safely: the table
-- and indexes use IF NOT EXISTS, the backfill only touches organizations and
-- products that still lack a real category, and the foreign key is guarded.
--
-- INVERSE (rollback), shipped as comments — NOT executed by this file:
--   ALTER TABLE products DROP CONSTRAINT products_category_org_fk;
--   DROP TABLE categories;
-- The rewritten `products.category_id` values are not restored (they held
-- meaningless ids that referenced nothing).

BEGIN;

-- ===========================================================================
-- 1. The categories table
-- ===========================================================================
--
-- `icon_key` is the POS/web icon vocabulary; the CHECK keeps it a closed set
-- at the database level. Adding a key later is a forward migration that
-- replaces this constraint.

CREATE TABLE IF NOT EXISTS categories (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    icon_key        text NOT NULL CHECK (icon_key IN (
        'meat', 'poultry', 'fish', 'wine', 'drinks', 'charcoal',
        'grocery', 'cleaning', 'bakery', 'dairy', 'produce', 'generic')),
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    -- Redundant with the primary key on purpose: a composite foreign key from
    -- `products (organization_id, category_id)` needs a unique constraint over
    -- exactly its target columns (same shape as 0014/0016).
    CONSTRAINT categories_org_scoped_uk UNIQUE (organization_id, id)
);

-- Unique name per organization, ignoring case and surrounding whitespace.
CREATE UNIQUE INDEX IF NOT EXISTS categories_org_name_uk
    ON categories (organization_id, lower(btrim(name)));

CREATE INDEX IF NOT EXISTS categories_org_updated ON categories (organization_id, updated_at_utc);

ALTER TABLE categories ENABLE ROW LEVEL SECURITY;
ALTER TABLE categories FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON categories FROM PUBLIC;
-- DELETE is granted (unlike most catalog tables) because an admin can remove
-- an unused category; the foreign key below refuses removal while any
-- product still references it.
GRANT SELECT, INSERT, UPDATE, DELETE ON categories TO app_runtime;

DROP POLICY IF EXISTS categories_tenant_isolation ON categories;
CREATE POLICY categories_tenant_isolation ON categories
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 2. Backfill: one "Sin categoría" per organization that owns products
-- ===========================================================================

INSERT INTO categories (id, organization_id, name, icon_key)
SELECT gen_random_uuid(), owners.organization_id, 'Sin categoría', 'generic'
FROM (SELECT DISTINCT organization_id FROM products) AS owners
WHERE NOT EXISTS (
    SELECT 1 FROM categories c
    WHERE c.organization_id = owners.organization_id
      AND lower(btrim(c.name)) = lower('Sin categoría')
);

-- Point every product whose category_id does not reference a real category of
-- its own organization at that organization's default category, and mark it
-- changed so devices re-sync it with its category.
UPDATE products p
SET category_id = c.id, updated_at_utc = now()
FROM categories c
WHERE c.organization_id = p.organization_id
  AND lower(btrim(c.name)) = lower('Sin categoría')
  AND NOT EXISTS (
      SELECT 1 FROM categories own
      WHERE own.organization_id = p.organization_id AND own.id = p.category_id
  );

-- ===========================================================================
-- 3. Same-organization foreign key from products
-- ===========================================================================
--
-- RLS filters reads, not foreign-key checks, so a plain `REFERENCES
-- categories (id)` would let a product reference another organization's
-- category. Making the organization part of the key closes that (0014/0016).
-- RESTRICT: a category that any product in ANY branch still uses cannot be
-- deleted (RI checks are not subject to RLS).

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'products_category_org_fk'
    ) THEN
        ALTER TABLE products
            ADD CONSTRAINT products_category_org_fk
            FOREIGN KEY (organization_id, category_id)
            REFERENCES categories (organization_id, id) ON DELETE RESTRICT;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS products_category_idx ON products (category_id);

COMMIT;
