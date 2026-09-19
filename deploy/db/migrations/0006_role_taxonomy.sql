-- Repo-owned, transactional, forward-only data migration.
--
-- Rewrites every persisted `"admin"` role entry to `"business-admin"`
-- (commerce-role-taxonomy proposal.md "Rename migration" / design.md
-- "Rename migration must defeat FORCE RLS"). `"admin"` alone is now
-- ambiguous once a platform-level admin exists. Append-only: 0001-0005 are
-- NOT modified.
--
-- `users` has `FORCE ROW LEVEL SECURITY` (0002). With no
-- `app.current_org_id` set, even the migration operator (the table owner) is
-- filtered by `users_tenant_isolation` — a naive `UPDATE` here would "succeed"
-- while silently touching ZERO rows, and every existing org would keep the
-- unknown `"admin"` role name with nobody noticing. FORCE is toggled OFF for
-- the owner ONLY, ONLY inside this one transaction (`ENABLE` still binds
-- `app_runtime` throughout, so the running application is never exposed to
-- unscoped rows), and a post-condition `RAISE EXCEPTION` aborts the whole
-- transaction rather than half-applying if any legacy row survives.
--
-- Idempotent: the second run's `UPDATE ... WHERE roles @> '[{"name":"admin"}]'`
-- matches zero rows (there is nothing left to rewrite), so re-running is safe.
--
-- Inverse (rollback): swap the two literal names in the `UPDATE`'s
-- `jsonb_set` call and in the `WHERE`/assertion clauses:
--   BEGIN;
--   ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
--   UPDATE users
--      SET roles = (
--          SELECT jsonb_agg(
--              CASE WHEN e->>'name' = 'business-admin'
--                   THEN jsonb_set(e, '{name}', '"admin"')
--                   ELSE e END)
--          FROM jsonb_array_elements(roles) AS e)
--    WHERE roles @> '[{"name":"business-admin"}]';
--   ALTER TABLE users FORCE ROW LEVEL SECURITY;
--   COMMIT;

BEGIN;

ALTER TABLE users NO FORCE ROW LEVEL SECURITY;

UPDATE users
   SET roles = (
       SELECT jsonb_agg(
           CASE WHEN e->>'name' = 'admin'
                THEN jsonb_set(e, '{name}', '"business-admin"')
                ELSE e END)
       FROM jsonb_array_elements(roles) AS e)
 WHERE roles @> '[{"name":"admin"}]';

ALTER TABLE users FORCE ROW LEVEL SECURITY;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM users WHERE roles @> '[{"name":"admin"}]') THEN
        RAISE EXCEPTION '0006: legacy "admin" role entries survived the rewrite';
    END IF;
END
$$;

COMMIT;
