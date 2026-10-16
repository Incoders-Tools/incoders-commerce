-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- persist-web-orders (review follow-up): the guest branch of `orders_origin_identity_ck`
-- (0025) wrote `btrim(guest_document_id) <> ''` without `IS NOT NULL`. In SQL a CHECK passes
-- when it evaluates to NULL, so a guest order with a NULL document id, contact address or
-- display name was accepted by the database even though the domain constructor rejects it.
-- This migration drops and re-adds the constraint with each mandatory guest part written as
-- `IS NOT NULL AND btrim(...) <> ''`. The registered-customer branch is unchanged.
-- APPLIED AFTER: 0025_orders.sql. 0025 stays as it was applied (a shipped migration is never
-- edited); a fresh database gets the weak constraint from 0025 and the strict one from 0026.
--
-- Existing rows are validated by the re-added constraint: every order stored so far came
-- through the domain constructor, so none can violate it.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   ALTER TABLE orders DROP CONSTRAINT IF EXISTS orders_origin_identity_ck;
--   ALTER TABLE orders ADD CONSTRAINT orders_origin_identity_ck CHECK (
--       (origin = 'RegisteredCustomer' AND customer_id IS NOT NULL
--           AND guest_document_id IS NULL AND guest_channel IS NULL AND guest_contact_address IS NULL
--           AND guest_display_name IS NULL AND guest_delivery_notes IS NULL)
--       OR
--       (origin = 'Guest' AND customer_id IS NULL
--           AND btrim(guest_document_id) <> '' AND guest_channel IS NOT NULL
--           AND btrim(guest_contact_address) <> '' AND btrim(guest_display_name) <> ''));
--   COMMIT;

BEGIN;

ALTER TABLE orders DROP CONSTRAINT IF EXISTS orders_origin_identity_ck;
ALTER TABLE orders ADD CONSTRAINT orders_origin_identity_ck CHECK (
    (origin = 'RegisteredCustomer'
        AND customer_id IS NOT NULL
        AND guest_document_id IS NULL AND guest_channel IS NULL AND guest_contact_address IS NULL
        AND guest_display_name IS NULL AND guest_delivery_notes IS NULL)
    OR
    (origin = 'Guest'
        AND customer_id IS NULL
        AND guest_document_id IS NOT NULL AND btrim(guest_document_id) <> ''
        AND guest_channel IS NOT NULL
        AND guest_contact_address IS NOT NULL AND btrim(guest_contact_address) <> ''
        AND guest_display_name IS NOT NULL AND btrim(guest_display_name) <> ''));

COMMIT;
