-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- owner feedback 2026-10-15, B6: the staff buying goods are priced on the counter list (the organization's default
-- list, `price_lists.is_default`: Mostrador). The customer linked to an employee for its goods ("Juan Perez (personal)")
-- was created with the organization's default list for new customers (`organizations.default_customer_price_list_id`,
-- Reparto at Vaca Verde), so a POS sale on account to it was priced on that list. Since B6 a new link gets the default
-- list of the employee's branch; this moves every customer already linked to an employee onto it (price lists are
-- branch-owned). Customers not linked to an employee are untouched, and so are branches with no default list.
--
-- APPLIED AFTER: 0059_advance_cash_voids.sql.
--
-- The whole file runs in ONE transaction and can be re-run safely (IS DISTINCT FROM: a customer already there is left).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file: the earlier list of each customer is not kept;
-- set it again from the customers screen if needed.

BEGIN;

UPDATE customers c
SET price_list_id = pl.id, updated_at_utc = now()
FROM employees e
JOIN price_lists pl ON pl.organization_id = e.organization_id AND pl.branch_id = e.branch_id AND pl.is_default
WHERE e.organization_id = c.organization_id AND e.customer_id = c.id
  AND c.price_list_id IS DISTINCT FROM pl.id;

COMMIT;
