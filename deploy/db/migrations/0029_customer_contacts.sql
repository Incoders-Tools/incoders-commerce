-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- customer-contacts: a customer (typically a company) can have SEVERAL contact
-- people, so the single free-text `customers.contact_name` of 0027 becomes the
-- organization-scoped sub-table `customer_contacts` (first name required, last
-- name, phone, email, role, one optional primary contact, display order).
-- Existing `contact_name` values are moved into it as the primary contact and
-- the column is dropped.
--
-- APPLIED AFTER: 0028_core_geography.sql. Touches `customers` (0008, 0027).
--
-- RLS: same shape as `customers` (0008): the policy compares only
-- `organization_id` (fail-closed when no organization is scoped). Unlike the
-- catalogs, DELETE is granted: saving a customer REPLACES its contact set
-- (contacts missing from the request are removed in the same transaction).
-- The composite foreign key keeps a contact inside its customer's organization
-- (RLS filters reads, not foreign-key checks) and cascades when a customer row
-- is ever removed. At most one primary per customer: a partial unique index.
--
-- Migration of `contact_name`: first_name = btrim(contact_name), is_primary =
-- true, no last name (the free text had no structure; the owner edits it in
-- the app). Rows with a blank value get no contact. The ids are deterministic
-- (md5 of the customer id) so a re-run before the column drop cannot duplicate.
--
-- The whole file runs in ONE transaction and can be re-run safely (once the
-- column is dropped, the move is skipped).
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   ALTER TABLE customers ADD COLUMN contact_name text;
--   UPDATE customers c SET contact_name = (SELECT btrim(cc.first_name || ' ' || coalesce(cc.last_name, ''))
--     FROM customer_contacts cc WHERE cc.customer_id = c.id ORDER BY cc.is_primary DESC, cc.sort_order LIMIT 1);
--   DROP TABLE customer_contacts;
--   ALTER TABLE customers DROP CONSTRAINT customers_org_scoped_uk;

BEGIN;

-- ===========================================================================
-- 1. Target of the composite foreign key
-- ===========================================================================

DO $mig$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'customers'::regclass AND conname = 'customers_org_scoped_uk'
    ) THEN
        ALTER TABLE customers ADD CONSTRAINT customers_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $mig$;

-- ===========================================================================
-- 2. customer_contacts
-- ===========================================================================

CREATE TABLE IF NOT EXISTS customer_contacts (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    customer_id     uuid NOT NULL,
    first_name      text NOT NULL CHECK (btrim(first_name) <> ''),
    last_name       text,
    phone           text,
    email           text,
    role            text,
    is_primary      boolean NOT NULL DEFAULT false,
    sort_order      integer NOT NULL DEFAULT 0,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_contacts_customer_fk
        FOREIGN KEY (organization_id, customer_id)
        REFERENCES customers (organization_id, id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS customer_contacts_customer_idx
    ON customer_contacts (organization_id, customer_id, sort_order);
CREATE UNIQUE INDEX IF NOT EXISTS customer_contacts_one_primary_uk
    ON customer_contacts (customer_id) WHERE is_primary;

ALTER TABLE customer_contacts ENABLE ROW LEVEL SECURITY;
ALTER TABLE customer_contacts FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON customer_contacts FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE, DELETE ON customer_contacts TO app_runtime;

DROP POLICY IF EXISTS customer_contacts_tenant_isolation ON customer_contacts;
CREATE POLICY customer_contacts_tenant_isolation ON customer_contacts
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 3. Move customers.contact_name into a primary contact, then drop the column
-- ===========================================================================

DO $mig$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'customers' AND column_name = 'contact_name'
    ) THEN
        INSERT INTO customer_contacts (id, organization_id, customer_id, first_name, is_primary)
        SELECT md5('customer-contact:' || c.id::text)::uuid, c.organization_id, c.id, btrim(c.contact_name), true
        FROM customers c
        WHERE btrim(coalesce(c.contact_name, '')) <> ''
        ON CONFLICT DO NOTHING;

        ALTER TABLE customers DROP COLUMN contact_name;
    END IF;
END $mig$;

COMMIT;
