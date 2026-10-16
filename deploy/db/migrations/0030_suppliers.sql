-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- suppliers: the supplier registry (modelled on `customers`, 0008/0027/0029).
-- Adds the ORGANIZATION-owned catalog `supplier_categories` ("rubros": meat,
-- technology services, cleaning, ...; same shape as `business_types`, 0027),
-- `suppliers` (commercial, tax and contact data, payment terms in days, bank
-- details) and the `supplier_contacts` sub-table (several contact people per
-- supplier, at most one primary; same shape as `customer_contacts`, 0029).
--
-- APPLIED AFTER: 0029_customer_contacts.sql. References the global `cities`
-- (0028) and `organizations` (0003). The current-account ledger that hangs off
-- `suppliers` is 0031_current_account_movements.sql.
--
-- RLS: the same shape as `customers` (0008): every policy compares only
-- `organization_id` (fail-closed when no organization is scoped). The composite
-- foreign keys keep a supplier inside its organization's category and a contact
-- inside its supplier's organization (RLS filters reads, not foreign-key
-- checks). `supplier_categories` has no DELETE grant: an entry that stops being
-- used is disabled (`is_active = false`) so suppliers keep their reference.
-- `suppliers` has no DELETE grant either (a supplier is disabled, never
-- removed, because its current account must stay readable); contacts are a
-- replace-set, so DELETE is granted on them.
--
-- `city_id` references the GLOBAL geography (no organization): a plain foreign
-- key. `payment_terms_days` is the default term used to date the due day of
-- an invoice; `bank_cbu` is a 22 digit CBU/CVU; `bank_alias` is a 6-20 char
-- alias of letters, digits, dots and dashes. Tax ids are stored digits only
-- (the API normalizes them); the CHECK keeps "type <-> value" consistent.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   DROP TABLE supplier_contacts; DROP TABLE suppliers; DROP TABLE supplier_categories;

BEGIN;

-- ===========================================================================
-- 1. supplier_categories
-- ===========================================================================

CREATE TABLE IF NOT EXISTS supplier_categories (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    key             text NOT NULL CHECK (btrim(key) <> ''),
    sort_order      integer NOT NULL DEFAULT 0,
    is_active       boolean NOT NULL DEFAULT true,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    -- Target of the composite foreign key from `suppliers` (same shape as 0018/0027).
    CONSTRAINT supplier_categories_org_scoped_uk UNIQUE (organization_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS supplier_categories_org_name_uk
    ON supplier_categories (organization_id, lower(btrim(name)));
CREATE UNIQUE INDEX IF NOT EXISTS supplier_categories_org_key_uk
    ON supplier_categories (organization_id, key);

ALTER TABLE supplier_categories ENABLE ROW LEVEL SECURITY;
ALTER TABLE supplier_categories FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON supplier_categories FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON supplier_categories TO app_runtime;

DROP POLICY IF EXISTS supplier_categories_tenant_isolation ON supplier_categories;
CREATE POLICY supplier_categories_tenant_isolation ON supplier_categories
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 2. suppliers
-- ===========================================================================

CREATE TABLE IF NOT EXISTS suppliers (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    display_name       text NOT NULL CHECK (btrim(display_name) <> ''),
    legal_name         text NULL,
    tax_id_type        text NOT NULL DEFAULT 'None'
                            CHECK (tax_id_type IN ('None','Cuit','Cuil','Dni')),
    tax_id             text NULL,
    tax_condition      text NOT NULL DEFAULT 'NoAplica'
                            CHECK (tax_condition IN ('ConsumidorFinal','ResponsableInscripto',
                                                     'Monotributo','Exento','NoAplica')),
    phone              text NULL,
    email              text NULL,
    address_street     text NULL,
    address_number     text NULL,
    neighborhood       text NULL,
    postal_code        text NULL,
    city_id            uuid NULL,
    category_id        uuid NULL,
    payment_terms_days integer NULL CHECK (payment_terms_days >= 0),
    bank_cbu           text NULL CHECK (bank_cbu ~ '^[0-9]{22}$'),
    bank_alias         text NULL CHECK (bank_alias ~ '^[A-Za-z0-9.-]{6,20}$'),
    notes              text NULL,
    is_enabled         boolean NOT NULL DEFAULT true,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid NOT NULL,
    updated_at_utc     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT suppliers_tax_id_requires_type
        CHECK ((tax_id_type = 'None' AND tax_id IS NULL) OR
               (tax_id_type <> 'None' AND tax_id IS NOT NULL)),
    -- Target of the composite foreign keys from `supplier_contacts` and the ledger (0031).
    CONSTRAINT suppliers_org_scoped_uk UNIQUE (organization_id, id),
    CONSTRAINT suppliers_city_fk FOREIGN KEY (city_id) REFERENCES cities (id),
    CONSTRAINT suppliers_category_org_fk
        FOREIGN KEY (organization_id, category_id)
        REFERENCES supplier_categories (organization_id, id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS suppliers_org_idx          ON suppliers (organization_id);
CREATE INDEX IF NOT EXISTS suppliers_org_updated      ON suppliers (organization_id, updated_at_utc);
CREATE INDEX IF NOT EXISTS suppliers_org_city_idx     ON suppliers (organization_id, city_id);
CREATE INDEX IF NOT EXISTS suppliers_org_category_idx ON suppliers (organization_id, category_id);

ALTER TABLE suppliers ENABLE ROW LEVEL SECURITY;
ALTER TABLE suppliers FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON suppliers FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON suppliers TO app_runtime;

DROP POLICY IF EXISTS suppliers_tenant_isolation ON suppliers;
CREATE POLICY suppliers_tenant_isolation ON suppliers
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 3. supplier_contacts
-- ===========================================================================

CREATE TABLE IF NOT EXISTS supplier_contacts (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    supplier_id     uuid NOT NULL,
    first_name      text NOT NULL CHECK (btrim(first_name) <> ''),
    last_name       text,
    phone           text,
    email           text,
    role            text,
    is_primary      boolean NOT NULL DEFAULT false,
    sort_order      integer NOT NULL DEFAULT 0,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_contacts_supplier_fk
        FOREIGN KEY (organization_id, supplier_id)
        REFERENCES suppliers (organization_id, id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS supplier_contacts_supplier_idx
    ON supplier_contacts (organization_id, supplier_id, sort_order);
CREATE UNIQUE INDEX IF NOT EXISTS supplier_contacts_one_primary_uk
    ON supplier_contacts (supplier_id) WHERE is_primary;

ALTER TABLE supplier_contacts ENABLE ROW LEVEL SECURITY;
ALTER TABLE supplier_contacts FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON supplier_contacts FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE, DELETE ON supplier_contacts TO app_runtime;

DROP POLICY IF EXISTS supplier_contacts_tenant_isolation ON supplier_contacts;
CREATE POLICY supplier_contacts_tenant_isolation ON supplier_contacts
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;
