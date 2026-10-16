-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- admin-console-field-fixes (T2): the country of an organization and the postal code of a city.
--
--   * organizations.country_code - the organization's country (`countries.code`). NOT NULL DEFAULT 'AR': adding the
--     column backfills every existing organization with Argentina, the only country loaded. The customer form lists
--     the provinces of this country.
--   * cities.postal_code         - optional postal code of a city, maintained by the platform system administrator
--     (Georef/INDEC publishes none, so nothing is invented). An Argentine CP of 4 digits ("2000") or a CPA: the
--     province letter (no I or O), 4 digits and 3 letters ("S2000ABC"), always upper case.
--
-- APPLIED AFTER: 0038_order_line_price_provenance.sql.
--
-- Table-level grants and policies already cover the new columns: `app_runtime` reads/updates `organizations` under
-- its tenant-isolation policy (0003) and reads/inserts/updates the global `cities` (0028).
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE cities DROP CONSTRAINT cities_postal_code_format_ck;
--   ALTER TABLE cities DROP COLUMN postal_code;
--   ALTER TABLE organizations DROP CONSTRAINT organizations_country_fk;
--   ALTER TABLE organizations DROP COLUMN country_code;

BEGIN;

ALTER TABLE organizations ADD COLUMN IF NOT EXISTS country_code text NOT NULL DEFAULT 'AR';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'organizations_country_fk') THEN
        ALTER TABLE organizations
            ADD CONSTRAINT organizations_country_fk FOREIGN KEY (country_code) REFERENCES countries (code);
    END IF;
END $$;

ALTER TABLE cities ADD COLUMN IF NOT EXISTS postal_code text NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'cities_postal_code_format_ck') THEN
        ALTER TABLE cities
            ADD CONSTRAINT cities_postal_code_format_ck
            CHECK (postal_code IS NULL OR postal_code ~ '^([0-9]{4}|[A-HJ-NP-Z][0-9]{4}[A-Z]{3})$');
    END IF;
END $$;

COMMIT;
