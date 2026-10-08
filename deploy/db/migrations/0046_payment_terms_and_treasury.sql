-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Payment terms of customers and the company's treasury.
-- APPLIED AFTER: 0045_order_fulfillment_and_delivery.sql.
--
-- 1. PAYMENT TERMS. A sale or delivery on a customer's current account is due after the customer's own payment terms
--    (`customers.payment_terms_days`) or, when the customer has none, after the organization's default
--    (`organizations.default_customer_payment_terms_days`, 30 days unless changed). 0 means due the same day. The
--    free-text `customers.payment_terms` stays as a note; the days are what due dates are computed from.
--
-- 2. TREASURY (tesorería): the company's money accounts, one per branch and payment method (Cash = the drawer, Card,
--    Qr), created on first use, and their append-only movements. Every POS sale paid at the counter, every payment a
--    customer makes at the POS, and every delivery paid on delivery puts money In; a void takes it back Out with a
--    Reversal (never an update or a delete). `source_type` + `source_id` make each posting idempotent (a retried
--    projection never counts money twice); a movement is reversed at most once.
--
-- RLS: both tables are org-scoped with FORCE ROW LEVEL SECURITY and the symmetric tenant-isolation policy (NULLIF
-- pooler-safety hardening from 0001 - never regress it). SELECT and INSERT only: money is never rewritten.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS treasury_movements, treasury_accounts;
--   ALTER TABLE customers DROP COLUMN IF EXISTS payment_terms_days;
--   ALTER TABLE organizations DROP COLUMN IF EXISTS default_customer_payment_terms_days;
--   COMMIT;

BEGIN;

-- ---- 1. payment terms ------------------------------------------------------------------

ALTER TABLE customers ADD COLUMN IF NOT EXISTS payment_terms_days smallint NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS default_customer_payment_terms_days smallint NOT NULL DEFAULT 30;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_payment_terms_days_ck') THEN
        ALTER TABLE customers ADD CONSTRAINT customers_payment_terms_days_ck
            CHECK (payment_terms_days IS NULL OR payment_terms_days BETWEEN 0 AND 365);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'organizations_default_payment_terms_ck') THEN
        ALTER TABLE organizations ADD CONSTRAINT organizations_default_payment_terms_ck
            CHECK (default_customer_payment_terms_days BETWEEN 0 AND 365);
    END IF;
END $$;

-- ---- 2. treasury -----------------------------------------------------------------------

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS treasury_accounts (
    organization_id uuid        NOT NULL,
    id              uuid        NOT NULL,
    branch_id       uuid        NOT NULL,
    kind            text        NOT NULL CHECK (kind IN ('Cash', 'Card', 'Qr')),
    name            text        NOT NULL CHECK (btrim(name) <> '' AND char_length(name) <= 120),
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT treasury_accounts_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT treasury_accounts_branch_kind_uk UNIQUE (organization_id, branch_id, kind),
    CONSTRAINT treasury_accounts_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

ALTER TABLE treasury_accounts ENABLE ROW LEVEL SECURITY;
ALTER TABLE treasury_accounts FORCE ROW LEVEL SECURITY;
REVOKE ALL ON treasury_accounts FROM PUBLIC;
GRANT SELECT, INSERT ON treasury_accounts TO app_runtime;
DROP POLICY IF EXISTS treasury_accounts_tenant_isolation ON treasury_accounts;
CREATE POLICY treasury_accounts_tenant_isolation ON treasury_accounts
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

CREATE TABLE IF NOT EXISTS treasury_movements (
    organization_id      uuid          NOT NULL,
    id                   uuid          NOT NULL,
    account_id           uuid          NOT NULL,
    kind                 text          NOT NULL CHECK (kind IN ('Sale', 'CustomerPayment', 'DeliveryPayment', 'Reversal')),
    direction            text          NOT NULL CHECK (direction IN ('In', 'Out')),
    amount               numeric(18,2) NOT NULL CHECK (amount > 0),
    occurred_at_utc      timestamptz   NOT NULL,
    business_date        date          NOT NULL,
    concept              text          NOT NULL CHECK (btrim(concept) <> ''),
    document_reference   text          NULL,
    customer_id          uuid          NULL,
    source_type          text          NULL,
    source_id            uuid          NULL,
    reverses_movement_id uuid          NULL,
    created_by_user_id   uuid          NOT NULL,
    created_at           timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT treasury_movements_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT treasury_movements_account_fk
        FOREIGN KEY (organization_id, account_id) REFERENCES treasury_accounts (organization_id, id),
    CONSTRAINT treasury_movements_reversal_link CHECK ((kind = 'Reversal') = (reverses_movement_id IS NOT NULL)),
    CONSTRAINT treasury_movements_source_ck CHECK ((source_type IS NULL) = (source_id IS NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS treasury_movements_source_uk
    ON treasury_movements (organization_id, source_type, source_id) WHERE source_type IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS treasury_movements_one_reversal_uk
    ON treasury_movements (organization_id, reverses_movement_id) WHERE reverses_movement_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS treasury_movements_account_date_idx
    ON treasury_movements (organization_id, account_id, business_date, created_at);

ALTER TABLE treasury_movements ENABLE ROW LEVEL SECURITY;
ALTER TABLE treasury_movements FORCE ROW LEVEL SECURITY;
REVOKE ALL ON treasury_movements FROM PUBLIC;
GRANT SELECT, INSERT ON treasury_movements TO app_runtime;   -- append-only: money is never rewritten
DROP POLICY IF EXISTS treasury_movements_tenant_isolation ON treasury_movements;
CREATE POLICY treasury_movements_tenant_isolation ON treasury_movements
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;
