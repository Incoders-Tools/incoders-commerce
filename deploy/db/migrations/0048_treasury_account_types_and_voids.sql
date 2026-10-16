-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Treasury account types (an organization-owned catalog), account management (edit, activate/deactivate) and voided
-- or edited treasury movements.
-- APPLIED AFTER: 0047_treasury_accounts_and_cash_movements.sql.
--
-- 1. ACCOUNT TYPES (`treasury_account_types`): the organization's own catalog of kinds of money ("Efectivo", "Bancos",
--    "Tarjetas de crédito", "Billeteras virtuales"...), the same shape and rules as the other catalogs
--    (`supplier_categories`, 0030): name + key unique per organization, sort order, active/inactive, never deleted.
--    Every organization that already has treasury accounts gets the default types; an organization without types gets
--    them when its first account is created. Each account points at one type (`account_type_id`), so the owner sees the
--    company total split by type and by branch. The technical `kind` of an account stays: it is what the POS posts to
--    (the drawer = Cash, Card, Qr, and the branch Safe); the type is how the business groups its money.
--
-- 2. ACCOUNTS can be renamed, re-typed, described, and deactivated (`is_active`). UPDATE is granted on those columns
--    only.
--
-- 3. VOIDED MOVEMENTS (`treasury_movement_voids`): an administrator voids a movement (it stays, marked voided with who,
--    when and why, and stops counting in every balance) or edits it (the original is voided and points at its
--    replacement, which carries `corrects_movement_id`). Append-only like the movements: nothing is ever deleted or
--    rewritten, so the history and its audit stay complete.
--
-- RLS: org-scoped, FORCE, the symmetric tenant-isolation policy (NULLIF pooler-safety hardening from 0001).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS treasury_movement_voids;
--   ALTER TABLE treasury_movements DROP COLUMN IF EXISTS corrects_movement_id;
--   ALTER TABLE treasury_accounts DROP COLUMN IF EXISTS account_type_id, DROP COLUMN IF EXISTS is_active,
--       DROP COLUMN IF EXISTS updated_at;
--   DROP TABLE IF EXISTS treasury_account_types;
--   COMMIT;

BEGIN;

-- ---- 1. account types --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS treasury_account_types (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    key             text NOT NULL CHECK (btrim(key) <> ''),
    sort_order      integer NOT NULL DEFAULT 0,
    is_active       boolean NOT NULL DEFAULT true,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT treasury_account_types_org_scoped_uk UNIQUE (organization_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS treasury_account_types_org_name_uk
    ON treasury_account_types (organization_id, lower(btrim(name)));
CREATE UNIQUE INDEX IF NOT EXISTS treasury_account_types_org_key_uk
    ON treasury_account_types (organization_id, key);

ALTER TABLE treasury_account_types ENABLE ROW LEVEL SECURITY;
ALTER TABLE treasury_account_types FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON treasury_account_types FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON treasury_account_types TO app_runtime;
DROP POLICY IF EXISTS treasury_account_types_tenant_isolation ON treasury_account_types;
CREATE POLICY treasury_account_types_tenant_isolation ON treasury_account_types
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- The default types of every organization that already has treasury accounts (the others get them with their first
-- account). Same keys the API seeds.
INSERT INTO treasury_account_types (id, organization_id, name, key, sort_order)
SELECT gen_random_uuid(), owners.organization_id, defaults.name, defaults.key, defaults.sort_order
FROM (SELECT DISTINCT organization_id FROM treasury_accounts) AS owners
CROSS JOIN (VALUES
    ('Efectivo', 'efectivo', 10),
    ('Tarjetas', 'tarjetas', 20),
    ('Billeteras virtuales / QR', 'billeteras', 30),
    ('Bancos', 'bancos', 40),
    ('Otras', 'otras', 50)
) AS defaults (name, key, sort_order)
WHERE NOT EXISTS (SELECT 1 FROM treasury_account_types t WHERE t.organization_id = owners.organization_id);

-- ---- 2. accounts -------------------------------------------------------------------------

ALTER TABLE treasury_accounts ADD COLUMN IF NOT EXISTS account_type_id uuid NULL;
ALTER TABLE treasury_accounts ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true;
ALTER TABLE treasury_accounts ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'treasury_accounts_type_fk') THEN
        ALTER TABLE treasury_accounts ADD CONSTRAINT treasury_accounts_type_fk
            FOREIGN KEY (organization_id, account_type_id) REFERENCES treasury_account_types (organization_id, id);
    END IF;
END $$;

UPDATE treasury_accounts a
SET account_type_id = t.id
FROM treasury_account_types t
WHERE t.organization_id = a.organization_id
  AND a.account_type_id IS NULL
  AND t.key = CASE a.kind
      WHEN 'Cash' THEN 'efectivo' WHEN 'Safe' THEN 'efectivo' WHEN 'Card' THEN 'tarjetas'
      WHEN 'Qr' THEN 'billeteras' WHEN 'Bank' THEN 'bancos' ELSE 'otras' END;

GRANT UPDATE (name, description, account_type_id, is_active, updated_at) ON treasury_accounts TO app_runtime;

-- ---- 3. voided and edited movements ------------------------------------------------------

ALTER TABLE treasury_movements ADD COLUMN IF NOT EXISTS corrects_movement_id uuid NULL;

CREATE TABLE IF NOT EXISTS treasury_movement_voids (
    organization_id         uuid        NOT NULL,
    movement_id             uuid        NOT NULL,
    voided_at_utc           timestamptz NOT NULL DEFAULT now(),
    voided_by_user_id       uuid        NOT NULL,
    reason                  text        NOT NULL CHECK (btrim(reason) <> '' AND char_length(reason) <= 200),
    replacement_movement_id uuid        NULL,
    CONSTRAINT treasury_movement_voids_pk PRIMARY KEY (organization_id, movement_id),
    CONSTRAINT treasury_movement_voids_movement_fk
        FOREIGN KEY (organization_id, movement_id) REFERENCES treasury_movements (organization_id, id)
);

ALTER TABLE treasury_movement_voids ENABLE ROW LEVEL SECURITY;
ALTER TABLE treasury_movement_voids FORCE ROW LEVEL SECURITY;
REVOKE ALL ON treasury_movement_voids FROM PUBLIC;
GRANT SELECT, INSERT ON treasury_movement_voids TO app_runtime;   -- append-only: a void is never undone or rewritten
DROP POLICY IF EXISTS treasury_movement_voids_tenant_isolation ON treasury_movement_voids;
CREATE POLICY treasury_movement_voids_tenant_isolation ON treasury_movement_voids
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;
