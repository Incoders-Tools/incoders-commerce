-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- vaca-verde-suppliers-and-catalog-seed (T1): product soft deletion. A deactivated product stays in the table (its
-- receptions, stock movements, sales and price entries keep pointing at it) but disappears from the default catalog
-- lists, the guest ordering catalog, new receptions and the POS replica. `deactivated_at_utc` records when.
--
-- APPLIED AFTER: 0035_organization_settings.sql.
--
-- The flag lives on the PRODUCT only: the presentations of an inactive product are inactive by implication, so there
-- is one switch to flip and no way to leave a product half active. The branch replica is told about the removal
-- through the existing `removedPresentationIds` of `GET /device/catalog/sync`; the deactivation bumps
-- `products.updated_at_utc`, which that cursor already follows (`products_org_updated`), so no new index or column is
-- needed for it. No index on `is_active` either: a branch catalog is hundreds of rows and the lists already scan it.
-- Table-level grants and the tenant-isolation RLS policy of `products` (0009/0016) already cover the new columns.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE products DROP COLUMN deactivated_at_utc;
--   ALTER TABLE products DROP COLUMN is_active;

BEGIN;

ALTER TABLE products ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true;
ALTER TABLE products ADD COLUMN IF NOT EXISTS deactivated_at_utc timestamptz NULL;

COMMIT;
