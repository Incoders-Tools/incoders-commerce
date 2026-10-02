-- Vaca Verde customer price lists (customer-price-lists T1): Mostrador becomes a BASE-price list with its own
-- composition and Reparto as its floor, Reparto becomes the organization's default list for customers and every
-- customer's list, and the old "Clientes" list is dropped.
--
-- HAND-MAINTAINED (not produced by generate_suppliers_catalog.py): it derives everything from the rows 003 loaded, so
-- it works the same on a fresh environment and on one where an earlier 003 already ran. See README.md.
--
-- What it does, in one transaction, on the branch "Ruta 51" of the organization "Vaca Verde":
--   1. Mostrador's prices stop being FINAL prices. New entries are published effective 2026-10-02 (the 2026-10-01 finals
--      stay as history; entries are append-only): the base of a product that Reparto also sells is REPARTO'S BASE, the
--      base of a counter-only product is round(final / 1.48, 2). Only products whose current Mostrador entry is still
--      the seeded one, and only while Mostrador has no rate set of its own.
--   2. Mostrador gets its own LIST-SPECIFIC rate set effective 2026-10-02: IVA 10.5 %, Ingresos Brutos 2.5 % and
--      Remarcacion 35 %, all on the base, no flete (base x 1.48).
--   3. Mostrador's floor is Reparto, set in the same run that publishes its composition, and only while it has none.
--   4. The organization's default customer list is Reparto, only while unset and no settings change was ever audited.
--   5. Every customer without a list gets Reparto (a list chosen in the app is never overwritten).
--   6. The "Clientes" list is deleted, with its seeded entries, only when nothing else references it: no customer, no
--      organization default, no floor, no rate set, and no entry other than the 2026-10-01 seeded ones.
--
-- Safe to re-run: deterministic ids, ON CONFLICT DO NOTHING and the "only while untouched" guards above, so a second run
-- changes nothing and never overwrites later edits. When the organization, its business admin, its branch or either of
-- the lists Mostrador / Reparto does not exist, nothing is changed. Apply after 003 (and migration 0037).
--
-- Run as the database owner, e.g.:
--   psql -U commerce_owner -d <db> -v ON_ERROR_STOP=1 -f 004_vaca_verde_customer_price_lists.sql
-- If the owner role is subject to row level security, scope the session to the
-- organization first: PGOPTIONS="-c app.current_org_id=<organization uuid>".

SET client_encoding = 'UTF8';

BEGIN;

CREATE TEMP TABLE _vv_seed_ctx (organization_id uuid NOT NULL, user_id uuid NOT NULL, branch_id uuid NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _vv_cl (mostrador_id uuid NOT NULL, reparto_id uuid NOT NULL, first_run boolean NOT NULL) ON COMMIT DROP;

DO $$
DECLARE
    v_scope uuid := NULLIF(current_setting('app.current_org_id', true), '')::uuid;
    v_org   uuid;
    v_user  uuid;
    v_branch uuid;
    v_mostrador uuid;
    v_reparto uuid;
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

    SELECT id INTO v_user
    FROM users
    WHERE organization_id = v_org
      AND NOT is_revoked
      AND customer_id IS NULL
      AND roles @> '[{"name":"business-admin"}]'::jsonb
    ORDER BY created_at_utc, id
    LIMIT 1;

    IF v_user IS NULL THEN
        RAISE NOTICE 'Vaca Verde seed skipped: the organization has no business-admin user to own the records.';
        RETURN;
    END IF;

    SELECT id INTO v_branch
    FROM branches
    WHERE organization_id = v_org AND lower(btrim(name)) = 'ruta 51'
    ORDER BY created_at, id
    LIMIT 1;

    IF v_branch IS NULL THEN
        RAISE NOTICE 'Vaca Verde seed skipped: the organization has no branch named "Ruta 51" to own the price lists.';
        RETURN;
    END IF;

    -- Branch-owned tables (catalog and pricing) are keyed on this setting too; transaction-local.
    PERFORM set_config('app.current_branch_id', v_branch::text, true);

    INSERT INTO _vv_seed_ctx (organization_id, user_id, branch_id) VALUES (v_org, v_user, v_branch);

    SELECT id INTO v_mostrador FROM price_lists
    WHERE organization_id = v_org AND branch_id = v_branch AND lower(btrim(name)) = 'mostrador'
    ORDER BY created_at_utc, id LIMIT 1;
    SELECT id INTO v_reparto FROM price_lists
    WHERE organization_id = v_org AND branch_id = v_branch AND lower(btrim(name)) = 'reparto'
    ORDER BY created_at_utc, id LIMIT 1;

    IF v_mostrador IS NULL OR v_reparto IS NULL THEN
        RAISE NOTICE 'Vaca Verde customer price lists skipped: the branch needs the lists Mostrador and Reparto (apply 003 first).';
        RETURN;
    END IF;

    -- "First run" = Mostrador has no composition of its own yet; evaluated ONCE, before anything below is written.
    INSERT INTO _vv_cl (mostrador_id, reparto_id, first_run)
    VALUES (v_mostrador, v_reparto,
            NOT EXISTS (SELECT 1 FROM rate_component_sets s WHERE s.price_list_id = v_mostrador));
END $$;

-- 1. Mostrador's BASE prices, effective 2026-10-02. Reparto's base where the product exists there, else final / 1.48.
INSERT INTO price_list_entries (id, organization_id, branch_id, price_list_id, presentation_id, unit_price,
                                effective_from, source, created_by_user_id)
SELECT md5('vaca-verde:price-entry:mostrador-base:' || cur.presentation_id::text || ':2026-10-02')::uuid,
       c.organization_id, c.branch_id, l.mostrador_id, cur.presentation_id,
       COALESCE(rep.unit_price, round(cur.unit_price / 1.48, 2)), DATE '2026-10-02', 'Manual', c.user_id
FROM _vv_seed_ctx c
JOIN _vv_cl l ON l.first_run
JOIN LATERAL (
    SELECT DISTINCT ON (e.presentation_id) e.presentation_id, e.unit_price, e.effective_from
    FROM price_list_entries e
    WHERE e.price_list_id = l.mostrador_id
    ORDER BY e.presentation_id, e.effective_from DESC
) cur ON cur.effective_from = DATE '2026-10-01'
LEFT JOIN LATERAL (
    SELECT e.unit_price
    FROM price_list_entries e
    WHERE e.price_list_id = l.reparto_id AND e.presentation_id = cur.presentation_id AND e.effective_from <= DATE '2026-10-02'
    ORDER BY e.effective_from DESC
    LIMIT 1
) rep ON true
ON CONFLICT DO NOTHING;

-- 2. Mostrador's own LIST-SPECIFIC rate set: IVA 10.5 + IB 2.5 + Remarcacion 35, all on the base (base x 1.48).
INSERT INTO rate_component_sets (id, organization_id, branch_id, price_list_id, effective_from, created_by_user_id)
SELECT md5('vaca-verde:rate-set:mostrador:2026-10-02')::uuid, c.organization_id, c.branch_id, l.mostrador_id,
       DATE '2026-10-02', c.user_id
FROM _vv_seed_ctx c
JOIN _vv_cl l ON l.first_run
ON CONFLICT DO NOTHING;

INSERT INTO rate_components (id, organization_id, branch_id, set_id, code, label, percentage, calculation_base,
                             component_order)
SELECT md5('vaca-verde:rate-component:mostrador:' || v.code)::uuid, c.organization_id, c.branch_id, s.id, v.code, v.label,
       v.percentage::numeric(9,4), 'Base', v.component_order
FROM (VALUES
    ('IVA', 'IVA (10,5%)', '10.5', 1),
    ('IB', 'Ingresos Brutos (2,5%)', '2.5', 2),
    ('REMARCACION', 'Remarcación (35%)', '35', 3)
) AS v (code, label, percentage, component_order)
CROSS JOIN _vv_seed_ctx c
JOIN rate_component_sets s ON s.organization_id = c.organization_id AND s.branch_id = c.branch_id
                          AND s.id = md5('vaca-verde:rate-set:mostrador:2026-10-02')::uuid
ON CONFLICT DO NOTHING;

-- 3. Floor: Mostrador never prices below Reparto. Set in the run that publishes the composition, only while unset.
UPDATE price_lists pl
SET floor_price_list_id = l.reparto_id
FROM _vv_cl l
WHERE l.first_run AND pl.id = l.mostrador_id AND pl.floor_price_list_id IS NULL;

-- 4. The organization default for customers, only while unset and the settings were never edited in the app.
UPDATE organizations o
SET default_customer_price_list_id = l.reparto_id
FROM _vv_seed_ctx c
JOIN _vv_cl l ON true
WHERE o.id = c.organization_id
  AND o.default_customer_price_list_id IS NULL
  AND NOT EXISTS (SELECT 1 FROM audit_log a
                  WHERE a.organization_id = o.id AND a.action = 'organization.settings_updated');

-- 6 (before 5, so a customer already on Clientes keeps it and keeps the list alive). Drop the old Clientes list.
DELETE FROM price_lists pl
USING _vv_seed_ctx c
WHERE pl.organization_id = c.organization_id AND pl.branch_id = c.branch_id
  AND EXISTS (SELECT 1 FROM _vv_cl)
  AND lower(btrim(pl.name)) = 'clientes' AND NOT pl.is_default
  AND NOT EXISTS (SELECT 1 FROM customers cu WHERE cu.price_list_id = pl.id)
  AND NOT EXISTS (SELECT 1 FROM organizations o WHERE o.default_customer_price_list_id = pl.id)
  AND NOT EXISTS (SELECT 1 FROM price_lists f WHERE f.floor_price_list_id = pl.id)
  AND NOT EXISTS (SELECT 1 FROM rate_component_sets s WHERE s.price_list_id = pl.id)
  AND NOT EXISTS (SELECT 1 FROM price_list_entries e
                  WHERE e.price_list_id = pl.id AND e.effective_from <> DATE '2026-10-01');

-- 5. Every customer without a list is priced from Reparto (a choice made in the app is never overwritten).
UPDATE customers cu
SET price_list_id = l.reparto_id
FROM _vv_seed_ctx c
JOIN _vv_cl l ON true
WHERE cu.organization_id = c.organization_id AND cu.price_list_id IS NULL;

COMMIT;
