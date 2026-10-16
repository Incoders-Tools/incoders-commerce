-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- employee-lifecycle E3: the reason of an employee's leave ("Gestionar baja": date and reason). `termination_date`
-- already exists (0050); the reason sits next to it and is cleared with it when the employee is reinstated.
--
-- APPLIED AFTER: 0054_employee_overtime.sql.
--
-- Nullable, no default: nobody given the leave before has one. The `employees` RLS policy and the `app_runtime` grants
-- already cover a new column.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE employees DROP COLUMN termination_reason;

BEGIN;

ALTER TABLE employees ADD COLUMN IF NOT EXISTS termination_reason text NULL
    CHECK (termination_reason IS NULL OR length(termination_reason) <= 500);

COMMIT;
