-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- customer-price-lists (T6): when the buyer's list has no effective price for a presentation, the organization default
-- list (Mostrador) prices it. Each order line records which list priced it:
--
--   * order_lines.priced_from_price_list_id - the price list that priced the line (NULL on lines stored before this).
--   * order_lines.price_fell_back           - true when the buyer's own list had no price and the default list priced it.
--
-- APPLIED AFTER: 0037_customer_price_lists.sql.
--
-- Provenance is a snapshot like the rest of the line (ADR-003): no foreign key, so it can never block or change later
-- list maintenance. Table-level grants and the tenant-isolation policy of `order_lines` already cover the new columns.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE order_lines DROP COLUMN price_fell_back;
--   ALTER TABLE order_lines DROP COLUMN priced_from_price_list_id;

BEGIN;

ALTER TABLE order_lines ADD COLUMN IF NOT EXISTS priced_from_price_list_id uuid NULL;
ALTER TABLE order_lines ADD COLUMN IF NOT EXISTS price_fell_back boolean NOT NULL DEFAULT false;

COMMIT;
