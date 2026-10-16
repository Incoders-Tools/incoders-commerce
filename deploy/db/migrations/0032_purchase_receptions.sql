-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- purchases-receptions-and-stock (T1): goods receptions (PRD 9.6 / 9.8). A reception records the goods a supplier
-- delivered to ONE branch. It starts as a DRAFT (editable, no number), is CONFIRMED once (the application then, in the
-- same transaction, assigns its human number, moves the stock, posts the Invoice to the supplier current account and
-- records the cost history) and can later be VOIDED (compensating movements, never a delete).
--
-- APPLIED AFTER: 0030_suppliers.sql, 0031_current_account_movements.sql (and 0016/0017 for the branch-owned catalog).
--
-- BRANCH OWNED. Like the catalog (0016) and pricing (0017), a reception and its lines carry `branch_id` and the RLS
-- policies compare BOTH `app.current_org_id` and `app.current_branch_id` (USING and WITH CHECK): with no branch
-- selected nothing is visible and nothing can be written. Composite foreign keys keep the supplier inside the
-- organization and the presentation inside the branch.
--
-- NUMBER. `R{branch code}-W-{sequence}` (docs/document-numbering.md). The three parts (`branch_code`, `sequence`,
-- `number`) are present together exactly when the reception is not a draft; the sequence counts per branch and is
-- unique (`purchase_receptions_number_uk`). The application takes it under a per-branch advisory lock.
--
-- DUPLICATE GUARD. The same supplier document cannot be confirmed twice: unique (organization, supplier, document
-- type, normalized reference) among CONFIRMED receptions with a reference. A voided reception frees its document.
--
-- LINES. Replaced as a set while the reception is a draft (the application deletes and re-inserts); the trigger
-- `purchase_reception_lines_require_draft` refuses any change once it is Confirmed or Voided.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   DROP TABLE purchase_reception_lines; DROP TABLE purchase_receptions;
--   DROP FUNCTION purchase_reception_lines_require_draft();

BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS purchase_receptions (
    id                          uuid PRIMARY KEY,
    organization_id             uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    branch_id                   uuid NOT NULL,
    supplier_id                 uuid NOT NULL,
    status                      text NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft', 'Confirmed', 'Voided')),
    branch_code                 smallint NULL,
    sequence                    integer NULL,
    number                      text NULL,
    document_type               text NOT NULL CHECK (document_type IN ('Invoice', 'DeliveryNote', 'Other')),
    document_reference          text NULL CHECK (document_reference IS NULL OR btrim(document_reference) <> ''),
    occurred_on                 date NOT NULL,
    due_on                      date NULL,
    notes                       text NULL,
    total_amount                numeric(18,2) NOT NULL DEFAULT 0 CHECK (total_amount >= 0),
    ledger_invoice_movement_id  uuid NULL,
    ledger_reversal_movement_id uuid NULL,
    void_reason                 text NULL,
    created_by_user_id          uuid NOT NULL,
    created_at_utc              timestamptz NOT NULL DEFAULT now(),
    confirmed_by_user_id        uuid NULL,
    confirmed_at_utc            timestamptz NULL,
    voided_by_user_id           uuid NULL,
    voided_at_utc               timestamptz NULL,
    updated_at_utc              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT purchase_receptions_due_not_before_occurred
        CHECK (due_on IS NULL OR due_on >= occurred_on),
    CONSTRAINT purchase_receptions_numbered_ck
        CHECK ((status = 'Draft') = (number IS NULL AND sequence IS NULL AND branch_code IS NULL)
               AND (sequence IS NULL OR sequence >= 1)),
    CONSTRAINT purchase_receptions_state_ck
        CHECK ((status = 'Draft' OR confirmed_at_utc IS NOT NULL)
               AND (status <> 'Voided' OR (voided_at_utc IS NOT NULL AND void_reason IS NOT NULL AND btrim(void_reason) <> ''))),
    -- Targets of the composite foreign keys.
    CONSTRAINT purchase_receptions_org_scoped_uk UNIQUE (organization_id, id),
    CONSTRAINT purchase_receptions_branch_scoped_uk UNIQUE (organization_id, branch_id, id),
    CONSTRAINT purchase_receptions_number_uk UNIQUE (organization_id, branch_id, sequence),
    CONSTRAINT purchase_receptions_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT purchase_receptions_supplier_fk
        FOREIGN KEY (organization_id, supplier_id) REFERENCES suppliers (organization_id, id)
);

-- The same supplier document cannot be confirmed twice (see the header).
CREATE UNIQUE INDEX IF NOT EXISTS purchase_receptions_confirmed_document_uk
    ON purchase_receptions (organization_id, supplier_id, document_type, lower(btrim(document_reference)))
    WHERE status = 'Confirmed' AND document_reference IS NOT NULL;

CREATE INDEX IF NOT EXISTS purchase_receptions_branch_list_idx
    ON purchase_receptions (organization_id, branch_id, occurred_on DESC, created_at_utc DESC);
CREATE INDEX IF NOT EXISTS purchase_receptions_supplier_idx
    ON purchase_receptions (organization_id, supplier_id);

CREATE TABLE IF NOT EXISTS purchase_reception_lines (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL,
    branch_id        uuid NOT NULL,
    reception_id     uuid NOT NULL,
    presentation_id  uuid NOT NULL,
    quantity         numeric(18,3) NOT NULL CHECK (quantity > 0),
    unit_cost        numeric(18,4) NOT NULL CHECK (unit_cost >= 0),
    line_total       numeric(18,2) NOT NULL CHECK (line_total >= 0),
    lot_code         text NULL CHECK (lot_code IS NULL OR btrim(lot_code) <> ''),
    expires_on       date NULL,
    sort_order       integer NOT NULL DEFAULT 0,
    CONSTRAINT purchase_reception_lines_reception_fk
        FOREIGN KEY (organization_id, branch_id, reception_id)
        REFERENCES purchase_receptions (organization_id, branch_id, id) ON DELETE CASCADE,
    CONSTRAINT purchase_reception_lines_presentation_fk
        FOREIGN KEY (branch_id, presentation_id) REFERENCES presentations (branch_id, id)
);

CREATE INDEX IF NOT EXISTS purchase_reception_lines_reception_idx
    ON purchase_reception_lines (organization_id, branch_id, reception_id, sort_order);
CREATE INDEX IF NOT EXISTS purchase_reception_lines_presentation_idx
    ON purchase_reception_lines (branch_id, presentation_id);

CREATE OR REPLACE FUNCTION purchase_reception_lines_require_draft() RETURNS trigger AS $$
DECLARE
    reception_status text;
BEGIN
    SELECT status INTO reception_status
      FROM purchase_receptions
     WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.reception_id ELSE NEW.reception_id END;

    -- A cascading delete of the whole reception (owner maintenance) finds no parent any more: allow it.
    IF TG_OP = 'DELETE' AND reception_status IS NULL THEN
        RETURN OLD;
    END IF;

    IF reception_status IS DISTINCT FROM 'Draft' THEN
        RAISE EXCEPTION 'reception lines can only change while the reception is a draft';
    END IF;

    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS purchase_reception_lines_require_draft ON purchase_reception_lines;
CREATE TRIGGER purchase_reception_lines_require_draft
    BEFORE INSERT OR UPDATE OR DELETE ON purchase_reception_lines
    FOR EACH ROW EXECUTE FUNCTION purchase_reception_lines_require_draft();

ALTER TABLE purchase_receptions      ENABLE ROW LEVEL SECURITY;
ALTER TABLE purchase_receptions      FORCE  ROW LEVEL SECURITY;
ALTER TABLE purchase_reception_lines ENABLE ROW LEVEL SECURITY;
ALTER TABLE purchase_reception_lines FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON purchase_receptions      FROM PUBLIC;
REVOKE ALL ON purchase_reception_lines FROM PUBLIC;
-- No DELETE on a reception: a mistake is a Void. Lines are replaced as a set while the reception is a draft.
GRANT SELECT, INSERT, UPDATE         ON purchase_receptions      TO app_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON purchase_reception_lines TO app_runtime;

DROP POLICY IF EXISTS purchase_receptions_tenant_isolation ON purchase_receptions;
CREATE POLICY purchase_receptions_tenant_isolation ON purchase_receptions
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS purchase_reception_lines_tenant_isolation ON purchase_reception_lines;
CREATE POLICY purchase_reception_lines_tenant_isolation ON purchase_reception_lines
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

COMMIT;
