-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- purchases-receptions-and-stock (T1): the stock ledger of a branch, its minimum levels and the purchase cost history.
--
-- APPLIED AFTER: 0032_purchase_receptions.sql (and 0016/0017 for the branch-owned presentations).
--
-- STOCK IS CLOUD-AUTHORITATIVE and DERIVED. `stock_movements` is an APPEND-ONLY ledger; the on-hand quantity of a
-- presentation is never stored, it is SUM(quantity) of its movements in the branch (index
-- `stock_movements_on_hand_idx`). `app_runtime` gets SELECT and INSERT only: a movement can never be updated or
-- deleted. A mistake is corrected with a compensating movement (`Reversal`, which points at the original through
-- `reverses_movement_id`; a movement is reversed at most once and a reversal cannot be reversed) or a manual
-- adjustment.
--
-- SIGN CONVENTION. `quantity` is SIGNED, in the presentation's unit (units or kg): Opening and PurchaseReceipt are
-- positive, Sale and Shrinkage are negative (`stock_movements_sign_ck`), Adjustment, CountCorrection and Reversal
-- are either sign, and no movement is zero. Stock may go negative (the POS warns, it does not block).
--
-- IDEMPOTENCY KEY. A movement derived from a document line carries (`source_type`, `source_id`, `source_line_id`):
-- the document kind, the document id and the line id. UNIQUE (organization, branch, source_type, source_line_id) makes
-- projecting the same line twice a no-op for the writer (`INSERT ... ON CONFLICT DO NOTHING`): the synced POS sale
-- line uses source_type 'PosSale' (source_id = sale id, source_line_id = the sale line id). The compensating movement
-- of a void uses ANOTHER source_type for the same line ('PurchaseReceptionVoid', 'PosSaleVoid'), so a void never
-- collides with the original. Manual movements have no source.
--
-- BRANCH OWNED: org AND branch RLS (USING and WITH CHECK), exactly the pattern of 0016/0017; the presentation must
-- belong to the same branch (composite foreign key).
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   DROP TABLE presentation_costs; DROP TABLE stock_minimums; DROP TABLE stock_movements;

BEGIN;

CREATE TABLE IF NOT EXISTS stock_movements (
    id                    uuid PRIMARY KEY,
    organization_id       uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    branch_id             uuid NOT NULL,
    presentation_id       uuid NOT NULL,
    quantity              numeric(18,3) NOT NULL,
    kind                  text NOT NULL CHECK (kind IN (
        'Opening', 'PurchaseReceipt', 'Sale', 'Adjustment', 'Shrinkage', 'CountCorrection', 'Reversal')),
    source_type           text NULL,
    source_id             uuid NULL,
    source_line_id        uuid NULL,
    reverses_movement_id  uuid NULL,
    reason                text NULL,
    lot_code              text NULL,
    occurred_at_utc       timestamptz NOT NULL,
    created_at_utc        timestamptz NOT NULL DEFAULT now(),
    created_by_user_id    uuid NULL,
    -- Generated helpers of the reversal rules (same trick as current_account_movements).
    is_reversible         boolean GENERATED ALWAYS AS (kind <> 'Reversal') STORED,
    reverses_reversible   boolean GENERATED ALWAYS AS (CASE WHEN reverses_movement_id IS NULL THEN NULL ELSE true END) STORED,
    CONSTRAINT stock_movements_quantity_nonzero_ck CHECK (quantity <> 0),
    CONSTRAINT stock_movements_sign_ck CHECK (
        CASE kind
            WHEN 'Opening'         THEN quantity > 0
            WHEN 'PurchaseReceipt' THEN quantity > 0
            WHEN 'Sale'            THEN quantity < 0
            WHEN 'Shrinkage'       THEN quantity < 0
            ELSE true
        END),
    CONSTRAINT stock_movements_source_ck CHECK (
        (source_type IS NULL) = (source_id IS NULL)
        AND (source_line_id IS NULL OR source_type IS NOT NULL)
        AND (source_type IS NULL OR btrim(source_type) <> '')),
    CONSTRAINT stock_movements_reversal_link CHECK ((kind = 'Reversal') = (reverses_movement_id IS NOT NULL)),
    CONSTRAINT stock_movements_branch_scoped_uk UNIQUE (organization_id, branch_id, id),
    CONSTRAINT stock_movements_reversal_target_uk UNIQUE (organization_id, branch_id, id, is_reversible),
    CONSTRAINT stock_movements_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT stock_movements_presentation_fk
        FOREIGN KEY (branch_id, presentation_id) REFERENCES presentations (branch_id, id),
    CONSTRAINT stock_movements_reverses_fk
        FOREIGN KEY (organization_id, branch_id, reverses_movement_id, reverses_reversible)
        REFERENCES stock_movements (organization_id, branch_id, id, is_reversible)
);

-- At most one reversal per movement.
CREATE UNIQUE INDEX IF NOT EXISTS stock_movements_one_reversal_uk
    ON stock_movements (reverses_movement_id) WHERE reverses_movement_id IS NOT NULL;

-- Idempotency key of the movements derived from a document line (see the header).
CREATE UNIQUE INDEX IF NOT EXISTS stock_movements_source_line_uk
    ON stock_movements (organization_id, branch_id, source_type, source_line_id) WHERE source_line_id IS NOT NULL;

-- On hand = SUM(quantity) per presentation, answered from the index.
CREATE INDEX IF NOT EXISTS stock_movements_on_hand_idx
    ON stock_movements (organization_id, branch_id, presentation_id) INCLUDE (quantity, occurred_at_utc);
-- Movement history of a presentation, newest first.
CREATE INDEX IF NOT EXISTS stock_movements_history_idx
    ON stock_movements (organization_id, branch_id, presentation_id, occurred_at_utc DESC, id DESC);
CREATE INDEX IF NOT EXISTS stock_movements_source_idx
    ON stock_movements (organization_id, branch_id, source_type, source_id);

CREATE TABLE IF NOT EXISTS stock_minimums (
    organization_id    uuid NOT NULL,
    branch_id          uuid NOT NULL,
    presentation_id    uuid NOT NULL,
    minimum_quantity   numeric(18,3) NOT NULL CHECK (minimum_quantity >= 0),
    updated_at_utc     timestamptz NOT NULL DEFAULT now(),
    updated_by_user_id uuid NULL,
    CONSTRAINT stock_minimums_pk PRIMARY KEY (organization_id, branch_id, presentation_id),
    CONSTRAINT stock_minimums_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT stock_minimums_presentation_fk
        FOREIGN KEY (branch_id, presentation_id) REFERENCES presentations (branch_id, id) ON DELETE CASCADE
);

-- Purchase cost history: one row per received line, append-only. The current cost of a presentation is its latest
-- row whose reception is still Confirmed (a voided reception stops counting).
CREATE TABLE IF NOT EXISTS presentation_costs (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL,
    branch_id          uuid NOT NULL,
    presentation_id    uuid NOT NULL,
    supplier_id        uuid NOT NULL,
    reception_id       uuid NOT NULL,
    reception_line_id  uuid NULL,
    unit_cost          numeric(18,4) NOT NULL CHECK (unit_cost >= 0),
    occurred_on        date NOT NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT presentation_costs_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT presentation_costs_presentation_fk
        FOREIGN KEY (branch_id, presentation_id) REFERENCES presentations (branch_id, id),
    CONSTRAINT presentation_costs_supplier_fk
        FOREIGN KEY (organization_id, supplier_id) REFERENCES suppliers (organization_id, id),
    CONSTRAINT presentation_costs_reception_fk
        FOREIGN KEY (organization_id, branch_id, reception_id)
        REFERENCES purchase_receptions (organization_id, branch_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS presentation_costs_line_uk
    ON presentation_costs (reception_line_id) WHERE reception_line_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS presentation_costs_presentation_idx
    ON presentation_costs (organization_id, branch_id, presentation_id, occurred_on DESC);

ALTER TABLE stock_movements    ENABLE ROW LEVEL SECURITY;
ALTER TABLE stock_movements    FORCE  ROW LEVEL SECURITY;
ALTER TABLE stock_minimums     ENABLE ROW LEVEL SECURITY;
ALTER TABLE stock_minimums     FORCE  ROW LEVEL SECURITY;
ALTER TABLE presentation_costs ENABLE ROW LEVEL SECURITY;
ALTER TABLE presentation_costs FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON stock_movements    FROM PUBLIC;
REVOKE ALL ON stock_minimums     FROM PUBLIC;
REVOKE ALL ON presentation_costs FROM PUBLIC;
GRANT SELECT, INSERT         ON stock_movements    TO app_runtime;   -- no UPDATE, no DELETE: append-only
GRANT SELECT, INSERT, UPDATE ON stock_minimums     TO app_runtime;
GRANT SELECT, INSERT         ON presentation_costs TO app_runtime;   -- append-only

DROP POLICY IF EXISTS stock_movements_tenant_isolation ON stock_movements;
CREATE POLICY stock_movements_tenant_isolation ON stock_movements
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS stock_minimums_tenant_isolation ON stock_minimums;
CREATE POLICY stock_minimums_tenant_isolation ON stock_minimums
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS presentation_costs_tenant_isolation ON presentation_costs;
CREATE POLICY presentation_costs_tenant_isolation ON presentation_costs
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

COMMIT;
