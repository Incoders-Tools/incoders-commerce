-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- POS sale voids: a terminal can void (annul) a sale of its open cash session,
-- authorized with the branch PIN. The sale itself is never rewritten (`pos_sales`
-- is append-only): the void arrives as its own `sale.voided` envelope and is
-- projected here, one row per voided sale, next to an audit row `sale.voided`.
-- The stock the sale took out is put back with `Reversal` movements of source
-- `PosSaleVoid` (reserved since 0033), one per original `PosSale` movement.
-- APPLIED AFTER: 0043_price_entry_same_day_correction.sql.
--
--   sale_id        the voided sale. No foreign key to pos_sales: the void may be
--                  ingested before its sale (the terminal pushes in order, but a
--                  failed push of the sale does not stop the next one).
--   reason         required, what the operator typed (at most 200 characters on
--                  the terminal; plain text here so ingestion never fails on it).
--   authorized_by  the operator whose PIN authorized the void, and the PIN
--                  version, as for a discount.
--
-- Append-only like `pos_sales` (0023): app_runtime gets SELECT, INSERT, no UPDATE
-- or DELETE; a void is never undone. FORCE ROW LEVEL SECURITY with the symmetric
-- tenant-isolation policy (NULLIF pooler-safety hardening from 0001 - never regress
-- it). The composite foreign key to branches follows 0014/0016/0023.
--
-- Deploy order: apply this BEFORE the API version that projects voids. An API
-- deployed ahead of it still ingests every void (the projection runs in a savepoint
-- and is skipped with a log line); the envelope stays in `sync_inbox`.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS pos_sale_voids;
--   COMMIT;

BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS pos_sale_voids (
    organization_id       uuid        NOT NULL,
    branch_id             uuid        NOT NULL,
    sale_id               uuid        NOT NULL,
    operation_id          uuid        NOT NULL,
    voided_at_utc         timestamptz NOT NULL,
    voided_by_operator_id uuid        NOT NULL,
    authorized_by         uuid        NOT NULL,
    pin_version           bigint      NOT NULL,
    reason                text        NOT NULL,
    total_amount          numeric     NOT NULL,
    cash_session_id       uuid        NULL,
    recorded_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pos_sale_voids_pk PRIMARY KEY (organization_id, sale_id),
    CONSTRAINT pos_sale_voids_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS pos_sale_voids_branch_time_idx
    ON pos_sale_voids (organization_id, branch_id, voided_at_utc);

ALTER TABLE pos_sale_voids ENABLE ROW LEVEL SECURITY;
ALTER TABLE pos_sale_voids FORCE ROW LEVEL SECURITY;
REVOKE ALL ON pos_sale_voids FROM PUBLIC;
GRANT SELECT, INSERT ON pos_sale_voids TO app_runtime;   -- no UPDATE, no DELETE: append-only

DROP POLICY IF EXISTS pos_sale_voids_tenant_isolation ON pos_sale_voids;
CREATE POLICY pos_sale_voids_tenant_isolation ON pos_sale_voids
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;
