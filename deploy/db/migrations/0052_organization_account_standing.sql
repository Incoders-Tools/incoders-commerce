-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- organization-account-standing (T2): the inputs of `AccountStandingRules.Evaluate` (Commerce.Domain). The standing
-- itself (Active / Overdue / Suspended) is DERIVED from these on every read and never stored, so it changes on its
-- own when the business day changes.
--   - `billing_due_on`: the date the payment to the platform is due. NULL = billing not tracked, the organization
--     stays Active; every existing organization starts this way, so nothing changes for it.
--   - `billing_grace_days`: days after the due date that still work (0-90, default 30). Enforced here as well as in
--     the domain, so bad data can never reach a rule that rejects it.
--   - `suspended_at`: set when a system administrator suspends the organization by hand; wins over any date.
--
-- APPLIED AFTER: 0051_treasury_recurrences.sql.
--
-- Columns on `organizations`, like 0015 and 0035: its tenant-isolation RLS policy and the `app_runtime` grants
-- already cover new columns. Only the system-administrator endpoints write them.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE organizations DROP CONSTRAINT organizations_billing_grace_days_ck;
--   ALTER TABLE organizations DROP COLUMN suspended_at;
--   ALTER TABLE organizations DROP COLUMN billing_grace_days;
--   ALTER TABLE organizations DROP COLUMN billing_due_on;

BEGIN;

ALTER TABLE organizations
    ADD COLUMN IF NOT EXISTS billing_due_on date NULL,
    ADD COLUMN IF NOT EXISTS billing_grace_days integer NOT NULL DEFAULT 30,
    ADD COLUMN IF NOT EXISTS suspended_at timestamptz NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'organizations_billing_grace_days_ck'
    ) THEN
        ALTER TABLE organizations
            ADD CONSTRAINT organizations_billing_grace_days_ck
            CHECK (billing_grace_days BETWEEN 0 AND 90);
    END IF;
END $$;

COMMIT;
