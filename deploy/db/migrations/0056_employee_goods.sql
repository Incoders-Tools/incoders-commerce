-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- employee-lifecycle E4: the goods an employee takes, item by item, whatever the channel:
--   'Web'      registered in the web like a sale (priced on the default list, Mostrador): the API writes the stock out
--              (stock_movements 'Sale', source 'EmployeeGoods') and the debt on the linked customer's account.
--   'PosSale'  a POS sale on current account to the employee's linked customer: the sale projection already wrote the
--              stock and the debt; its lines are copied here (a sale discount spread over them) and removed if voided.
-- A payroll takes the pending items into a payslip (`payslip_id`), where a discount per item (`discount_percent`) is
-- decided before paying; deleting that payslip (employee taken out of a Draft, Draft discarded) sets them back to pending
-- by itself (ON DELETE SET NULL on the single-column `payslips_id_uk`); a Paid run is final.
--
-- APPLIED AFTER: 0055_employee_termination_reason.sql.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file (only while no goods were recorded):
--   DROP TABLE IF EXISTS employee_goods_items;

BEGIN;

CREATE TABLE IF NOT EXISTS employee_goods_items (
    organization_id    uuid          NOT NULL,
    id                 uuid          NOT NULL,
    employee_id        uuid          NOT NULL,
    customer_id        uuid          NOT NULL,
    branch_id          uuid          NULL,
    source_type        text          NOT NULL CHECK (source_type IN ('Web', 'PosSale')),
    source_id          uuid          NOT NULL,
    line_no            integer       NOT NULL CHECK (line_no > 0),
    occurred_on        date          NOT NULL,
    presentation_id    uuid          NULL,
    product_name       text          NOT NULL,
    presentation_name  text          NOT NULL,
    quantity           numeric(18,3) NOT NULL CHECK (quantity > 0),
    unit_price         numeric(18,2) NOT NULL CHECK (unit_price >= 0),
    line_total         numeric(18,2) NOT NULL CHECK (line_total >= 0),
    discount_percent   numeric(5,2)  NOT NULL DEFAULT 0 CHECK (discount_percent BETWEEN 0 AND 100),
    payslip_id         uuid          NULL,
    created_by_user_id uuid          NULL,
    created_at_utc     timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT employee_goods_items_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT employee_goods_items_source_uk UNIQUE (organization_id, source_type, source_id, line_no),
    CONSTRAINT employee_goods_items_employee_fk FOREIGN KEY (organization_id, employee_id) REFERENCES employees (organization_id, id),
    CONSTRAINT employee_goods_items_payslip_fk FOREIGN KEY (payslip_id) REFERENCES payslips (id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS employee_goods_items_employee_ix ON employee_goods_items (organization_id, employee_id, occurred_on);
CREATE INDEX IF NOT EXISTS employee_goods_items_payslip_ix ON employee_goods_items (payslip_id) WHERE payslip_id IS NOT NULL;

ALTER TABLE employee_goods_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE employee_goods_items FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON employee_goods_items FROM PUBLIC;
-- A voided POS sale deletes its items; a payroll sets payslip_id and the per-item discount.
GRANT SELECT, INSERT, UPDATE, DELETE ON employee_goods_items TO app_runtime;
DROP POLICY IF EXISTS employee_goods_items_tenant_isolation ON employee_goods_items;
CREATE POLICY employee_goods_items_tenant_isolation ON employee_goods_items
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;
