-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- owner-requests-quick-adjustments (Q1): the customer's location reference, how to find the place
-- ("Casa gris con 2 árboles al frente"). It is NOT the delivery notes (`delivery_notes`, 0008), which say what to do
-- with the delivery ("Dejar con el vecino si no hay nadie"); both are shown on the remito.
--
-- APPLIED AFTER: 0052_organization_account_standing.sql.
--
-- Nullable, no default: every existing customer simply has none. The `customers` RLS policy and the `app_runtime`
-- grants already cover a new column.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE customers DROP COLUMN location_reference;

BEGIN;

ALTER TABLE customers ADD COLUMN IF NOT EXISTS location_reference text NULL;

COMMIT;
