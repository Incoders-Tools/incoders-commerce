-- Repo-owned, transactional, idempotent, forward-only data migration.
--
-- staff-order-taking (T1): staff take orders for customers from the web with the new `TakeOrders` permission
-- (bit 32), granted to `seller` and `business-admin` in RoleCatalog. Roles are persisted per user as
-- `{"name", "permissions"}` in `users.roles` and the effective permissions are read from those rows (see 0020), so
-- every existing `seller` and `business-admin` entry must gain the bit or no existing user could take an order.
-- `cashier`, `provider` and `platform-admin` deliberately do NOT gain it. APPLIED AFTER: 0040_customer_party_type.sql.
--
-- `users` has `FORCE ROW LEVEL SECURITY` (0002): with no `app.current_org_id` even the owner sees ZERO rows, so an
-- unguarded UPDATE would "succeed" while touching nothing (the hazard 0006/0020 document). FORCE is switched off for
-- the owner ONLY inside this one transaction and restored before COMMIT; the post-condition aborts the whole
-- transaction if any seller or business-admin entry still lacks the bit.
--
-- Idempotent: the permission is combined with a bitwise OR, so a second run rewrites the same value (and the WHERE
-- skips rows that already carry it).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
--   UPDATE users SET roles = (
--       SELECT jsonb_agg(CASE WHEN e->>'name' IN ('seller', 'business-admin')
--           THEN jsonb_set(e, '{permissions}', to_jsonb(((e->>'permissions')::int) & ~32))
--           ELSE e END)
--       FROM jsonb_array_elements(roles) AS e)
--    WHERE roles @> '[{"name":"seller"}]' OR roles @> '[{"name":"business-admin"}]';
--   ALTER TABLE users FORCE ROW LEVEL SECURITY;
--   COMMIT;

BEGIN;

ALTER TABLE users NO FORCE ROW LEVEL SECURITY;

UPDATE users
   SET roles = (
       SELECT jsonb_agg(
           CASE WHEN e->>'name' IN ('seller', 'business-admin')
                THEN jsonb_set(e, '{permissions}', to_jsonb(((e->>'permissions')::int) | 32))
                ELSE e END
           ORDER BY ord)
       FROM jsonb_array_elements(roles) WITH ORDINALITY AS r(e, ord))
 WHERE EXISTS (
       SELECT 1 FROM jsonb_array_elements(roles) AS e
        WHERE e->>'name' IN ('seller', 'business-admin')
          AND (((e->>'permissions')::int) & 32) = 0);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM users u, jsonb_array_elements(u.roles) AS e
         WHERE e->>'name' IN ('seller', 'business-admin')
           AND (((e->>'permissions')::int) & 32) = 0) THEN
        RAISE EXCEPTION '0041: a seller or business-admin role entry survived without TakeOrders';
    END IF;
END
$$;

ALTER TABLE users FORCE ROW LEVEL SECURITY;

COMMIT;
