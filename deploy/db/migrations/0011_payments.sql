-- Repo-owned, idempotent per-environment migration.
--
-- commerce-payments design.md "Fail-closed approval" / "Interfaces /
-- Contracts": `payment_entries` — the append-only payment ledger. RLS/FORCE/
-- policy mirror 0008's (`customers`) exact symmetric tenant-isolation shape,
-- including the `NULLIF(..., '')::uuid` pooler-safety hardening from 0001.
-- NEVER regress that fix.
--
-- Append-only is a GRANT property, not a convention: `app_runtime` gets
-- SELECT, INSERT only — no UPDATE, no DELETE. A reversal is a NEW row, never
-- a rewrite of an existing one (ADR-011; "Partial Payments and Reversals
-- Without History Mutation").
--
-- `customers.billing_instrument_reference` (Decision 4): a new, separately-
-- named nullable column. `payment_terms` (0008) is NOT touched by this
-- migration — no ALTER on that column at all.

CREATE TABLE IF NOT EXISTS payment_entries (
    entry_id            uuid PRIMARY KEY,
    organization_id     uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    operation_id        uuid NOT NULL UNIQUE,     -- the idempotency key (ADR-003)
    subject_kind        text NOT NULL CHECK (subject_kind IN ('Order','Sale')),
    subject_id          uuid NOT NULL,
    entry_kind          text NOT NULL CHECK (entry_kind IN ('Payment','Reversal')),
    method               text NOT NULL CHECK (method IN
                          ('Cash','AccountCredit','BankTransfer','Card','MercadoPago')),
    amount              numeric(12,2) NOT NULL CHECK (amount > 0),
    approval_state      text NOT NULL CHECK (approval_state IN
                          ('Approved','Declined','Unavailable')),
    reverses_entry_id   uuid NULL REFERENCES payment_entries (entry_id),
    provider_reference  text NULL,                -- opaque token/alias; never a PAN
    actor_id            uuid NOT NULL,
    recorded_at_utc     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payment_entries_reversal_requires_target
        CHECK ((entry_kind = 'Reversal') = (reverses_entry_id IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS payment_entries_org_idx ON payment_entries (organization_id);
CREATE INDEX IF NOT EXISTS payment_entries_subject_idx ON payment_entries (subject_kind, subject_id);

ALTER TABLE customers ADD COLUMN IF NOT EXISTS billing_instrument_reference text NULL;
ALTER TABLE customers DROP CONSTRAINT IF EXISTS customers_instrument_not_pan_shaped;
ALTER TABLE customers ADD CONSTRAINT customers_instrument_not_pan_shaped
    CHECK (billing_instrument_reference IS NULL
           OR billing_instrument_reference !~ '^[0-9]{13,19}$');

ALTER TABLE payment_entries ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment_entries FORCE ROW LEVEL SECURITY;
REVOKE ALL ON payment_entries FROM PUBLIC;
GRANT SELECT, INSERT ON payment_entries TO app_runtime; -- no UPDATE, no DELETE: append-only

-- Symmetric, identical in shape to customers_tenant_isolation (0008),
-- including the NULLIF(..., '')::uuid pooler-safety hardening from 0001.
-- NEVER regress it.
DROP POLICY IF EXISTS payment_entries_tenant_isolation ON payment_entries;
CREATE POLICY payment_entries_tenant_isolation ON payment_entries
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Inverse (rollback), shipped as comments — NOT executed by this file:
--   ALTER TABLE customers DROP CONSTRAINT customers_instrument_not_pan_shaped;
--   ALTER TABLE customers DROP COLUMN billing_instrument_reference;
--   DROP TABLE payment_entries;
-- Lossy only after first real use (settlement history has no prior model to
-- fall back to; design.md "Migration / Rollout") — prefer forward-fix then.
-- Narrower rollback: stop mapping MapPaymentEndpoints() — the domain stays
-- dormant and this table stays empty/inert.
