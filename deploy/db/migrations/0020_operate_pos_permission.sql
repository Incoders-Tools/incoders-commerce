-- Repo-owned, transactional, idempotent, forward-only data migration.
--
-- pos-operator-session "Operating The POS Requires OperatePos": the POS device
-- endpoints now require the new `OperatePos` permission (bit 16). Roles are
-- persisted per user as `{"name", "permissions"}` in `users.roles` and the
-- effective permissions are read from those rows, so every existing
-- `business-admin` entry (previously 15) must gain the bit or every existing
-- administrator would be locked out of the POS the moment this ships. The
-- `cashier` role is new and has no persisted rows; `seller` deliberately does
-- NOT gain the bit. APPLIED AFTER: 0019_branch_discount_pin.sql.
--
-- `users` has `FORCE ROW LEVEL SECURITY` (0002): with no `app.current_org_id`
-- even the owner sees ZERO rows, so an unguarded UPDATE would "succeed" while
-- touching nothing (same hazard 0006 documents). FORCE is switched off for the
-- owner ONLY inside this one transaction and restored before COMMIT; the
-- post-condition aborts the whole transaction if any business-admin entry
-- still lacks the bit.
--
-- Idempotent: the permission is combined with a bitwise OR, so a second run
-- rewrites the same value (and the WHERE skips rows that already carry it).
--
-- INVERSE (rollback), shipped as a comment — NOT executed by this file:
--   BEGIN;
--   ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
--   UPDATE users SET roles = (
--       SELECT jsonb_agg(CASE WHEN e->>'name' = 'business-admin'
--           THEN jsonb_set(e, '{permissions}', to_jsonb(((e->>'permissions')::int) & ~16))
--           ELSE e END)
--       FROM jsonb_array_elements(roles) AS e)
--    WHERE roles @> '[{"name":"business-admin"}]';
--   ALTER TABLE users FORCE ROW LEVEL SECURITY;
--   COMMIT;

BEGIN;

ALTER TABLE users NO FORCE ROW LEVEL SECURITY;

UPDATE users
   SET roles = (
       SELECT jsonb_agg(
           CASE WHEN e->>'name' = 'business-admin'
                THEN jsonb_set(e, '{permissions}', to_jsonb(((e->>'permissions')::int) | 16))
                ELSE e END)
       FROM jsonb_array_elements(roles) AS e)
 WHERE roles @> '[{"name":"business-admin"}]'
   AND EXISTS (
       SELECT 1 FROM jsonb_array_elements(roles) AS e
        WHERE e->>'name' = 'business-admin'
          AND (((e->>'permissions')::int) & 16) = 0);

ALTER TABLE users FORCE ROW LEVEL SECURITY;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM users u, jsonb_array_elements(u.roles) AS e
         WHERE e->>'name' = 'business-admin'
           AND (((e->>'permissions')::int) & 16) = 0) THEN
        RAISE EXCEPTION '0020: a business-admin role entry survived without OperatePos';
    END IF;
END
$$;

COMMIT;
