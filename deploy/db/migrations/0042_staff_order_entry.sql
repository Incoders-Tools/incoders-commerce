-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- staff-order-taking (T2): a staff member (seller, business admin, or a system administrator acting on the
-- organization) takes an order for a customer from the web. The order records who took it and an optional note.
--
--   * orders.taken_by_user_id - the signed-in staff member who took the order (always the caller, never a request
--     field). NULL for orders the customer or a guest submitted themselves.
--   * orders.note             - optional free text the staff member typed for the order, at most 500 characters.
--
-- APPLIED AFTER: 0041_take_orders_permission.sql (and 0025_orders.sql, which creates `orders`).
--
-- Both columns are written once, at insert, like the rest of the order's identity: the table-level SELECT and INSERT
-- grants of 0025 already cover them and the column-limited UPDATE grant (status, pending_reason) deliberately does
-- not. The tenant-isolation policy of 0025 is unchanged. No foreign key to `users`: a system administrator's own row
-- lives in another organization, and the order keeps the id even if the user is later removed.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE orders DROP CONSTRAINT orders_note_length_ck;
--   ALTER TABLE orders DROP COLUMN note;
--   ALTER TABLE orders DROP COLUMN taken_by_user_id;

BEGIN;

ALTER TABLE orders ADD COLUMN IF NOT EXISTS taken_by_user_id uuid NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS note text NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'orders_note_length_ck') THEN
        ALTER TABLE orders
            ADD CONSTRAINT orders_note_length_ck CHECK (note IS NULL OR char_length(note) <= 500);
    END IF;
END $$;

COMMIT;
