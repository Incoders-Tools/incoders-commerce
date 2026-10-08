-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Order fulfillment, delivery runs and remitos (delivery notes).
-- APPLIED AFTER: 0044_pos_sale_voids.sql.
--
-- 1. ORDERS get an operational status, separate from the ADR-003 sync status
--    (`status`/`pending_reason`, untouched):
--      fulfillment_status  Confirmed -> InPreparation -> ReadyToDispatch -> OutForDelivery
--                          -> Delivered | PartiallyDelivered; Cancelled before dispatch.
--                          Existing orders start as Confirmed.
--      remito_sequence     the order's remito number within its branch (R01-00000042),
--                          assigned once, the first time the remito is printed or the
--                          order is dispatched; `remito_counters` hands them out.
--      settlement          how a delivered order was settled: on the customer's current
--                          account, or paid on delivery (also every guest order).
--      delivered_total     what was delivered, at the order's net prices.
--    The new columns get a column-level UPDATE grant; order_lines stay frozen.
--
-- 2. DELIVERY RUNS (repartos): a date, a driver and a vehicle, and the ordered list of
--    the orders the truck takes (`delivery_run_orders`, an order is in at most one run).
--    Planned -> OutForDelivery (dispatch) -> Completed (settled on return).
--    `order_line_deliveries` keeps what was really delivered per line (kilos may differ
--    from the order).
--
-- 3. CURRENT ACCOUNTS for CUSTOMERS: `current_account_movements` (0031, supplier-only)
--    accepts party_kind 'Customer' with its `customer_id`. For a customer the balance is
--    what the CUSTOMER owes: a delivery is a Debit, a payment a Credit. `source_type` /
--    `source_id` make a posting idempotent (one movement per delivered order).
--
-- 4. DOCUMENT DATA of the organization (legal name, CUIT, tax condition, gross income
--    number, activity start, fiscal address, footer) and of each branch (address,
--    locality, phone, e-mail, warehouse) printed on remitos. All nullable: existing
--    rows simply have none yet. Covered by the tables' existing grants and policies.
--
-- RLS: every new table is org-scoped with FORCE ROW LEVEL SECURITY and the symmetric
-- tenant-isolation policy (NULLIF pooler-safety hardening from 0001 - never regress it),
-- like `orders` (0025). Composite foreign keys keep every reference inside the tenant.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS order_line_deliveries, delivery_run_orders, delivery_runs, remito_counters;
--   ALTER TABLE orders DROP COLUMN IF EXISTS fulfillment_status, DROP COLUMN IF EXISTS fulfillment_updated_at,
--       DROP COLUMN IF EXISTS cancel_reason, DROP COLUMN IF EXISTS remito_sequence, DROP COLUMN IF EXISTS settlement,
--       DROP COLUMN IF EXISTS delivered_total, DROP COLUMN IF EXISTS delivered_at;
--   DROP INDEX IF EXISTS current_account_movements_source_uk, current_account_movements_customer_idx;
--   ALTER TABLE current_account_movements DROP CONSTRAINT IF EXISTS current_account_movements_party_customer,
--       DROP CONSTRAINT IF EXISTS current_account_movements_customer_fk,
--       DROP CONSTRAINT IF EXISTS current_account_movements_party_kind_ck,
--       DROP COLUMN IF EXISTS customer_id, DROP COLUMN IF EXISTS source_type, DROP COLUMN IF EXISTS source_id;
--   ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_party_kind_check
--       CHECK (party_kind IN ('Supplier'));   -- only when no Customer movement exists
--   ALTER TABLE organizations DROP COLUMN IF EXISTS legal_name, DROP COLUMN IF EXISTS tax_id,
--       DROP COLUMN IF EXISTS tax_condition, DROP COLUMN IF EXISTS gross_income_number,
--       DROP COLUMN IF EXISTS activity_start_date, DROP COLUMN IF EXISTS fiscal_address,
--       DROP COLUMN IF EXISTS document_footer;
--   ALTER TABLE branches DROP COLUMN IF EXISTS address, DROP COLUMN IF EXISTS locality,
--       DROP COLUMN IF EXISTS phone, DROP COLUMN IF EXISTS email, DROP COLUMN IF EXISTS warehouse_address;
--   COMMIT;

BEGIN;

-- ---- 1. orders -------------------------------------------------------------------------

ALTER TABLE orders ADD COLUMN IF NOT EXISTS fulfillment_status     text          NOT NULL DEFAULT 'Confirmed';
ALTER TABLE orders ADD COLUMN IF NOT EXISTS fulfillment_updated_at timestamptz   NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS cancel_reason          text          NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS remito_sequence        integer       NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS settlement             text          NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS delivered_total        numeric(18,2) NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS delivered_at           timestamptz   NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'orders_fulfillment_status_ck') THEN
        ALTER TABLE orders ADD CONSTRAINT orders_fulfillment_status_ck CHECK (fulfillment_status IN (
            'Confirmed', 'InPreparation', 'ReadyToDispatch', 'OutForDelivery', 'Delivered', 'PartiallyDelivered', 'Cancelled'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'orders_settlement_ck') THEN
        ALTER TABLE orders ADD CONSTRAINT orders_settlement_ck
            CHECK (settlement IS NULL OR settlement IN ('CurrentAccount', 'PaidOnDelivery'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'orders_cancel_reason_ck') THEN
        ALTER TABLE orders ADD CONSTRAINT orders_cancel_reason_ck
            CHECK (cancel_reason IS NULL OR char_length(cancel_reason) <= 200);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'orders_remito_sequence_ck') THEN
        ALTER TABLE orders ADD CONSTRAINT orders_remito_sequence_ck CHECK (remito_sequence IS NULL OR remito_sequence >= 1);
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS orders_remito_number_uk
    ON orders (organization_id, destination_branch_id, remito_sequence) WHERE remito_sequence IS NOT NULL;
CREATE INDEX IF NOT EXISTS orders_fulfillment_idx
    ON orders (organization_id, destination_branch_id, fulfillment_status, submitted_at_utc);

GRANT UPDATE (fulfillment_status, fulfillment_updated_at, cancel_reason, remito_sequence, settlement, delivered_total, delivered_at)
    ON orders TO app_runtime;

CREATE TABLE IF NOT EXISTS remito_counters (
    organization_id uuid    NOT NULL,
    branch_id       uuid    NOT NULL,
    last_sequence   integer NOT NULL CHECK (last_sequence >= 0),
    CONSTRAINT remito_counters_pk PRIMARY KEY (organization_id, branch_id),
    CONSTRAINT remito_counters_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

ALTER TABLE remito_counters ENABLE ROW LEVEL SECURITY;
ALTER TABLE remito_counters FORCE ROW LEVEL SECURITY;
REVOKE ALL ON remito_counters FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON remito_counters TO app_runtime;
DROP POLICY IF EXISTS remito_counters_tenant_isolation ON remito_counters;
CREATE POLICY remito_counters_tenant_isolation ON remito_counters
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ---- 2. delivery runs ------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS delivery_runs (
    organization_id    uuid        NOT NULL,
    id                 uuid        NOT NULL,
    branch_id          uuid        NOT NULL,
    run_number         integer     NOT NULL CHECK (run_number >= 1),
    run_date           date        NOT NULL,
    driver_name        text        NULL CHECK (driver_name IS NULL OR char_length(driver_name) <= 120),
    vehicle            text        NULL CHECK (vehicle IS NULL OR char_length(vehicle) <= 120),
    notes              text        NULL CHECK (notes IS NULL OR char_length(notes) <= 500),
    status             text        NOT NULL DEFAULT 'Planned' CHECK (status IN ('Planned', 'OutForDelivery', 'Completed')),
    created_at         timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid        NOT NULL,
    dispatched_at      timestamptz NULL,
    completed_at       timestamptz NULL,
    CONSTRAINT delivery_runs_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT delivery_runs_number_uk UNIQUE (organization_id, branch_id, run_number),
    CONSTRAINT delivery_runs_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS delivery_runs_branch_date_idx ON delivery_runs (organization_id, branch_id, run_date);

ALTER TABLE delivery_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery_runs FORCE ROW LEVEL SECURITY;
REVOKE ALL ON delivery_runs FROM PUBLIC;
GRANT SELECT, INSERT ON delivery_runs TO app_runtime;
GRANT UPDATE (run_date, driver_name, vehicle, notes, status, dispatched_at, completed_at) ON delivery_runs TO app_runtime;
DROP POLICY IF EXISTS delivery_runs_tenant_isolation ON delivery_runs;
CREATE POLICY delivery_runs_tenant_isolation ON delivery_runs
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

CREATE TABLE IF NOT EXISTS delivery_run_orders (
    organization_id uuid    NOT NULL,
    run_id          uuid    NOT NULL,
    order_id        uuid    NOT NULL,
    stop_no         integer NOT NULL CHECK (stop_no >= 1),
    CONSTRAINT delivery_run_orders_pk PRIMARY KEY (organization_id, run_id, order_id),
    CONSTRAINT delivery_run_orders_one_run_uk UNIQUE (organization_id, order_id),
    CONSTRAINT delivery_run_orders_run_fk
        FOREIGN KEY (organization_id, run_id) REFERENCES delivery_runs (organization_id, id) ON DELETE CASCADE,
    CONSTRAINT delivery_run_orders_order_fk
        FOREIGN KEY (organization_id, order_id) REFERENCES orders (organization_id, order_id) ON DELETE CASCADE
);

ALTER TABLE delivery_run_orders ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery_run_orders FORCE ROW LEVEL SECURITY;
REVOKE ALL ON delivery_run_orders FROM PUBLIC;
GRANT SELECT, INSERT, DELETE ON delivery_run_orders TO app_runtime;
GRANT UPDATE (stop_no) ON delivery_run_orders TO app_runtime;
DROP POLICY IF EXISTS delivery_run_orders_tenant_isolation ON delivery_run_orders;
CREATE POLICY delivery_run_orders_tenant_isolation ON delivery_run_orders
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

CREATE TABLE IF NOT EXISTS order_line_deliveries (
    organization_id    uuid          NOT NULL,
    order_id           uuid          NOT NULL,
    line_no            integer       NOT NULL,
    delivered_quantity numeric(18,3) NOT NULL CHECK (delivered_quantity >= 0),
    recorded_at        timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT order_line_deliveries_pk PRIMARY KEY (organization_id, order_id, line_no),
    CONSTRAINT order_line_deliveries_line_fk
        FOREIGN KEY (organization_id, order_id, line_no) REFERENCES order_lines (organization_id, order_id, line_no) ON DELETE CASCADE
);

ALTER TABLE order_line_deliveries ENABLE ROW LEVEL SECURITY;
ALTER TABLE order_line_deliveries FORCE ROW LEVEL SECURITY;
REVOKE ALL ON order_line_deliveries FROM PUBLIC;
GRANT SELECT, INSERT ON order_line_deliveries TO app_runtime;   -- written once, at settlement
DROP POLICY IF EXISTS order_line_deliveries_tenant_isolation ON order_line_deliveries;
CREATE POLICY order_line_deliveries_tenant_isolation ON order_line_deliveries
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ---- 3. customer current accounts -----------------------------------------------------

ALTER TABLE current_account_movements ADD COLUMN IF NOT EXISTS customer_id uuid NULL;
ALTER TABLE current_account_movements ADD COLUMN IF NOT EXISTS source_type text NULL;
ALTER TABLE current_account_movements ADD COLUMN IF NOT EXISTS source_id   uuid NULL;

-- 0031 declared the party kind inline, so Postgres named it current_account_movements_party_kind_check; it is
-- replaced by a named one that also admits customers.
ALTER TABLE current_account_movements DROP CONSTRAINT IF EXISTS current_account_movements_party_kind_check;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_party_kind_ck') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_party_kind_ck
            CHECK (party_kind IN ('Supplier', 'Customer'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_party_customer') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_party_customer
            CHECK (party_kind <> 'Customer' OR customer_id = party_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_org_scoped_uk') THEN
        ALTER TABLE customers ADD CONSTRAINT customers_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_customer_fk') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_customer_fk
            FOREIGN KEY (organization_id, customer_id) REFERENCES customers (organization_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_source_ck') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_source_ck
            CHECK ((source_type IS NULL) = (source_id IS NULL));
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS current_account_movements_source_uk
    ON current_account_movements (organization_id, party_id, source_type, source_id) WHERE source_type IS NOT NULL;
CREATE INDEX IF NOT EXISTS current_account_movements_customer_idx
    ON current_account_movements (organization_id, customer_id, occurred_on, created_at_utc) WHERE customer_id IS NOT NULL;

-- ---- 4. document data -----------------------------------------------------------------

ALTER TABLE organizations ADD COLUMN IF NOT EXISTS legal_name          text NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS tax_id              text NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS tax_condition       text NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS gross_income_number text NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS activity_start_date date NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS fiscal_address      text NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS document_footer     text NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'organizations_document_data_ck') THEN
        ALTER TABLE organizations ADD CONSTRAINT organizations_document_data_ck CHECK (
            (legal_name IS NULL OR char_length(legal_name) <= 200)
            AND (tax_id IS NULL OR tax_id ~ '^[0-9]{11}$')
            AND (tax_condition IS NULL OR tax_condition IN ('ResponsableInscripto', 'Monotributo', 'Exento', 'ConsumidorFinal', 'NoAplica'))
            AND (gross_income_number IS NULL OR char_length(gross_income_number) <= 40)
            AND (fiscal_address IS NULL OR char_length(fiscal_address) <= 200)
            AND (document_footer IS NULL OR char_length(document_footer) <= 300));
    END IF;
END $$;

ALTER TABLE branches ADD COLUMN IF NOT EXISTS address           text NULL;
ALTER TABLE branches ADD COLUMN IF NOT EXISTS locality          text NULL;
ALTER TABLE branches ADD COLUMN IF NOT EXISTS phone             text NULL;
ALTER TABLE branches ADD COLUMN IF NOT EXISTS email             text NULL;
ALTER TABLE branches ADD COLUMN IF NOT EXISTS warehouse_address text NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_document_data_ck') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_document_data_ck CHECK (
            (address IS NULL OR char_length(address) <= 200)
            AND (locality IS NULL OR char_length(locality) <= 120)
            AND (phone IS NULL OR char_length(phone) <= 60)
            AND (email IS NULL OR char_length(email) <= 200)
            AND (warehouse_address IS NULL OR char_length(warehouse_address) <= 200));
    END IF;
END $$;

COMMIT;
