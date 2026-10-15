-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- employee-lifecycle E2: overtime (horas extra). Each record is hours x the employee's overtime hourly rate x a
-- multiplier (a Sunday proposes x2, any other day x1); its amount is frozen when recorded. A record is pending until a
-- payroll run takes it into a payslip (`payslip_id`) as an "Horas extra" earning line (`payslip_lines.source`
-- 'Overtime'); deleting that payslip (an employee taken out of a Draft, or the Draft discarded) sets it back to pending
-- by itself (ON DELETE SET NULL on the single-column `payslips_id_uk`); a Paid run is final.
--
-- APPLIED AFTER: 0053_customer_location_reference.sql.
--
-- 1. `employees.overtime_hourly_rate`: nullable, no default (nobody has one until it is set).
-- 2. `employee_overtime`: organization scoped, row level security like the rest of the payroll.
-- 3. `payslip_lines.source` admits 'Overtime' (widened only when it does not yet, so a re-run never narrows it).
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- Rollback (manual, only while no overtime was recorded):
--   BEGIN;
--   DROP TABLE IF EXISTS employee_overtime;
--   ALTER TABLE employees DROP COLUMN IF EXISTS overtime_hourly_rate;
--   COMMIT;

BEGIN;

ALTER TABLE employees ADD COLUMN IF NOT EXISTS overtime_hourly_rate numeric(18,2) NULL
    CHECK (overtime_hourly_rate IS NULL OR overtime_hourly_rate > 0);

CREATE TABLE IF NOT EXISTS employee_overtime (
    organization_id    uuid          NOT NULL,
    id                 uuid          NOT NULL,
    employee_id        uuid          NOT NULL,
    work_date          date          NOT NULL,
    hours              numeric(5,2)  NOT NULL CHECK (hours > 0 AND hours <= 24),
    multiplier         numeric(4,2)  NOT NULL CHECK (multiplier >= 1 AND multiplier <= 3),
    hourly_rate        numeric(18,2) NOT NULL CHECK (hourly_rate > 0),
    amount             numeric(18,2) NOT NULL CHECK (amount > 0),
    note               text          NULL,
    payslip_id         uuid          NULL,
    created_by_user_id uuid          NOT NULL,
    created_at_utc     timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT employee_overtime_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT employee_overtime_employee_fk FOREIGN KEY (organization_id, employee_id) REFERENCES employees (organization_id, id),
    CONSTRAINT employee_overtime_payslip_fk FOREIGN KEY (payslip_id) REFERENCES payslips (id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS employee_overtime_employee_ix ON employee_overtime (organization_id, employee_id, work_date);
CREATE INDEX IF NOT EXISTS employee_overtime_payslip_ix ON employee_overtime (payslip_id) WHERE payslip_id IS NOT NULL;

ALTER TABLE employee_overtime ENABLE ROW LEVEL SECURITY;
ALTER TABLE employee_overtime FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON employee_overtime FROM PUBLIC;
-- Pending records are deleted by hand; a payroll sets and clears `payslip_id`.
GRANT SELECT, INSERT, UPDATE, DELETE ON employee_overtime TO app_runtime;
DROP POLICY IF EXISTS employee_overtime_tenant_isolation ON employee_overtime;
CREATE POLICY employee_overtime_tenant_isolation ON employee_overtime
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Widened only when it does not admit 'Overtime' yet: re-running this file never narrows what a later migration widened.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'payslip_lines_source_check'
                   AND pg_get_constraintdef(oid) LIKE '%Overtime%') THEN
        ALTER TABLE payslip_lines DROP CONSTRAINT IF EXISTS payslip_lines_source_check;
        ALTER TABLE payslip_lines ADD CONSTRAINT payslip_lines_source_check
            CHECK (source IN ('BaseSalary', 'Advances', 'Manual', 'Overtime'));
    END IF;
END $$;

COMMIT;
