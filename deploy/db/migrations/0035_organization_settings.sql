-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- purchases-receptions-and-stock (T7): per-organization number format, the first field of the organization
-- settings. `quantity_decimal_separator` says how this business writes quantities (kilos): `Comma` ("1,5", the
-- default and today's behaviour) or `Dot` ("1.5"). Money formatting is NOT part of it.
--
-- APPLIED AFTER: 0034_stock_replica_index.sql.
--
-- Why columns on `organizations` and not a 1:1 `organization_settings` table: `organizations` already carries the
-- per-business web settings (`logo_url`, `primary_color`, 0015), its tenant-isolation RLS policy and the
-- `app_runtime` SELECT/UPDATE grant already cover a new column (table-level grants apply to every column), and
-- reading a setting needs no join. Each future setting is one more NOT NULL DEFAULT column with its own CHECK.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE organizations DROP CONSTRAINT organizations_quantity_decimal_separator_ck;
--   ALTER TABLE organizations DROP COLUMN quantity_decimal_separator;

BEGIN;

ALTER TABLE organizations
    ADD COLUMN IF NOT EXISTS quantity_decimal_separator text NOT NULL DEFAULT 'Comma';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'organizations_quantity_decimal_separator_ck'
    ) THEN
        ALTER TABLE organizations
            ADD CONSTRAINT organizations_quantity_decimal_separator_ck
            CHECK (quantity_decimal_separator IN ('Comma', 'Dot'));
    END IF;
END $$;

COMMIT;
