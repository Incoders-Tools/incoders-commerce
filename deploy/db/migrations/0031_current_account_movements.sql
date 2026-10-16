-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- current-account-movements: the generic, APPEND-ONLY current-account ledger
-- (PRD 9.13). It is designed for several kinds of party (suppliers now;
-- customers and employees later) but wired to `suppliers` only in this
-- migration. The balance is never stored: it is always derived from the
-- movements.
--
-- APPLIED AFTER: 0030_suppliers.sql.
--
-- SIGN CONVENTION. `direction` says which side of the account a movement
-- lands on; `amount` is always positive. For a SUPPLIER account the balance
-- is what the business OWES the supplier:
--     balance = sum(amount WHERE direction = 'Credit') - sum(amount WHERE direction = 'Debit')
--   Invoice, DebitNote, OpeningBalance (a positive debt)  -> Credit (owes more)
--   Payment, CreditNote                                   -> Debit  (owes less)
--   Adjustment                                            -> either, chosen explicitly
--   Reversal -> the OPPOSITE direction of the movement it reverses, same amount
-- The application owns the kind -> direction mapping; the database keeps the
-- closed vocabularies and the structural invariants below.
--
-- PARTY LINK. `party_kind` + `party_id` identify the account owner generically.
-- Referential integrity is a nullable, typed foreign key per party kind
-- (`supplier_id` today; a future `customer_id`/`employee_id` is added the same
-- way) plus a CHECK that the typed column agrees with `party_id` for its kind.
-- This keeps real composite foreign keys (same-organization guarantee) without a
-- trigger or a polymorphic, unchecked uuid.
--
-- APPEND-ONLY. `app_runtime` gets SELECT and INSERT only: a movement can never
-- be updated or deleted by the application. A mistake is corrected with a
-- `Reversal` movement that points at the original (`reverses_movement_id`).
-- Database-enforced reversal rules: a reversal names a movement and only a
-- reversal does (`..._reversal_link`); a movement is reversed at most once
-- (partial unique index); a reversal can not itself be reversed (the composite
-- foreign key also matches the target's generated `is_reversible` flag, which is
-- false for a Reversal). That the reversal belongs to the same supplier and
-- mirrors amount/direction is enforced by the API.
--
-- RLS: same shape as `customers` (0008): the policy compares only
-- `organization_id`. Composite foreign keys keep a movement inside its
-- organization's supplier and reversal target.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   DROP TABLE current_account_movements;

BEGIN;

CREATE TABLE IF NOT EXISTS current_account_movements (
    id                    uuid PRIMARY KEY,
    organization_id       uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    party_kind            text NOT NULL CHECK (party_kind IN ('Supplier')),
    party_id              uuid NOT NULL,
    supplier_id           uuid NULL,
    kind                  text NOT NULL CHECK (kind IN (
        'OpeningBalance', 'Invoice', 'DebitNote', 'CreditNote', 'Payment', 'Adjustment', 'Reversal')),
    direction             text NOT NULL CHECK (direction IN ('Debit', 'Credit')),
    amount                numeric(18,2) NOT NULL CHECK (amount > 0),
    occurred_on           date NOT NULL,
    due_on                date NULL,
    document_reference    text NULL,
    concept               text NOT NULL CHECK (btrim(concept) <> ''),
    reverses_movement_id  uuid NULL,
    created_at_utc        timestamptz NOT NULL DEFAULT now(),
    created_by_user_id    uuid NOT NULL,
    -- Generated helpers of the reversal rules (see the header).
    is_reversible         boolean GENERATED ALWAYS AS (kind <> 'Reversal') STORED,
    reverses_reversible   boolean GENERATED ALWAYS AS (CASE WHEN reverses_movement_id IS NULL THEN NULL ELSE true END) STORED,
    CONSTRAINT current_account_movements_due_not_before_occurred
        CHECK (due_on IS NULL OR due_on >= occurred_on),
    CONSTRAINT current_account_movements_party_supplier
        CHECK (party_kind <> 'Supplier' OR supplier_id = party_id),
    CONSTRAINT current_account_movements_reversal_link
        CHECK ((kind = 'Reversal') = (reverses_movement_id IS NOT NULL)),
    -- Targets of the composite foreign keys (own and the reversal self reference).
    CONSTRAINT current_account_movements_org_scoped_uk UNIQUE (organization_id, id),
    CONSTRAINT current_account_movements_reversal_target_uk UNIQUE (organization_id, id, is_reversible),
    CONSTRAINT current_account_movements_supplier_fk
        FOREIGN KEY (organization_id, supplier_id)
        REFERENCES suppliers (organization_id, id),
    CONSTRAINT current_account_movements_reverses_fk
        FOREIGN KEY (organization_id, reverses_movement_id, reverses_reversible)
        REFERENCES current_account_movements (organization_id, id, is_reversible)
);

-- At most one reversal per movement.
CREATE UNIQUE INDEX IF NOT EXISTS current_account_movements_one_reversal_uk
    ON current_account_movements (reverses_movement_id) WHERE reverses_movement_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS current_account_movements_supplier_idx
    ON current_account_movements (organization_id, supplier_id, occurred_on, created_at_utc);
CREATE INDEX IF NOT EXISTS current_account_movements_org_created
    ON current_account_movements (organization_id, created_at_utc);

ALTER TABLE current_account_movements ENABLE ROW LEVEL SECURITY;
ALTER TABLE current_account_movements FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON current_account_movements FROM PUBLIC;
GRANT SELECT, INSERT ON current_account_movements TO app_runtime;

DROP POLICY IF EXISTS current_account_movements_tenant_isolation ON current_account_movements;
CREATE POLICY current_account_movements_tenant_isolation ON current_account_movements
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;
