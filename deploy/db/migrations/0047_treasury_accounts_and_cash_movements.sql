-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Treasury accounts entered by the administration, manual movements and transfers, cash drawer movements of the POS
-- and the cash count difference of a closed cash session.
-- APPLIED AFTER: 0046_payment_terms_and_treasury.sql.
--
-- 1. ACCOUNTS. Besides the automatic accounts of each branch (Cash = the drawer, Card, Qr), the administration creates
--    Bank accounts and Other accounts (company-wide: `branch_id` NULL, or of one branch), and each branch has one Safe
--    (caja fuerte), created by the administration or on the first POS withdrawal into it. Cash, Card, Qr and Safe stay
--    one per branch (partial unique index); Bank and Other may repeat.
--
-- 2. MOVEMENTS. New kinds, all append-only like the rest (a mistake is a Reversal, never an edit):
--    - CashCountDifference: the surplus (In) or shortage (Out) a cash session closed with, on the branch Cash account.
--    - CashWithdrawal / CashDeposit: money taken out of / put into the drawer at the POS outside a sale (an expense paid
--      from the drawer, change brought in...).
--    - Transfer: money moved between two accounts (drawer to safe, safe to bank...): an Out and an In sharing
--      `transfer_id`.
--    - ManualIn / ManualOut: money the administration records by hand (a bank deposit slip, an expense paid by the bank,
--      the initial change fund...).
--    A Reversal now flips the direction of what it reverses (an Out reversed is an In).
--
-- RLS: unchanged (org-scoped, FORCE, SELECT and INSERT only).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file (only possible while no new-kind row exists):
--   BEGIN;
--   DELETE FROM treasury_movements WHERE kind IN ('CashCountDifference','CashWithdrawal','CashDeposit','Transfer','ManualIn','ManualOut');
--   DELETE FROM treasury_accounts WHERE kind IN ('Safe','Bank','Other');
--   ALTER TABLE treasury_movements DROP COLUMN IF EXISTS transfer_id;
--   ALTER TABLE treasury_accounts DROP COLUMN IF EXISTS description;
--   ALTER TABLE treasury_accounts ALTER COLUMN branch_id SET NOT NULL;
--   COMMIT;

BEGIN;

-- ---- 1. accounts -----------------------------------------------------------------------

ALTER TABLE treasury_accounts ALTER COLUMN branch_id DROP NOT NULL;
ALTER TABLE treasury_accounts ADD COLUMN IF NOT EXISTS description text NULL;

ALTER TABLE treasury_accounts DROP CONSTRAINT IF EXISTS treasury_accounts_kind_check;
ALTER TABLE treasury_accounts DROP CONSTRAINT IF EXISTS treasury_accounts_kind_ck;
ALTER TABLE treasury_accounts ADD CONSTRAINT treasury_accounts_kind_ck
    CHECK (kind IN ('Cash', 'Card', 'Qr', 'Safe', 'Bank', 'Other'));

ALTER TABLE treasury_accounts DROP CONSTRAINT IF EXISTS treasury_accounts_branch_scope_ck;
ALTER TABLE treasury_accounts ADD CONSTRAINT treasury_accounts_branch_scope_ck
    CHECK (kind IN ('Bank', 'Other') OR branch_id IS NOT NULL);

ALTER TABLE treasury_accounts DROP CONSTRAINT IF EXISTS treasury_accounts_description_ck;
ALTER TABLE treasury_accounts ADD CONSTRAINT treasury_accounts_description_ck
    CHECK (description IS NULL OR char_length(description) <= 200);

ALTER TABLE treasury_accounts DROP CONSTRAINT IF EXISTS treasury_accounts_branch_kind_uk;
CREATE UNIQUE INDEX IF NOT EXISTS treasury_accounts_branch_kind_uk
    ON treasury_accounts (organization_id, branch_id, kind) WHERE kind IN ('Cash', 'Card', 'Qr', 'Safe');

-- ---- 2. movements ----------------------------------------------------------------------

ALTER TABLE treasury_movements ADD COLUMN IF NOT EXISTS transfer_id uuid NULL;

-- Widened only when it does not admit these kinds yet: re-running this file never narrows what a later migration widened.
ALTER TABLE treasury_movements DROP CONSTRAINT IF EXISTS treasury_movements_kind_check;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'treasury_movements_kind_ck'
                   AND pg_get_constraintdef(oid) LIKE '%CashCountDifference%') THEN
        ALTER TABLE treasury_movements DROP CONSTRAINT IF EXISTS treasury_movements_kind_ck;
        ALTER TABLE treasury_movements ADD CONSTRAINT treasury_movements_kind_ck
            CHECK (kind IN ('Sale', 'CustomerPayment', 'DeliveryPayment', 'Reversal', 'CashCountDifference', 'CashWithdrawal',
                            'CashDeposit', 'Transfer', 'ManualIn', 'ManualOut'));
    END IF;
END $$;

ALTER TABLE treasury_movements DROP CONSTRAINT IF EXISTS treasury_movements_transfer_ck;
ALTER TABLE treasury_movements ADD CONSTRAINT treasury_movements_transfer_ck
    CHECK (kind <> 'Transfer' OR transfer_id IS NOT NULL);

CREATE INDEX IF NOT EXISTS treasury_movements_transfer_idx
    ON treasury_movements (organization_id, transfer_id) WHERE transfer_id IS NOT NULL;

COMMIT;
