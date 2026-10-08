-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- persist-web-orders: web orders (registered customer and guest) used to live in an
-- in-memory dictionary of the API process and vanished on every restart. `orders` and
-- `order_lines` store them, and give every order the human number
-- `P{branch}-W-{sequence}` (`P01-W-37`): the branch code (frozen from `branches.code`,
-- which never changes) plus a sequence that counts the orders of that branch from 1.
-- APPLIED AFTER: 0021_branch_codes.sql (needs branches.code and branches_org_scoped_uk).
--
--   orders       one row per order: origin (Guest | RegisteredCustomer, text), the customer
--                id for a registered order, the guest contact for a guest order, the delivery
--                state (status, pending_reason, text like the domain enums), the number
--                (branch_code, sequence) and the submission time. Primary key
--                (organization_id, order_id): the business order id is idempotent PER
--                organization. UNIQUE (organization_id, destination_branch_id, sequence).
--   order_lines  the full OrderLineSnapshot of each line (names, quantity behavior, unit,
--                list price, discount, net price, total), frozen at submission.
--
-- Invariants the domain constructor enforces are repeated here as CHECK constraints so no
-- writer can bypass them: a registered order has a customer and no guest data, a guest order
-- has the guest contact (document, channel, address, name) and no customer.
--
-- The sequence is assigned by the application inside the INSERT transaction under a
-- transaction-scoped advisory lock keyed by the destination branch (seed 3; 1 is the
-- per-branch register lock and 2 the per-installation lock of 0022) as MAX(sequence)+1;
-- the UNIQUE constraint is the backstop. Orders are never deleted, so a committed number is
-- never reissued, and an idempotent resubmit returns the stored row without touching the counter.
--
-- Row level security: FORCE ROW LEVEL SECURITY with the symmetric tenant-isolation policy
-- (NULLIF pooler-safety hardening from 0001 - never regress it). app_runtime gets SELECT and
-- INSERT on both tables and UPDATE only on the delivery state of an order (status,
-- pending_reason); the identity, the lines and the number never change. No DELETE. The
-- composite foreign key to branches follows 0014/0016/0023.
--
-- Deploy order: apply this BEFORE the API version that stores orders. That API fails
-- /health/ready against a database without 0025 and never serves traffic half-migrated.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS order_lines;
--   DROP TABLE IF EXISTS orders;
--   COMMIT;

BEGIN;

-- Same guard 0016/0022/0023 install (the composite foreign key needs a unique
-- constraint over exactly its target columns); a no-op where they already ran.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS orders (
    organization_id       uuid        NOT NULL,
    order_id              uuid        NOT NULL,
    destination_branch_id uuid        NOT NULL,
    origin                text        NOT NULL,
    customer_id           uuid        NULL,
    guest_document_id     text        NULL,
    guest_channel         text        NULL,
    guest_contact_address text        NULL,
    guest_display_name    text        NULL,
    guest_delivery_notes  text        NULL,
    status                text        NOT NULL,
    pending_reason        text        NOT NULL,
    branch_code           smallint    NOT NULL,
    sequence              integer     NOT NULL,
    submitted_at_utc      timestamptz NOT NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT orders_pk PRIMARY KEY (organization_id, order_id),
    CONSTRAINT orders_number_uk UNIQUE (organization_id, destination_branch_id, sequence),
    CONSTRAINT orders_branch_fk
        FOREIGN KEY (organization_id, destination_branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT orders_number_ck CHECK (branch_code BETWEEN 1 AND 999 AND sequence >= 1),
    CONSTRAINT orders_origin_ck CHECK (origin IN ('Guest', 'RegisteredCustomer')),
    -- Order Construction Invariant: registered => customer, no guest data; guest => guest
    -- contact (all four mandatory parts), no customer.
    CONSTRAINT orders_origin_identity_ck CHECK (
        (origin = 'RegisteredCustomer'
            AND customer_id IS NOT NULL
            AND guest_document_id IS NULL AND guest_channel IS NULL AND guest_contact_address IS NULL
            AND guest_display_name IS NULL AND guest_delivery_notes IS NULL)
        OR
        (origin = 'Guest'
            AND customer_id IS NULL
            AND btrim(guest_document_id) <> '' AND guest_channel IS NOT NULL
            AND btrim(guest_contact_address) <> '' AND btrim(guest_display_name) <> '')),
    CONSTRAINT orders_status_ck CHECK (status IN ('PendingDestination', 'DestinationConfirmed')),
    CONSTRAINT orders_pending_reason_ck CHECK (pending_reason IN ('None', 'DestinationOffline', 'StockUnconfirmed')),
    -- A confirmed order is no longer pending for any reason.
    CONSTRAINT orders_confirmed_ck CHECK (status <> 'DestinationConfirmed' OR pending_reason = 'None')
);

CREATE INDEX IF NOT EXISTS orders_org_submitted_idx ON orders (organization_id, submitted_at_utc);

CREATE TABLE IF NOT EXISTS order_lines (
    organization_id             uuid    NOT NULL,
    order_id                    uuid    NOT NULL,
    line_no                     integer NOT NULL,
    product_id                  uuid    NOT NULL,
    product_name                text    NOT NULL,
    presentation_id             uuid    NOT NULL,
    presentation_name           text    NOT NULL,
    quantity_behavior           text    NOT NULL,
    unit_id                     uuid    NOT NULL,
    quantity                    numeric NOT NULL,
    unit_list_price             numeric NOT NULL,
    applied_discount_percentage numeric NOT NULL,
    unit_net_price              numeric NOT NULL,
    line_total                  numeric NOT NULL,
    CONSTRAINT order_lines_pk PRIMARY KEY (organization_id, order_id, line_no),
    CONSTRAINT order_lines_order_fk
        FOREIGN KEY (organization_id, order_id) REFERENCES orders (organization_id, order_id) ON DELETE CASCADE,
    CONSTRAINT order_lines_line_no_ck CHECK (line_no >= 1),
    CONSTRAINT order_lines_quantity_behavior_ck CHECK (quantity_behavior IN ('FixedQuantity', 'Weighted', 'Bulk'))
);

ALTER TABLE orders ENABLE ROW LEVEL SECURITY;
ALTER TABLE orders FORCE ROW LEVEL SECURITY;
REVOKE ALL ON orders FROM PUBLIC;
GRANT SELECT, INSERT ON orders TO app_runtime;
-- Only the delivery state evolves (MarkPending / MarkDestinationConfirmed).
GRANT UPDATE (status, pending_reason) ON orders TO app_runtime;

ALTER TABLE order_lines ENABLE ROW LEVEL SECURITY;
ALTER TABLE order_lines FORCE ROW LEVEL SECURITY;
REVOKE ALL ON order_lines FROM PUBLIC;
GRANT SELECT, INSERT ON order_lines TO app_runtime;   -- no UPDATE, no DELETE: lines are frozen

DROP POLICY IF EXISTS orders_tenant_isolation ON orders;
CREATE POLICY orders_tenant_isolation ON orders
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS order_lines_tenant_isolation ON order_lines;
CREATE POLICY order_lines_tenant_isolation ON order_lines
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;
