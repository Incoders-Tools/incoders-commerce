-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Recurring treasury movements: fixed expenses (electricity, gas, internet, phone, rent...) and recurring income, recorded
-- automatically on their dates.
-- APPLIED AFTER: 0050_employees_and_payroll.sql.
--
-- A recurrence (`treasury_recurrences`) says: money In or Out of an account, an amount and a concept, every N weeks,
-- months or years from a start date (the weekday, day of the month or date of the year come from it; a day the month
-- does not have becomes its last day), and when it ends: never, on a date, or after a number of occurrences. It can be
-- paused. Editing its amount, concept or account changes what comes next; what was already recorded stays. Only dates
-- from `generate_from` on are recorded: the creation day unless the past dates since the start were asked for, and the
-- day it is resumed after a pause (the dates while paused are not recorded).
--
-- Its occurrences are ordinary treasury movements (ManualIn / ManualOut) that point at it (`recurrence_id`) and carry the
-- source `TreasuryRecurrence` with an id derived from the recurrence and the date, so each date is recorded ONCE no
-- matter how often the generation runs (when the treasury is opened, and by the daily job). Like any manual movement an
-- occurrence can be edited or voided; a voided one is not recorded again.
--
-- RLS: org-scoped with FORCE ROW LEVEL SECURITY and the symmetric tenant-isolation policy (NULLIF pooler-safety
-- hardening from 0001). Recurrences are deactivated, never deleted.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   ALTER TABLE treasury_movements DROP COLUMN IF EXISTS recurrence_id;
--   DROP TABLE IF EXISTS treasury_recurrences;
--   COMMIT;

BEGIN;

CREATE TABLE IF NOT EXISTS treasury_recurrences (
    organization_id      uuid          NOT NULL,
    id                   uuid          NOT NULL,
    account_id           uuid          NOT NULL,
    direction            text          NOT NULL CHECK (direction IN ('In', 'Out')),
    amount               numeric(18,2) NOT NULL CHECK (amount > 0),
    concept              text          NOT NULL CHECK (btrim(concept) <> '' AND char_length(concept) <= 200),
    document_reference   text          NULL CHECK (document_reference IS NULL OR char_length(document_reference) <= 60),
    frequency            text          NOT NULL CHECK (frequency IN ('Weekly', 'Monthly', 'Yearly')),
    interval_count       integer       NOT NULL DEFAULT 1 CHECK (interval_count BETWEEN 1 AND 24),
    start_date           date          NOT NULL,
    end_mode             text          NOT NULL DEFAULT 'Never' CHECK (end_mode IN ('Never', 'OnDate', 'AfterCount')),
    end_date             date          NULL,
    max_occurrences      integer       NULL CHECK (max_occurrences IS NULL OR max_occurrences BETWEEN 1 AND 1000),
    is_active            boolean       NOT NULL DEFAULT true,
    generate_from        date          NOT NULL,
    created_by_user_id   uuid          NOT NULL,
    created_at_utc       timestamptz   NOT NULL DEFAULT now(),
    updated_at_utc       timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT treasury_recurrences_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT treasury_recurrences_account_fk FOREIGN KEY (organization_id, account_id) REFERENCES treasury_accounts (organization_id, id),
    CONSTRAINT treasury_recurrences_end_ck CHECK (
        (end_mode = 'Never' AND end_date IS NULL AND max_occurrences IS NULL)
        OR (end_mode = 'OnDate' AND end_date IS NOT NULL AND end_date >= start_date AND max_occurrences IS NULL)
        OR (end_mode = 'AfterCount' AND max_occurrences IS NOT NULL AND end_date IS NULL))
);

ALTER TABLE treasury_recurrences ADD COLUMN IF NOT EXISTS generate_from date NOT NULL DEFAULT CURRENT_DATE;

ALTER TABLE treasury_recurrences ENABLE ROW LEVEL SECURITY;
ALTER TABLE treasury_recurrences FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON treasury_recurrences FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON treasury_recurrences TO app_runtime;
DROP POLICY IF EXISTS treasury_recurrences_tenant_isolation ON treasury_recurrences;
CREATE POLICY treasury_recurrences_tenant_isolation ON treasury_recurrences
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

ALTER TABLE treasury_movements ADD COLUMN IF NOT EXISTS recurrence_id uuid NULL;
CREATE INDEX IF NOT EXISTS treasury_movements_recurrence_idx
    ON treasury_movements (organization_id, recurrence_id, business_date) WHERE recurrence_id IS NOT NULL;

COMMIT;
