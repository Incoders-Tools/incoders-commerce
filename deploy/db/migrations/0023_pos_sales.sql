-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- pos-scan-sale "Sale Number" / branch-offline-sync "Sale Number Projection":
-- `pos_sales` is the server-side projection of POS sales. Until now a sale only
-- existed as a jsonb `sync_inbox` row; this table gives each sale a real row and,
-- above all, makes the human sale number `V{branch}-C{register}-{sequence}`
-- (`V01-C2-125`) UNIQUE per organization.
-- APPLIED AFTER: 0022_terminal_registers.sql.
--
-- The terminal numbers sales OFFLINE from its register and a local counter, so the
-- server can only verify, never assign. The ingestion transaction projects each
-- sale envelope here and validates the claimed number against the registry
-- (`terminal_registers`: that installation held that register in that branch) and
-- against `branches.code`. A number that fails validation or collides with another
-- sale is stored as NULL and an audit row `sale.number_conflict` records the claim:
-- the sale itself is ALWAYS ingested, a numbering problem never blocks the sync.
--
--   register_number / sale_sequence   NULL for sales made before numbering existed,
--                                     by a terminal that did not know its register,
--                                     or whose claim was rejected.
--   uniqueness                        partial UNIQUE index over (organization,
--                                     branch, register, sequence) WHERE the
--                                     sequence is not null, so unnumbered sales
--                                     never collide with each other.
--   total_amount                      plain numeric: the projection must never
--                                     fail on an amount the terminal accepted.
--
-- Append-only like `payment_entries` (0011): app_runtime gets SELECT, INSERT, no
-- UPDATE or DELETE; a sale is never rewritten. FORCE ROW LEVEL SECURITY with the
-- symmetric tenant-isolation policy (NULLIF pooler-safety hardening from 0001 -
-- never regress it). The composite foreign key to branches follows 0014/0016.
--
-- Deploy order: apply this BEFORE the API version that projects sales. An API
-- deployed ahead of it still ingests every sale (the projection runs in a
-- savepoint and is skipped with a log line), but sales received in that window have
-- no `pos_sales` row (they remain in `sync_inbox` and can be projected afterwards).
-- Prefer applying the migration first.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS pos_sales;
--   COMMIT;

BEGIN;

-- Same guard 0016/0022 install (the composite foreign key needs a unique
-- constraint over exactly its target columns); a no-op where they already ran.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS pos_sales (
    organization_id uuid        NOT NULL,
    branch_id       uuid        NOT NULL,
    sale_id         uuid        NOT NULL,
    register_number smallint    NULL,
    sale_sequence   integer     NULL,
    operation_id    uuid        NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    total_amount    numeric     NOT NULL,
    recorded_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pos_sales_pk PRIMARY KEY (organization_id, sale_id),
    -- Both parts or neither (a CHECK passes on NULL, so the pair is compared explicitly).
    CONSTRAINT pos_sales_number_ck CHECK (
        (register_number IS NULL) = (sale_sequence IS NULL)
        AND (register_number IS NULL OR (register_number BETWEEN 1 AND 999 AND sale_sequence >= 1))),
    CONSTRAINT pos_sales_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

-- A sale number is unique per organization and branch; unnumbered sales are exempt.
CREATE UNIQUE INDEX IF NOT EXISTS pos_sales_number_uk
    ON pos_sales (organization_id, branch_id, register_number, sale_sequence)
    WHERE sale_sequence IS NOT NULL;

CREATE INDEX IF NOT EXISTS pos_sales_branch_time_idx
    ON pos_sales (organization_id, branch_id, occurred_at_utc);

ALTER TABLE pos_sales ENABLE ROW LEVEL SECURITY;
ALTER TABLE pos_sales FORCE ROW LEVEL SECURITY;
REVOKE ALL ON pos_sales FROM PUBLIC;
GRANT SELECT, INSERT ON pos_sales TO app_runtime;   -- no UPDATE, no DELETE: append-only

DROP POLICY IF EXISTS pos_sales_tenant_isolation ON pos_sales;
CREATE POLICY pos_sales_tenant_isolation ON pos_sales
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;
