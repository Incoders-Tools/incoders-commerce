-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- railway-deploy-pipeline S10: Cloud.Api keeps its ASP.NET Core Data Protection key ring here instead of inside the
-- container, so a redeploy or restart keeps every auth cookie valid (until 0061 each deploy created a new ring and
-- signed every user out). One row per element of the ring (`key-<guid>` and revocations), written by
-- `PostgresDataProtectionKeyRepository`; the XML is the framework's own, unencrypted at rest (see that class).
--
-- APPLIED AFTER: 0060_employee_customers_on_default_list.sql.
--
-- RLS: NOT tenant data (one ring for the whole deployment, no organization_id). Like the geography tables (0028) it
-- keeps ENABLE + FORCE ROW LEVEL SECURITY, so a future table-level change cannot silently expose it, with permissive,
-- role-agnostic policies; access stays narrow by GRANT: `app_runtime` may only SELECT and INSERT (the framework never
-- updates or deletes an element: an expired key stays, a revocation is a new element), `platform_readonly` and PUBLIC
-- get nothing. The identity column needs no sequence grant: INSERT on the table is enough.
--
-- The whole file runs in ONE transaction and can be re-run safely (IF NOT EXISTS, DROP POLICY IF EXISTS).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file (dropping the table signs every user out):
--   DROP TABLE data_protection_keys;

BEGIN;

CREATE TABLE IF NOT EXISTS data_protection_keys (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    friendly_name text NOT NULL UNIQUE CHECK (btrim(friendly_name) <> ''),
    xml           text NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE data_protection_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE data_protection_keys FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON data_protection_keys FROM PUBLIC;
GRANT SELECT, INSERT ON data_protection_keys TO app_runtime;

DROP POLICY IF EXISTS data_protection_keys_read ON data_protection_keys;
CREATE POLICY data_protection_keys_read ON data_protection_keys FOR SELECT USING (true);
DROP POLICY IF EXISTS data_protection_keys_insert ON data_protection_keys;
CREATE POLICY data_protection_keys_insert ON data_protection_keys FOR INSERT WITH CHECK (true);

COMMIT;
