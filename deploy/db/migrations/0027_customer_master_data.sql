-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- customer-master-data: the customer registry gains two ORGANIZATION-owned
-- catalogs, `cities` and `business_types` (modelled on `categories`, 0018),
-- optional references from `customers` to them, a free-text `contact_name`
-- (the person to talk to at the customer) and the `Dni` tax id type.
--
-- APPLIED AFTER: 0026_orders_guest_check.sql. Touches `customers` (0008).
--
-- Both catalogs are shared by every branch of the organization, so their
-- policy compares only `organization_id` (fail-closed when no organization is
-- scoped) - the same shape as `customers` (0008) and `categories` (0018).
-- DELETE is deliberately NOT granted: an entry that stops being used is
-- disabled (`is_active = false`) so existing customers keep their reference.
--
-- `customers.locality` (free text) stays for compatibility; `city_id` is the
-- source of truth going forward. Existing customers keep NULL references.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   ALTER TABLE customers DROP CONSTRAINT customers_city_org_fk;
--   ALTER TABLE customers DROP CONSTRAINT customers_business_type_org_fk;
--   ALTER TABLE customers DROP COLUMN city_id, DROP COLUMN business_type_id, DROP COLUMN contact_name;
--   (restore the tax_id_type CHECK to IN ('None','Cuit','Cuil') once no row uses 'Dni')
--   DROP TABLE cities; DROP TABLE business_types;

BEGIN;

-- ===========================================================================
-- 1. cities
-- ===========================================================================

CREATE TABLE IF NOT EXISTS cities (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    key             text NOT NULL CHECK (btrim(key) <> ''),
    sort_order      integer NOT NULL DEFAULT 0,
    is_active       boolean NOT NULL DEFAULT true,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    -- Target of the composite foreign key from `customers` (same shape as 0018).
    CONSTRAINT cities_org_scoped_uk UNIQUE (organization_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS cities_org_name_uk ON cities (organization_id, lower(btrim(name)));
CREATE UNIQUE INDEX IF NOT EXISTS cities_org_key_uk ON cities (organization_id, key);

ALTER TABLE cities ENABLE ROW LEVEL SECURITY;
ALTER TABLE cities FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON cities FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON cities TO app_runtime;

DROP POLICY IF EXISTS cities_tenant_isolation ON cities;
CREATE POLICY cities_tenant_isolation ON cities
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 2. business_types
-- ===========================================================================

CREATE TABLE IF NOT EXISTS business_types (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    key             text NOT NULL CHECK (btrim(key) <> ''),
    sort_order      integer NOT NULL DEFAULT 0,
    is_active       boolean NOT NULL DEFAULT true,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT business_types_org_scoped_uk UNIQUE (organization_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS business_types_org_name_uk ON business_types (organization_id, lower(btrim(name)));
CREATE UNIQUE INDEX IF NOT EXISTS business_types_org_key_uk ON business_types (organization_id, key);

ALTER TABLE business_types ENABLE ROW LEVEL SECURITY;
ALTER TABLE business_types FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON business_types FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON business_types TO app_runtime;

DROP POLICY IF EXISTS business_types_tenant_isolation ON business_types;
CREATE POLICY business_types_tenant_isolation ON business_types
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 3. customers: optional references, contact name, Dni
-- ===========================================================================

ALTER TABLE customers ADD COLUMN IF NOT EXISTS city_id          uuid;
ALTER TABLE customers ADD COLUMN IF NOT EXISTS business_type_id uuid;
ALTER TABLE customers ADD COLUMN IF NOT EXISTS contact_name     text;

-- RLS filters reads, not foreign-key checks, so the organization is part of
-- the key: a customer can never reference another organization's city or
-- business type (same reasoning as 0014/0016/0018). MATCH SIMPLE: a NULL
-- reference is not checked. RESTRICT: no hard delete of a referenced entry.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_city_org_fk') THEN
        ALTER TABLE customers
            ADD CONSTRAINT customers_city_org_fk
            FOREIGN KEY (organization_id, city_id)
            REFERENCES cities (organization_id, id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_business_type_org_fk') THEN
        ALTER TABLE customers
            ADD CONSTRAINT customers_business_type_org_fk
            FOREIGN KEY (organization_id, business_type_id)
            REFERENCES business_types (organization_id, id) ON DELETE RESTRICT;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS customers_org_city_idx ON customers (organization_id, city_id);
CREATE INDEX IF NOT EXISTS customers_org_business_type_idx ON customers (organization_id, business_type_id);

-- `tax_id_type` is stored as text (the enum member name) behind the inline
-- CHECK 0008 declared (auto-named customers_tax_id_type_check). Replace it
-- with one that also allows 'Dni'; skipped when it already does.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'customers'::regclass
          AND conname = 'customers_tax_id_type_check'
          AND pg_get_constraintdef(oid) LIKE '%Dni%'
    ) THEN
        ALTER TABLE customers DROP CONSTRAINT IF EXISTS customers_tax_id_type_check;
        ALTER TABLE customers
            ADD CONSTRAINT customers_tax_id_type_check
            CHECK (tax_id_type IN ('None', 'Cuit', 'Cuil', 'Dni'));
    END IF;
END $$;

COMMIT;
