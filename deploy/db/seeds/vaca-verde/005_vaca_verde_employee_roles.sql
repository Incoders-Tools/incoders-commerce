-- Vaca Verde staff positions (owner-requests-quick-adjustments Q5): Titular (the owner), Encargado (the owner's
-- children), Carnicero, Chofer, Vendedor and Cajero, in that order.
--
-- HAND-MAINTAINED (not produced by any generator); see README.md in this directory.
--
-- Safe to re-run: every row has a deterministic id that includes the organization
-- (md5('vaca-verde:employee-role:<organization id>:<key>')::uuid, so the rows of an earlier, renamed "Vaca Verde" never
-- block a new one) and is inserted with ON CONFLICT DO NOTHING, which also covers the unique name and key indexes, so a
-- second run changes nothing, a position already created in the app with the same name or key is kept as is, and one
-- renamed or deactivated later is never overwritten. The order is spaced by 10, leaving room between positions. When the
-- organization "Vaca Verde" does not exist yet, the file prints a NOTICE and changes nothing. Apply after migration 0050.
--
-- Run as the database owner, e.g.:
--   psql -U commerce_owner -d <db> -v ON_ERROR_STOP=1 -f 005_vaca_verde_employee_roles.sql
-- If the owner role is subject to row level security, scope the session to the
-- organization first: PGOPTIONS="-c app.current_org_id=<organization uuid>".

SET client_encoding = 'UTF8';

BEGIN;

CREATE TEMP TABLE _vv_seed_ctx (organization_id uuid NOT NULL) ON COMMIT DROP;

DO $$
DECLARE
    v_scope uuid := NULLIF(current_setting('app.current_org_id', true), '')::uuid;
    v_org   uuid;
BEGIN
    SELECT id INTO v_org
    FROM organizations
    WHERE lower(btrim(name)) = 'vaca verde' AND (v_scope IS NULL OR id = v_scope)
    ORDER BY created_at, id
    LIMIT 1;

    IF v_org IS NULL THEN
        RAISE NOTICE 'Vaca Verde seed skipped: organization "Vaca Verde" not found (provision it first; under row level security set app.current_org_id).';
        RETURN;
    END IF;

    -- Row level security is keyed on this setting; transaction-local.
    PERFORM set_config('app.current_org_id', v_org::text, true);

    INSERT INTO _vv_seed_ctx (organization_id) VALUES (v_org);
END $$;

INSERT INTO employee_roles (id, organization_id, name, key, sort_order)
SELECT md5('vaca-verde:employee-role:' || c.organization_id::text || ':' || v.key)::uuid, c.organization_id, v.name, v.key, v.sort_order
FROM (VALUES
    ('titular', 'Titular', 10),
    ('encargado', 'Encargado', 20),
    ('carnicero', 'Carnicero', 30),
    ('chofer', 'Chofer', 40),
    ('vendedor', 'Vendedor', 50),
    ('cajero', 'Cajero', 60)
) AS v (key, name, sort_order)
CROSS JOIN _vv_seed_ctx c
ON CONFLICT DO NOTHING;

COMMIT;
