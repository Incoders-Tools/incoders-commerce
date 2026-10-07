-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Which categories the POS offers as filters, and discarding a delivery run that was planned wrong.
-- APPLIED AFTER: 0048_treasury_account_types_and_voids.sql.
--
-- 1. CATEGORIES ON THE POS. `categories.show_in_pos` says whether the POS category rail offers the category as a filter
--    (its products are still sold, scanned, searched and listed under "Todos" either way) and `pos_sort_order` the order
--    the rail shows it in (then by name). Every category is shown by default; "Embutidos" and "Achuras", which the POS
--    used to hide with a hard-coded list, start hidden so nothing changes for the cashier. The POS receives the
--    categories with its `price-lists` snapshot (a full snapshot on every sync) and keeps them locally.
--
-- 2. DISCARDING A PLANNED RUN. A delivery run still Planned (nothing dispatched: no remito numbered, no stock or money
--    moved) can be deleted; its stops go with it (ON DELETE CASCADE) and its orders are free again for another run. The
--    API refuses any other status and audits the deletion with the run's content. DELETE is granted for that.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   REVOKE DELETE ON delivery_runs FROM app_runtime;
--   ALTER TABLE categories DROP COLUMN IF EXISTS show_in_pos, DROP COLUMN IF EXISTS pos_sort_order;
--   COMMIT;

BEGIN;

-- The categories the POS hid by name until now (case and accents ignored) start hidden by the setting. Only when the
-- column is created: re-running this file never overwrites an administrator's later choice.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'public' AND table_name = 'categories' AND column_name = 'show_in_pos') THEN
        ALTER TABLE categories ADD COLUMN show_in_pos boolean NOT NULL DEFAULT true;
        UPDATE categories SET show_in_pos = false
        WHERE translate(lower(btrim(name)), 'áéíóú', 'aeiou') IN ('embutidos', 'achuras');
    END IF;
END $$;

ALTER TABLE categories ADD COLUMN IF NOT EXISTS pos_sort_order integer NOT NULL DEFAULT 0;

GRANT DELETE ON delivery_runs TO app_runtime;

COMMIT;
