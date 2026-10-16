-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- price-editing-and-desktop-polish (T4): same-day price corrections from the batch publish.
--
-- `price_list_entries` is append-only on purpose: app_runtime has held only SELECT and INSERT on it since 0009, so a
-- published price is never rewritten and the history is the record of every price a list ever had. The owner needs ONE
-- narrow exception: publishing a batch of prices (`POST /pricing/price-lists/{id}/entries/batch`) for a presentation
-- that already has an entry effective that SAME day replaces that entry's price as a correction, because
-- `price_list_entries_one_per_day` allows a single entry per (list, presentation, day).
--
-- The exception, kept as narrow as possible:
--   * a column-scoped UPDATE grant: only `unit_price`, `created_at_utc` (bumped so the POS catalog replica, whose cursor
--     is `created_at_utc > since`, picks the correction up) and `created_by_user_id` (who corrected it). The list,
--     presentation, effective date, organization, branch, source and import batch of an entry stay immutable, and
--     there is still no DELETE grant;
--   * the only code path that issues it is `PostgresPriceListStore.PublishEntriesAsync`, inside the same transaction
--     that writes the batch's audit row with the old and new price of every replaced entry. The single-entry
--     `POST .../entries` keeps refusing a second price for the same day (409 entry-already-exists-for-date);
--   * no policy change: `price_list_entries_tenant_isolation` (0017) applies to every command with USING and WITH CHECK
--     on the organization AND the branch, so an UPDATE only ever sees and keeps rows of the caller's own tenant.
--
-- APPLIED AFTER: 0042_staff_order_entry.sql (and 0009/0017, which create the table and its branch-scoped policy).
--
-- The whole file runs in ONE transaction and can be re-run safely (GRANT is idempotent).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   REVOKE UPDATE (unit_price, created_at_utc, created_by_user_id) ON price_list_entries FROM app_runtime;

BEGIN;

GRANT UPDATE (unit_price, created_at_utc, created_by_user_id) ON price_list_entries TO app_runtime;

COMMIT;
