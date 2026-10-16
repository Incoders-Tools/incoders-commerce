-- Commerce admin console identity merge. Forward-only and idempotent.
-- Apply after 0008_customer_registry.sql through 0011_payments.sql.
-- Inverse: recreate platform_admins from is_system_admin rows, remove the
-- migrated pseudo-organization users, then drop is_system_admin.

ALTER TABLE users ADD COLUMN IF NOT EXISTS is_system_admin boolean NOT NULL DEFAULT false;

DO $$
BEGIN
  IF to_regclass('public.platform_admins') IS NOT NULL THEN
    IF EXISTS (
      SELECT 1
      FROM platform_admins legacy
      JOIN user_directory directory ON directory.email_normalized = legacy.email
      WHERE directory.user_id <> legacy.id
    ) THEN
      RAISE EXCEPTION 'Cannot migrate platform admin: email already belongs to a different user';
    END IF;

    ALTER TABLE organizations NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE user_directory NO FORCE ROW LEVEL SECURITY;

    INSERT INTO organizations (id, name)
    VALUES ('00000000-0000-0000-0000-000000000001', 'Incoders Platform')
    ON CONFLICT (id) DO NOTHING;

    INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles, is_system_admin)
    SELECT id, '00000000-0000-0000-0000-000000000001', email, password_hash, '{}'::uuid[], '[]'::jsonb, true
    FROM platform_admins
    ON CONFLICT (organization_id, email) DO UPDATE SET is_system_admin = true;

    INSERT INTO user_directory (email_normalized, organization_id, user_id)
    SELECT email, '00000000-0000-0000-0000-000000000001', id
    FROM platform_admins
    ON CONFLICT (email_normalized) DO NOTHING;

    DROP TABLE platform_admins;
    ALTER TABLE user_directory FORCE ROW LEVEL SECURITY;
    ALTER TABLE users FORCE ROW LEVEL SECURITY;
    ALTER TABLE organizations FORCE ROW LEVEL SECURITY;
  END IF;
END $$;
