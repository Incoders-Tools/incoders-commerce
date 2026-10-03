-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- admin-console-field-fixes (T3b): whether a customer is a person or a company, independent of the commercial
-- Retail / Wholesale kind. It decides what the single customer name (`display_name`) means: a person's full name or a
-- company's legal name. `legal_name`, `locality` and `province` stay as columns but the admin API no longer writes them.
--
--   * customers.party_type - 'Person' | 'Company'. Existing customers are backfilled as 'Company' when their tax id
--     type is 'Cuit', otherwise 'Person' (editable afterwards). DEFAULT 'Person' for rows inserted without it; the API
--     always sends it.
--
-- APPLIED AFTER: 0039_organization_country_and_city_postal_code.sql.
--
-- The backfill only fills rows that have no party type yet, so re-running never overwrites an edited one. The
-- tenant-isolation policy and table-level grants of `customers` already cover the new column. The branch replica and
-- the device sync contract are unchanged.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE customers DROP CONSTRAINT customers_party_type_ck;
--   ALTER TABLE customers DROP COLUMN party_type;

BEGIN;

ALTER TABLE customers ADD COLUMN IF NOT EXISTS party_type text NULL;

UPDATE customers
SET party_type = CASE WHEN tax_id_type = 'Cuit' THEN 'Company' ELSE 'Person' END
WHERE party_type IS NULL;

ALTER TABLE customers ALTER COLUMN party_type SET DEFAULT 'Person';
ALTER TABLE customers ALTER COLUMN party_type SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_party_type_ck') THEN
        ALTER TABLE customers
            ADD CONSTRAINT customers_party_type_ck CHECK (party_type IN ('Person', 'Company'));
    END IF;
END $$;

COMMIT;
