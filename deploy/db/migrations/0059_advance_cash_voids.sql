-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- owner feedback 2026-10-14, A3: an employee's advance is two movements keyed by its id, the cash it left (treasury,
-- kind EmployeeAdvance, Out) and a debit on the employee's account. Until A2 they could be undone apart: reversing the
-- debit on the employee's account left the cash movement counting, so the drawer read the advance as paid out while the
-- employee no longer owed it. Since A2 voiding or reversing an advance undoes both sides; this voids the cash movement of
-- every advance whose debit was already reversed, as that void would have: the movement stays, marked voided by whoever
-- reversed the debit, when they did, with its concept as the reason, and no balance counts it any more.
--
-- APPLIED AFTER: 0058_payslip_payments.sql.
--
-- The whole file runs in ONE transaction and can be re-run safely (a movement is voided at most once: the void's
-- primary key, and NOT EXISTS for an advance already voided).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   DELETE FROM treasury_movement_voids v USING treasury_movements m
--   WHERE m.organization_id = v.organization_id AND m.id = v.movement_id AND m.source_type = 'EmployeeAdvance'
--     AND v.reason LIKE 'Anulado en la cuenta del empleado: %';

BEGIN;

INSERT INTO treasury_movement_voids (organization_id, movement_id, voided_at_utc, voided_by_user_id, reason)
SELECT m.organization_id, m.id, r.created_at_utc, r.created_by_user_id,
       left('Anulado en la cuenta del empleado: ' || r.concept, 200)
FROM treasury_movements m
JOIN current_account_movements d
  ON d.organization_id = m.organization_id AND d.source_type = 'EmployeeAdvance' AND d.source_id = m.source_id
 AND d.kind <> 'Reversal'
JOIN current_account_movements r
  ON r.organization_id = d.organization_id AND r.reverses_movement_id = d.id
WHERE m.source_type = 'EmployeeAdvance' AND m.kind <> 'Reversal'
  AND NOT EXISTS (SELECT 1 FROM treasury_movement_voids v WHERE v.organization_id = m.organization_id AND v.movement_id = m.id)
ON CONFLICT DO NOTHING;

COMMIT;
