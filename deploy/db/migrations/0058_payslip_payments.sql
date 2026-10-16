-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- owner feedback P5: a payslip's net is paid from one treasury account or split among several ("pago mixto"). Each part
-- is kept here, and its treasury movement (SalaryPayment, Out) points at it (source 'PayrollPayment', source_id = the
-- part's id), so a payslip can leave several accounts. The parts never exceed the net; what they leave unpaid stays owed
-- to the employee on its account. Runs paid before this kept no parts: they read the run's account and the net.
--
-- APPLIED AFTER: 0057_vehicles.sql.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   DROP TABLE IF EXISTS payslip_payments;

BEGIN;

CREATE TABLE IF NOT EXISTS payslip_payments (
    organization_id uuid          NOT NULL,
    id              uuid          NOT NULL,
    payslip_id      uuid          NOT NULL,
    line_no         integer       NOT NULL CHECK (line_no > 0),
    account_id      uuid          NOT NULL,
    amount          numeric(18,2) NOT NULL CHECK (amount > 0),
    created_at_utc  timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT payslip_payments_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT payslip_payments_line_uk UNIQUE (organization_id, payslip_id, line_no),
    CONSTRAINT payslip_payments_payslip_fk FOREIGN KEY (organization_id, payslip_id) REFERENCES payslips (organization_id, id),
    CONSTRAINT payslip_payments_account_fk FOREIGN KEY (organization_id, account_id) REFERENCES treasury_accounts (organization_id, id)
);

ALTER TABLE payslip_payments ENABLE ROW LEVEL SECURITY;
ALTER TABLE payslip_payments FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON payslip_payments FROM PUBLIC;
GRANT SELECT, INSERT ON payslip_payments TO app_runtime;
DROP POLICY IF EXISTS payslip_payments_tenant_isolation ON payslip_payments;
CREATE POLICY payslip_payments_tenant_isolation ON payslip_payments
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;
