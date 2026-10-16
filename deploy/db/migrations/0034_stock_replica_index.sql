-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- purchases-receptions-and-stock (T5): index for the cloud -> branch stock replica channel (`GET /device/stock/sync`).
--
-- APPLIED AFTER: 0033_stock.sql.
--
-- The replica asks "which presentations of this branch had a movement since the cursor". `created_at_utc` is the
-- monotonic column of that cursor (the same idea as `updated_at_utc` for the customers/catalog replicas); without this
-- index the question is a scan of the whole branch ledger on every sweep of every terminal. The index only changes
-- query plans: no data, no permission and no RLS policy changes.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   DROP INDEX stock_movements_created_idx;

BEGIN;

CREATE INDEX IF NOT EXISTS stock_movements_created_idx
    ON stock_movements (organization_id, branch_id, created_at_utc);

COMMIT;
