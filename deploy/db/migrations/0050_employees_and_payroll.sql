-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Staff (personal) and the internal payroll (liquidación de sueldos) of each branch.
-- APPLIED AFTER: 0049_category_pos_rail_and_run_discard.sql.
--
-- SCOPE (PRD 9.19): the staff file (legajo, contact, position, branch, status), each employee's current account
-- (advances, purchases, deductions, salary owed and paid) and the INTERNAL payroll: what the owner pays each employee
-- for a period. It is NOT a legal payroll: no social security contributions, union dues or legal payslips (the
-- accountant keeps doing those; PRD 9.19 leaves them out until a legal and accounting analysis).
--
-- 1. POSITIONS (`employee_roles`, "puestos": carnicero, cajero, repartidor...): an organization catalog with the shape and
--    rules of the other catalogs (`supplier_categories`, 0030): name + key unique, order, active/inactive, never deleted.
--
-- 2. EMPLOYEES (`employees`): one per person, of one branch, with a file number (`file_number`, legajo) unique in the
--    organization, the agreed pay (`base_salary` per `pay_frequency`: Monthly, Biweekly, Weekly), and dates. An employee
--    who buys goods at the counter has a linked customer (`customer_id`): the POS sells to it on current account as to any
--    customer (stock, sale, its account), and the payroll deducts that debt from the salary. Never deleted: an employee
--    who leaves is deactivated (`is_active`, `termination_date`) so the history stays readable.
--
-- 3. THE EMPLOYEE'S CURRENT ACCOUNT: `current_account_movements` admits party_kind 'Employee' with its `employee_id`. It
--    reads like a supplier's: the balance is what the business OWES the employee. The salary of a period is an Invoice
--    (Credit, the business owes it), an advance a Payment made beforehand (Debit), the goods bought and other deductions an
--    Adjustment (Debit), and the salary paid a Payment (Debit). A negative balance is what the employee owes.
--
-- 4. PAYROLL (`payroll_runs`, `payslips`, `payslip_lines`): a run is one branch and one period, numbered per branch. It is
--    prepared as a Draft (one payslip per active employee, with their base salary, their pending advances and their
--    purchases to deduct; earnings and deductions can be added and the purchases discount percentage set), and then
--    Paid in one step: each payslip posts its salary, deductions and payment on the employee's account, settles the
--    purchases on the linked customer's account (what is deducted as a payment, the discount the owner grants as a credit
--    note) and takes the net pay out of the chosen treasury account. A Draft can be discarded; a Paid run is final.
--
-- 5. TREASURY: movement kinds `SalaryPayment` (net pay) and `EmployeeAdvance` (an advance handed out).
--
-- RLS: every table is org-scoped with FORCE ROW LEVEL SECURITY and the symmetric tenant-isolation policy (NULLIF
-- pooler-safety hardening from 0001).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file (only while no employee movement exists):
--   BEGIN;
--   DROP TABLE IF EXISTS payslip_lines, payslips, payroll_runs;
--   ALTER TABLE current_account_movements DROP CONSTRAINT IF EXISTS current_account_movements_employee_fk,
--       DROP CONSTRAINT IF EXISTS current_account_movements_party_employee, DROP COLUMN IF EXISTS employee_id;
--   DROP TABLE IF EXISTS employees, employee_roles;
--   COMMIT;

BEGIN;

-- ---- 1. positions --------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS employee_roles (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    key             text NOT NULL CHECK (btrim(key) <> ''),
    sort_order      integer NOT NULL DEFAULT 0,
    is_active       boolean NOT NULL DEFAULT true,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT employee_roles_org_scoped_uk UNIQUE (organization_id, id)
);
CREATE UNIQUE INDEX IF NOT EXISTS employee_roles_org_name_uk ON employee_roles (organization_id, lower(btrim(name)));
CREATE UNIQUE INDEX IF NOT EXISTS employee_roles_org_key_uk ON employee_roles (organization_id, key);

ALTER TABLE employee_roles ENABLE ROW LEVEL SECURITY;
ALTER TABLE employee_roles FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON employee_roles FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON employee_roles TO app_runtime;
DROP POLICY IF EXISTS employee_roles_tenant_isolation ON employee_roles;
CREATE POLICY employee_roles_tenant_isolation ON employee_roles
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ---- 2. employees --------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS employees (
    organization_id  uuid          NOT NULL,
    id               uuid          NOT NULL,
    branch_id        uuid          NOT NULL,
    file_number      integer       NOT NULL CHECK (file_number > 0),
    first_name       text          NOT NULL CHECK (btrim(first_name) <> '' AND char_length(first_name) <= 100),
    last_name        text          NOT NULL CHECK (btrim(last_name) <> '' AND char_length(last_name) <= 100),
    document_number  text          NULL CHECK (document_number IS NULL OR document_number ~ '^[0-9]{6,11}$'),
    cuil             text          NULL CHECK (cuil IS NULL OR cuil ~ '^[0-9]{11}$'),
    role_id          uuid          NULL,
    phone            text          NULL CHECK (phone IS NULL OR char_length(phone) <= 40),
    email            text          NULL CHECK (email IS NULL OR char_length(email) <= 200),
    address          text          NULL CHECK (address IS NULL OR char_length(address) <= 200),
    hire_date        date          NULL,
    termination_date date          NULL,
    pay_frequency    text          NOT NULL DEFAULT 'Monthly' CHECK (pay_frequency IN ('Monthly', 'Biweekly', 'Weekly')),
    base_salary      numeric(18,2) NOT NULL DEFAULT 0 CHECK (base_salary >= 0),
    customer_id      uuid          NULL,
    notes            text          NULL CHECK (notes IS NULL OR char_length(notes) <= 1000),
    is_active        boolean       NOT NULL DEFAULT true,
    created_at_utc   timestamptz   NOT NULL DEFAULT now(),
    updated_at_utc   timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT employees_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT employees_id_uk UNIQUE (id),
    CONSTRAINT employees_file_number_uk UNIQUE (organization_id, file_number),
    CONSTRAINT employees_customer_uk UNIQUE (organization_id, customer_id),
    CONSTRAINT employees_branch_fk FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT employees_role_fk FOREIGN KEY (organization_id, role_id) REFERENCES employee_roles (organization_id, id),
    CONSTRAINT employees_customer_fk FOREIGN KEY (organization_id, customer_id) REFERENCES customers (organization_id, id),
    CONSTRAINT employees_dates_ck CHECK (termination_date IS NULL OR hire_date IS NULL OR termination_date >= hire_date)
);
CREATE INDEX IF NOT EXISTS employees_branch_idx ON employees (organization_id, branch_id, is_active);

ALTER TABLE employees ENABLE ROW LEVEL SECURITY;
ALTER TABLE employees FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON employees FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON employees TO app_runtime;   -- never deleted: deactivated
DROP POLICY IF EXISTS employees_tenant_isolation ON employees;
CREATE POLICY employees_tenant_isolation ON employees
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ---- 3. the employee's current account ------------------------------------------------------

ALTER TABLE current_account_movements ADD COLUMN IF NOT EXISTS employee_id uuid NULL;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_party_kind_ck'
                   AND pg_get_constraintdef(oid) LIKE '%Employee%') THEN
        ALTER TABLE current_account_movements DROP CONSTRAINT IF EXISTS current_account_movements_party_kind_ck;
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_party_kind_ck
            CHECK (party_kind IN ('Supplier', 'Customer', 'Employee'));
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_party_employee') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_party_employee
            CHECK (party_kind <> 'Employee' OR employee_id = party_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_employee_fk') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_employee_fk
            FOREIGN KEY (organization_id, employee_id) REFERENCES employees (organization_id, id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS current_account_movements_employee_idx
    ON current_account_movements (organization_id, employee_id, occurred_on, created_at_utc) WHERE employee_id IS NOT NULL;

-- ---- 4. payroll ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS payroll_runs (
    organization_id    uuid        NOT NULL,
    id                 uuid        NOT NULL,
    branch_id          uuid        NOT NULL,
    run_number         integer     NOT NULL CHECK (run_number > 0),
    period_from        date        NOT NULL,
    period_to          date        NOT NULL,
    pay_frequency      text        NULL CHECK (pay_frequency IS NULL OR pay_frequency IN ('Monthly', 'Biweekly', 'Weekly')),
    status             text        NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft', 'Paid')),
    notes              text        NULL CHECK (notes IS NULL OR char_length(notes) <= 500),
    paid_on            date        NULL,
    paid_at_utc        timestamptz NULL,
    payment_account_id uuid        NULL,
    created_by_user_id uuid        NOT NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payroll_runs_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT payroll_runs_id_uk UNIQUE (id),
    CONSTRAINT payroll_runs_number_uk UNIQUE (organization_id, branch_id, run_number),
    CONSTRAINT payroll_runs_period_ck CHECK (period_to >= period_from),
    CONSTRAINT payroll_runs_paid_ck CHECK ((status = 'Paid') = (paid_at_utc IS NOT NULL)),
    CONSTRAINT payroll_runs_branch_fk FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT payroll_runs_account_fk FOREIGN KEY (organization_id, payment_account_id) REFERENCES treasury_accounts (organization_id, id)
);

CREATE TABLE IF NOT EXISTS payslips (
    organization_id            uuid          NOT NULL,
    id                         uuid          NOT NULL,
    run_id                     uuid          NOT NULL,
    employee_id                uuid          NOT NULL,
    purchases_amount           numeric(18,2) NOT NULL DEFAULT 0 CHECK (purchases_amount >= 0),
    purchases_discount_percent numeric(5,2)  NOT NULL DEFAULT 0 CHECK (purchases_discount_percent BETWEEN 0 AND 100),
    CONSTRAINT payslips_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT payslips_id_uk UNIQUE (id),
    CONSTRAINT payslips_employee_uk UNIQUE (organization_id, run_id, employee_id),
    CONSTRAINT payslips_run_fk FOREIGN KEY (organization_id, run_id) REFERENCES payroll_runs (organization_id, id) ON DELETE CASCADE,
    CONSTRAINT payslips_employee_fk FOREIGN KEY (organization_id, employee_id) REFERENCES employees (organization_id, id)
);

CREATE TABLE IF NOT EXISTS payslip_lines (
    organization_id uuid          NOT NULL,
    payslip_id      uuid          NOT NULL,
    line_no         integer       NOT NULL CHECK (line_no > 0),
    kind            text          NOT NULL CHECK (kind IN ('Earning', 'Deduction')),
    source          text          NOT NULL CHECK (source IN ('BaseSalary', 'Advances', 'Manual')),
    concept         text          NOT NULL CHECK (btrim(concept) <> '' AND char_length(concept) <= 200),
    amount          numeric(18,2) NOT NULL CHECK (amount > 0),
    CONSTRAINT payslip_lines_pk PRIMARY KEY (organization_id, payslip_id, line_no),
    CONSTRAINT payslip_lines_payslip_fk FOREIGN KEY (organization_id, payslip_id) REFERENCES payslips (organization_id, id) ON DELETE CASCADE
);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['payroll_runs', 'payslips', 'payslip_lines'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('REVOKE ALL ON %I FROM PUBLIC', t);
        EXECUTE format('DROP POLICY IF EXISTS %I ON %I', t || '_tenant_isolation', t);
        EXECUTE format(
            'CREATE POLICY %I ON %I USING (organization_id = NULLIF(current_setting(''app.current_org_id'', true), '''')::uuid) '
            || 'WITH CHECK (organization_id = NULLIF(current_setting(''app.current_org_id'', true), '''')::uuid)',
            t || '_tenant_isolation', t);
    END LOOP;
END $$;
-- A Draft is edited (its payslips and lines replaced) and can be discarded; Paid is final.
GRANT SELECT, INSERT, UPDATE, DELETE ON payroll_runs, payslips, payslip_lines TO app_runtime;

-- ---- 5. treasury kinds -----------------------------------------------------------------------

-- Widened only when it does not admit these kinds yet: re-running this file never narrows what a later migration widened.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'treasury_movements_kind_ck'
                   AND pg_get_constraintdef(oid) LIKE '%SalaryPayment%') THEN
        ALTER TABLE treasury_movements DROP CONSTRAINT IF EXISTS treasury_movements_kind_ck;
        ALTER TABLE treasury_movements ADD CONSTRAINT treasury_movements_kind_ck
            CHECK (kind IN ('Sale', 'CustomerPayment', 'DeliveryPayment', 'Reversal', 'CashCountDifference', 'CashWithdrawal',
                            'CashDeposit', 'Transfer', 'ManualIn', 'ManualOut', 'SalaryPayment', 'EmployeeAdvance'));
    END IF;
END $$;

COMMIT;
