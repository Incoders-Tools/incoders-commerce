# -*- coding: utf-8 -*-
"""Generate 002_vaca_verde_suppliers.sql, 003_vaca_verde_catalog.sql and report-catalogo.md.

004_vaca_verde_customer_price_lists.sql is NOT generated: it derives everything from the rows 003 loaded.

The owner's spreadsheets live outside the repository; their paths are arguments:

    python generate_suppliers_catalog.py \
        --proveedores "Proveedores Lista.xlsx" \
        --achuras "Achuras.xlsx" \
        --precios "Precios Vaca Verde Reparto con Porcentajes.xlsx" \
        --vacuno-cerdo-pollo "Vacuno Cerdo Pollo.xlsx"

Requires Python 3 and openpyxl. The SQL and the report are deterministic: the same
spreadsheets always yield the same bytes. Never edit the generated files by hand.
"""
import argparse
import os
import re
import unicodedata
from decimal import Decimal

import openpyxl

from generate_seed import q, slug

HERE = os.path.dirname(os.path.abspath(__file__))

EFFECTIVE_FROM = "2026-10-01"

# Lists that are LOADED. The "Lista Clientes" sheet is still read (it names some products and carries owner
# notes) but is no longer a price list: customers are priced from Reparto (004_vaca_verde_customer_price_lists.sql).
PERSISTED_LISTS = ("Mostrador", "Reparto")

# Georef localities (cities.indec_id), looked up in the loaded core geography table.
INDEC = {
    "Rosario": "82084270",                 # Rosario, Santa Fe
    "Pilar": "06638040",                   # Pilar, Buenos Aires
    "Munro": "0686101005",                 # Munro, Vicente Lopez, Buenos Aires
    "CABA": "02014010",                    # Ciudad de Buenos Aires
    "Merlo": "06539010",                   # Merlo, Buenos Aires
    "San Pedro": "06770050",               # San Pedro, Buenos Aires (same as the customers' mapping)
}

CATEGORIES = [  # (name, icon_key); the order is the product code order
    ("Vacuno", "meat"), ("Cerdo", "meat"), ("Embutidos", "meat"), ("Pollo", "poultry"),
    ("Achuras", "meat"), ("Milanesas", "meat"), ("Almacén", "grocery"), ("Bebidas", "drinks"),
]

# Spanish display names where plain sentence case is not enough (lost accents, typos).
NAME_OVERRIDES = {
    "corazon de cuadril": "Corazón de cuadril",
    "vacio": "Vacío",
    "vacio novillo seleccionado": "Vacío novillo seleccionado",
    "vacio marca santa ines": "Vacío marca Santa Inés",
    "bondiola salda": "Bondiola salada",
    "pollo": "Pollo entero",
    "milanesas pechuga": "Milanesas de pechuga",
    "medallon de pollo jamon y ques": "Medallón de pollo jamón y queso",
}

EMBUTIDOS = {"chorizo seco", "salame", "salamin picado fino", "salamin picado grueso", "bondiola salda",
             "jamon crudo", "chorizo colorado", "salchicha parrillera", "chorizo fresco", "morcilla"}
MILANESAS = {"milanesas pechuga", "milanesas de muslo", "medallon de pollo", "medallon de pollo jamon y ques"}

CITY_LABEL = {"CABA": "Ciudad de Buenos Aires"}

PROVISIONAL = "Milanesa de ternera de bola de lomo"


def fold(s):
    s = "".join(c for c in unicodedata.normalize("NFKD", str(s)) if not unicodedata.combining(c))
    return re.sub(r"\s+", " ", s).strip().lower()


def display(raw):
    k = fold(raw)
    if k in NAME_OVERRIDES:
        return NAME_OVERRIDES[k]
    s = re.sub(r"\s+", " ", str(raw)).strip().lower()
    return s[:1].upper() + s[1:]


def dec(v):
    return Decimal(str(v)).quantize(Decimal("0.01"))


def money(v):
    return f"{v:,.2f}".replace(",", "X").replace(".", ",").replace("X", ".")


def rows(path, sheet):
    wb = openpyxl.load_workbook(path, data_only=True)
    return [r for r in wb[sheet].iter_rows(values_only=True)]


def price_rows(path, sheet):
    """(raw name, price, note) for every row whose second cell is a number."""
    out = []
    for r in rows(path, sheet):
        name, price = r[0], r[1]
        if isinstance(name, str) and isinstance(price, (int, float)):
            note = r[2] if len(r) > 2 and isinstance(r[2], str) else None
            if note:
                note = note.strip().strip("()").strip()
            out.append((name, price, note, r))
    return out


# ---------------------------------------------------------------- suppliers

def digits(v):
    if v is None:
        return None
    if isinstance(v, float):
        v = int(v)
    d = re.sub(r"\D", "", str(v))
    return d or None


def build_suppliers(path, flags):
    src = {fold(r[1]): r for r in rows(path, "Hoja1")[1:] if r[1]}
    assert len(src) == 10, f"expected 10 supplier rows, got {len(src)}"

    def row(prefix):
        m = [r for k, r in src.items() if k.startswith(prefix)]
        assert len(m) == 1, prefix
        return m[0]

    swift, urien, vdf, cow, merlo, gorina, frigolar, amalia = (
        row("swift"), row("urien"), row("ventasdf"), row("carniceria cow"), row("frigorifico merlo"),
        row("frigorifico gorina"), row("frigorifico frigolar"), row("amalia"))
    villarino = [r for k, r in src.items() if k.startswith("villarino")]
    assert len(villarino) == 2

    def phone(r):
        return digits(r[3])

    def email(r):
        return r[4].strip().lower() if r[4] else None

    suppliers = [
        dict(name="Swift", phone=phone(swift), email=email(swift), street=None, number=None, city="Rosario",
             contacts=[dict(first="Nelson Luis Beber Valdemarin", last=None, phone=None, email=email(swift), role=None)]),
        dict(name="Urien Loza", phone=phone(urien), email=None, street=None, number=None, city="Pilar",
             contacts=[dict(first="Marcos", last="Loza", phone=None, email=None, role=None)]),
        dict(name="Ventas DF Distribuidora", phone=phone(vdf), email=None, street=None, number=None, city="Munro",
             contacts=[dict(first="Gustavo", last=None, phone=None, email=None, role="Depósito")]),
        dict(name="Carnicería Cow", phone=phone(cow), email=None, street="Av. Nazca", number="5096", city="CABA",
             contacts=[]),
        dict(name="Frigorífico Merlo", phone=phone(merlo), email=email(merlo), street="Elías Alippi", number="1890",
             city="Merlo", contacts=[dict(first="Eduardo", last="Manchessot", phone=None, email=None, role=None)]),
        dict(name="Frigorífico Gorina", phone=phone(gorina), email=None, street=None, number=None, city=None,
             contacts=[dict(first="Lautaro", last="Silvestri", phone=None, email=None, role=None)]),
        dict(name="Frigorífico Frigolar", phone=phone(frigolar), email=None, street=None, number=None, city=None,
             contacts=[dict(first="Carlos", last="Charton", phone=None, email=None, role=None)]),
        dict(name="Amalia", phone=phone(amalia), email=None, street=None, number=None, city="San Pedro", contacts=[]),
    ]
    frimsa = []
    for r in villarino:
        first = "Agustín" if "agustin" in fold(r[1]) else "Matías"
        frimsa.append(dict(first=first, last="Villarino", phone=digits(r[3]), email=None, role=None))
    frimsa.sort(key=lambda c: c["first"])  # Agustín first (primary), then Matías
    suppliers.append(dict(name="Frimsa", phone=None, email=None, street=None, number=None, city=None, contacts=frimsa))
    assert len(suppliers) == 9
    return suppliers


# ------------------------------------------------------------------ catalog

class Catalog:
    def __init__(self):
        self.products = {}      # (category, fold(name)) -> dict(name, category, key)
        self.notes = {}

    def add(self, category, name):
        k = (category, fold(name))
        if k not in self.products:
            self.products[k] = dict(name=name, category=category)
        return self.products[k]


def build_catalog(precios, vcp, achuras, flags, merges):
    cat = Catalog()
    prices = {"Mostrador": {}, "Reparto": {}, "Clientes": {}}   # list -> product name -> price
    notes = {}                                                    # product name -> owner note

    reparto = [r for r in price_rows(precios, "Lista Reparto con porcentajes") if isinstance(r[3][6], (int, float))]
    clientes = price_rows(precios, "Lista Clientes")
    media = price_rows(precios, "MEDIA")
    vacuna = price_rows(vcp, "Vacuna")
    cerdo = price_rows(vcp, "Cerdo")
    pollo = price_rows(vcp, "Pollo")

    seen_beef = {}   # fold(name) -> [display name, sheets]

    # Union of all beef rows (Reparto first: the wholesale names).
    for src_name, rws in (("Reparto", reparto), ("Clientes", clientes), ("Vacuna", vacuna), ("MEDIA", media)):
        for raw, price, note, _ in rws:
            name = display(raw)
            cat.add("Vacuno", name)
            seen_beef.setdefault(fold(name), [name, []])[1].append(src_name)
    beef_keys = {fold(n) for (c, n) in cat.products if c == "Vacuno"}

    # Merge report: every beef name present in more than one sheet.
    for k, (name, srcs) in sorted(seen_beef.items()):
        if len(srcs) > 1:
            merges.append((name, srcs))

    for raw, price, note, _ in reparto:
        name = display(raw)
        prices["Reparto"][name] = dec(price)
        # the sheet's own PRECIO FINAL (column G) verifies the composition
    for raw, price, note, _ in clientes:
        name = display(raw)
        prices["Clientes"][name] = dec(price)
        if note:
            notes[name] = note

    # Retail beef: Vacuna is the primary sheet; MEDIA must agree.
    vac = {fold(display(r[0])): dec(r[1]) for r in vacuna}
    med = {fold(display(r[0])): dec(r[1]) for r in media}
    for k in sorted(set(vac) | set(med)):
        if vac.get(k) != med.get(k):
            flags.append(f"MEDIA y Vacuna difieren en «{display(k)}»: Vacuna {vac.get(k)} / MEDIA {med.get(k)}. Se usó Vacuna.")
    for raw, price, note, _ in vacuna:
        prices["Mostrador"][display(raw)] = dec(price)

    # Pork and embutidos.
    for raw, price, note, _ in cerdo:
        k = fold(raw)
        if k in EMBUTIDOS:
            name = display(raw)
            cat.add("Embutidos", name)
        else:
            name = display(raw)
            if fold(name) in beef_keys:
                name = f"{name} de cerdo"
            cat.add("Cerdo", name)
        prices["Mostrador"][name] = dec(price)

    # Poultry and milanesas.
    for raw, price, note, _ in pollo:
        name = display(raw)
        cat.add("Milanesas" if fold(raw) in MILANESAS else "Pollo", name)
        prices["Mostrador"][name] = dec(price)

    # Achuras.
    for r in rows(achuras, "Precios Achuras")[1:]:
        if not r[0]:
            continue
        name = display(r[0])
        cat.add("Achuras", name)
        final = r[3]
        if isinstance(final, (int, float)):
            prices["Mostrador"][name] = dec(final)
            orig, pct = r[1], r[2]
            if isinstance(orig, (int, float)) and isinstance(pct, (int, float)):
                expected = dec(Decimal(str(orig)) * (1 + Decimal(str(pct))))
                if expected != dec(final):
                    flags.append(("achura", name, dec(orig), pct, expected, dec(final)))
        else:
            flags.append(("achura-sin-precio", name, r[1], final))

    cat.add("Milanesas", PROVISIONAL)

    # Sanity: every priced name is a catalog product.
    names = {p["name"] for p in cat.products.values()}
    for lst, d in prices.items():
        for n in d:
            assert n in names, (lst, n)

    order = {c: i for i, (c, _) in enumerate(CATEGORIES)}
    ordered = sorted(cat.products.values(), key=lambda p: (order[p["category"]], fold(p["name"])))
    assert len({p["name"] for p in ordered}) == len(ordered), "duplicate product name"
    for i, p in enumerate(ordered, start=1):
        p["code"] = f"{i:03d}"
        p["key"] = slug(p["name"])
    assert len({p["key"] for p in ordered}) == len(ordered), "duplicate product key"
    return ordered, prices, notes, reparto


# -------------------------------------------------------------------- SQL

def values(rows_):
    return ",\n".join("    (" + ", ".join(r) + ")" for r in rows_)


def header(title, blurb, filename, needs_branch):
    branch_decl = "\n    v_branch uuid;" if needs_branch else ""
    branch_block = ""
    ctx_cols = "organization_id uuid NOT NULL, user_id uuid NOT NULL"
    ctx_insert = "INSERT INTO _vv_seed_ctx (organization_id, user_id) VALUES (v_org, v_user);"
    if needs_branch:
        ctx_cols += ", branch_id uuid NOT NULL"
        branch_block = """
    SELECT id INTO v_branch
    FROM branches
    WHERE organization_id = v_org AND lower(btrim(name)) = 'ruta 51'
    ORDER BY created_at, id
    LIMIT 1;

    IF v_branch IS NULL THEN
        RAISE NOTICE 'Vaca Verde seed skipped: the organization has no branch named "Ruta 51" to own the catalog.';
        RETURN;
    END IF;

    -- Branch-owned tables (catalog and pricing) are keyed on this setting too; transaction-local.
    PERFORM set_config('app.current_branch_id', v_branch::text, true);
"""
        ctx_insert = "INSERT INTO _vv_seed_ctx (organization_id, user_id, branch_id) VALUES (v_org, v_user, v_branch);"
    return f"""-- {title}
--
-- GENERATED by generate_suppliers_catalog.py from the owner's spreadsheets - do not
-- edit by hand; see README.md in this directory.
{blurb}
--
-- Safe to re-run: every row has a deterministic id (md5('vaca-verde:<kind>:<key>')::uuid)
-- and is inserted with ON CONFLICT DO NOTHING, so a second run changes nothing and never
-- overwrites later edits. When the organization "Vaca Verde" (or its business admin{', or its branch "Ruta 51"' if needs_branch else ''}) does not
-- exist yet, the file prints a NOTICE and changes nothing. Apply after 001 (and migrations 0030 / 0016-0018).
--
-- Run as the database owner, e.g.:
--   psql -U commerce_owner -d <db> -v ON_ERROR_STOP=1 -f {filename}
-- If the owner role is subject to row level security, scope the session to the
-- organization first: PGOPTIONS="-c app.current_org_id=<organization uuid>".

SET client_encoding = 'UTF8';

BEGIN;

CREATE TEMP TABLE _vv_seed_ctx ({ctx_cols}) ON COMMIT DROP;

DO $$
DECLARE
    v_scope uuid := NULLIF(current_setting('app.current_org_id', true), '')::uuid;
    v_org   uuid;
    v_user  uuid;{branch_decl}
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
      AND roles @> '[{{"name":"business-admin"}}]'::jsonb
    ORDER BY created_at_utc, id
    LIMIT 1;

    IF v_user IS NULL THEN
        RAISE NOTICE 'Vaca Verde seed skipped: the organization has no business-admin user to own the records.';
        RETURN;
    END IF;
{branch_block}
    {ctx_insert}
END $$;
"""


def suppliers_sql(suppliers):
    out = [header(
        "Vaca Verde suppliers seed: the supplier category \"Carne\", the suppliers and their contacts.",
        "-- Cities are global Georef data (migration 0028); suppliers point at them by INDEC id.",
        "002_vaca_verde_suppliers.sql", False)]
    out.append("""-- Supplier category "Carne" (organization scoped; found by key when it already exists).
INSERT INTO supplier_categories (id, organization_id, name, key, sort_order)
SELECT md5('vaca-verde:supplier-category:carne')::uuid, c.organization_id, 'Carne', 'carne', 0
FROM _vv_seed_ctx c
ON CONFLICT DO NOTHING;
""")
    rows_ = []
    for s in suppliers:
        rows_.append([q(slug(s["name"])), q(s["name"]), q(s["phone"]), q(s["email"]), q(s["street"]),
                      q(s["number"]), q(INDEC[s["city"]] if s["city"] else None)])
    out.append("-- Suppliers. The id derives from the name slug; the category is looked up by key.")
    out.append(f"""INSERT INTO suppliers (id, organization_id, display_name, phone, email, address_street, address_number,
                       city_id, category_id, is_enabled, created_by_user_id)
SELECT md5('vaca-verde:supplier:' || v.key)::uuid, c.organization_id, v.display_name, v.phone, v.email,
       v.address_street, v.address_number, ci.id, sc.id, true, c.user_id
FROM (VALUES
{values(rows_)}
) AS v (key, display_name, phone, email, address_street, address_number, city_indec_id)
CROSS JOIN _vv_seed_ctx c
LEFT JOIN cities ci ON ci.indec_id = v.city_indec_id
LEFT JOIN supplier_categories sc ON sc.organization_id = c.organization_id AND sc.key = 'carne'
ON CONFLICT DO NOTHING;
""")
    crow = []
    for s in suppliers:
        for i, ct in enumerate(s["contacts"]):
            crow.append([q(slug(s["name"])), q(str(i)), q(ct["first"]), q(ct["last"]), q(ct["phone"]),
                         q(ct["email"]), q(ct["role"]), "true" if i == 0 else "false"])
    out.append("-- Contacts: the first one of each supplier is its primary contact.")
    out.append(f"""INSERT INTO supplier_contacts (id, organization_id, supplier_id, first_name, last_name, phone, email, role,
                              is_primary, sort_order)
SELECT md5('vaca-verde:supplier-contact:' || v.key || ':' || v.n)::uuid, c.organization_id,
       md5('vaca-verde:supplier:' || v.key)::uuid, v.first_name, v.last_name, v.phone, v.email, v.role,
       v.is_primary, v.n::integer
FROM (VALUES
{values([r[:-1] + [r[-1] + '::boolean'] for r in crow])}
) AS v (key, n, first_name, last_name, phone, email, role, is_primary)
CROSS JOIN _vv_seed_ctx c
WHERE EXISTS (SELECT 1 FROM suppliers s
              WHERE s.organization_id = c.organization_id AND s.id = md5('vaca-verde:supplier:' || v.key)::uuid)
ON CONFLICT DO NOTHING;


COMMIT;
""")
    return "\n".join(out), len(crow)


MOSTRADOR_BASE_FROM = "2026-10-02"


def mostrador_base(prices):
    """What 004 publishes as Mostrador's BASE price: Reparto's base where the product exists there, else final / 1.48."""
    out = {}
    for name, final in prices["Mostrador"].items():
        out[name] = prices["Reparto"][name] if name in prices["Reparto"] else (final / Decimal("1.48")).quantize(Decimal("0.01"), rounding="ROUND_HALF_UP")
    return out


def catalog_sql(products, prices):
    out = [header(
        "Vaca Verde catalog seed: product categories, products, presentations and price lists.",
        "-- Branch \"Ruta 51\" hosts the butcher shop and the distribution: one catalog, two price lists\n"
        "-- (Mostrador = default, final prices here and base prices from 004; Reparto = base + list-specific rate components).\n"
        "-- Product codes 001... are provisional (scale integration will replace them).",
        "003_vaca_verde_catalog.sql", True)]

    out.append("-- Product categories (found by name when they already exist). Almacén and Bebidas start empty.")
    rows_ = [[q(n), q(slug(n)), q(icon)] for n, icon in CATEGORIES]
    out.append(f"""INSERT INTO categories (id, organization_id, name, icon_key)
SELECT md5('vaca-verde:category:' || v.key)::uuid, c.organization_id, v.name, v.icon_key
FROM (VALUES
{values(rows_)}
) AS v (name, key, icon_key)
CROSS JOIN _vv_seed_ctx c
ON CONFLICT DO NOTHING;
""")

    rows_ = [[q(p["key"]), q(p["name"]), q(p["category"])] for p in products]
    out.append("-- Products. The category is resolved by name; the unit is a bare uuid (no units table exists yet):")
    out.append("-- md5('vaca-verde:unit:kg') stands for kilograms.")
    out.append(f"""INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id)
SELECT md5('vaca-verde:product:' || v.key)::uuid, c.organization_id, c.branch_id, v.name, cat.id,
       md5('vaca-verde:unit:kg')::uuid, c.user_id
FROM (VALUES
{values(rows_)}
) AS v (key, name, category_name)
CROSS JOIN _vv_seed_ctx c
JOIN categories cat ON cat.organization_id = c.organization_id AND lower(btrim(cat.name)) = lower(v.category_name)
ON CONFLICT DO NOTHING;
""")

    rows_ = [[q(p["key"]), q(p["code"])] for p in products]
    out.append("-- One Weighted presentation \"Por kg\" per product, code 001... ordered by category, then name.")
    out.append(f"""INSERT INTO presentations (id, organization_id, branch_id, product_id, name, quantity_behavior, unit_id,
                           identification_code, created_by_user_id)
SELECT md5('vaca-verde:presentation:' || v.key)::uuid, c.organization_id, c.branch_id,
       md5('vaca-verde:product:' || v.key)::uuid, 'Por kg', 'Weighted', md5('vaca-verde:unit:kg')::uuid,
       v.code, c.user_id
FROM (VALUES
{values(rows_)}
) AS v (key, code)
CROSS JOIN _vv_seed_ctx c
WHERE EXISTS (SELECT 1 FROM products p
              WHERE p.organization_id = c.organization_id AND p.id = md5('vaca-verde:product:' || v.key)::uuid)
ON CONFLICT DO NOTHING;
""")

    out.append("""-- Price lists of the branch, resolved into _vv_lists(key, id):
--   * an existing list with the same name (case-insensitive) is reused;
--   * the branch's untouched empty "Default" list (the one provisioning creates) becomes "Mostrador";
--   * otherwise the list is created. Mostrador is created as the default only when the branch has none
--     (price_lists_one_default_per_branch allows one); Reparto never is.
CREATE TEMP TABLE _vv_lists (key text PRIMARY KEY, id uuid NOT NULL) ON COMMIT DROP;

DO $$
DECLARE
    c  _vv_seed_ctx%ROWTYPE;
    k  text;
    n  text;
    v_id uuid;
BEGIN
    SELECT * INTO c FROM _vv_seed_ctx;
    IF NOT FOUND THEN
        RETURN;
    END IF;

    UPDATE price_lists pl
    SET name = 'Mostrador'
    WHERE pl.organization_id = c.organization_id AND pl.branch_id = c.branch_id
      AND pl.is_default AND pl.name = 'Default'
      AND NOT EXISTS (SELECT 1 FROM price_list_entries e WHERE e.price_list_id = pl.id)
      AND NOT EXISTS (SELECT 1 FROM rate_component_sets s WHERE s.price_list_id = pl.id)
      AND NOT EXISTS (SELECT 1 FROM price_lists o
                      WHERE o.organization_id = c.organization_id AND o.branch_id = c.branch_id
                        AND lower(btrim(o.name)) = 'mostrador');

    FOREACH k IN ARRAY ARRAY['mostrador', 'reparto'] LOOP
        n := initcap(k);
        SELECT pl.id INTO v_id
        FROM price_lists pl
        WHERE pl.organization_id = c.organization_id AND pl.branch_id = c.branch_id
          AND lower(btrim(pl.name)) = k
        ORDER BY pl.created_at_utc, pl.id
        LIMIT 1;

        IF v_id IS NULL THEN
            v_id := md5('vaca-verde:price-list:' || k)::uuid;
            INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id)
            VALUES (v_id, c.organization_id, c.branch_id, n,
                    k = 'mostrador' AND NOT EXISTS (SELECT 1 FROM price_lists d
                                                    WHERE d.organization_id = c.organization_id
                                                      AND d.branch_id = c.branch_id AND d.is_default),
                    c.user_id)
            ON CONFLICT DO NOTHING;
            IF k = 'mostrador' AND NOT EXISTS (SELECT 1 FROM price_lists d
                                               WHERE d.id = v_id AND d.is_default) THEN
                RAISE NOTICE 'Vaca Verde catalog: the branch already has a different default price list; Mostrador was created but is not the default.';
            END IF;
        END IF;
        INSERT INTO _vv_lists (key, id) VALUES (k, v_id);
    END LOOP;
END $$;
""")

    erows = []
    for lst in PERSISTED_LISTS:
        for name, price in sorted(prices[lst].items(), key=lambda kv: fold(kv[0])):
            erows.append([q(lst.lower()), q(slug(name)), q(str(price)), ])
    out.append(f"-- Entries (effective from {EFFECTIVE_FROM}). Reparto holds the BASE price; its rate set composes it.")
    out.append(f"""INSERT INTO price_list_entries (id, organization_id, branch_id, price_list_id, presentation_id, unit_price,
                                effective_from, source, created_by_user_id)
SELECT md5('vaca-verde:price-entry:' || v.list_key || ':' || v.product_key || ':{EFFECTIVE_FROM}')::uuid,
       c.organization_id, c.branch_id, l.id, md5('vaca-verde:presentation:' || v.product_key)::uuid,
       v.unit_price::numeric(12,2), DATE '{EFFECTIVE_FROM}', 'Manual', c.user_id
FROM (VALUES
{values(erows)}
) AS v (list_key, product_key, unit_price)
CROSS JOIN _vv_seed_ctx c
JOIN _vv_lists l ON l.key = v.list_key
WHERE EXISTS (SELECT 1 FROM presentations pr
              WHERE pr.organization_id = c.organization_id AND pr.branch_id = c.branch_id
                AND pr.id = md5('vaca-verde:presentation:' || v.product_key)::uuid)
ON CONFLICT DO NOTHING;
""")

    comps = [("IVA", "IVA (10,5%)", "10.5", 1), ("IB", "Ingresos Brutos (2,5%)", "2.5", 2),
             ("FLETE", "Flete (7%)", "7", 3), ("REMARCACION", "Remarcación (25%)", "25", 4)]
    out.append("""-- Reparto's rate components: LIST-SPECIFIC (price_list_id = Reparto, never the organization default),
-- so Mostrador is not composed by this seed (004 gives it its own set). All four apply to the base: base x 1.45.
INSERT INTO rate_component_sets (id, organization_id, branch_id, price_list_id, effective_from, created_by_user_id)
SELECT md5('vaca-verde:rate-set:reparto:{d}')::uuid, c.organization_id, c.branch_id, l.id, DATE '{d}', c.user_id
FROM _vv_seed_ctx c
JOIN _vv_lists l ON l.key = 'reparto'
ON CONFLICT DO NOTHING;
""".replace("{d}", EFFECTIVE_FROM))
    crow = [[q(code), q(label), q(pct), str(order)] for code, label, pct, order in comps]
    out.append(f"""INSERT INTO rate_components (id, organization_id, branch_id, set_id, code, label, percentage, calculation_base,
                             component_order)
SELECT md5('vaca-verde:rate-component:reparto:' || v.code)::uuid, c.organization_id, c.branch_id, s.id, v.code, v.label,
       v.percentage::numeric(9,4), 'Base', v.component_order
FROM (VALUES
{values(crow)}
) AS v (code, label, percentage, component_order)
CROSS JOIN _vv_seed_ctx c
JOIN rate_component_sets s ON s.organization_id = c.organization_id AND s.branch_id = c.branch_id
                          AND s.id = md5('vaca-verde:rate-set:reparto:{EFFECTIVE_FROM}')::uuid
ON CONFLICT DO NOTHING;


COMMIT;
""")
    return "\n".join(out)


# ------------------------------------------------------------------ report

def report(suppliers, products, prices, notes, reparto, flags, merges, contact_count):
    cats = {}
    for p in products:
        cats.setdefault(p["category"], []).append(p)
    L = []
    a = L.append
    a("# Informe del seed de proveedores y catálogo de Vaca Verde")
    a("")
    a("Generado por `generate_suppliers_catalog.py` a partir de las planillas del dueño. "
      "Incluye lo que se cargó, las decisiones tomadas y todo lo que hay que **revisar**.")
    a("")
    a("## Resumen")
    a("")
    a("| Qué | Cantidad |")
    a("| --- | --- |")
    a(f"| Proveedores | {len(suppliers)} |")
    a(f"| Contactos de proveedores | {contact_count} |")
    a("| Categoría de proveedores | 1 (Carne) |")
    a(f"| Categorías de productos | {len(CATEGORIES)} (Almacén y Bebidas vacías) |")
    a(f"| Productos | {len(products)} |")
    a(f"| Presentaciones («Por kg», pesables, kg) | {len(products)} |")
    for lst in PERSISTED_LISTS:
        a(f"| Precios en la lista {lst} | {len(prices[lst])} |")
    a("| Conjunto de tasas | 1, solo de la lista Reparto (IVA 10,5 %, IB 2,5 %, Flete 7 %, Remarcación 25 %, todas sobre la base) |")
    a("| Lista Clientes | no se carga (los clientes se precian con Reparto) |")
    a("")
    a(f"Todos los precios rigen desde el {EFFECTIVE_FROM[8:]}/{EFFECTIVE_FROM[5:7]}/{EFFECTIVE_FROM[:4]}. "
      "La sucursal es «Ruta 51». Volver a correr el seed no cambia nada.")
    a("")
    a("## Proveedores")
    a("")
    a("| Proveedor | Ciudad | Dirección | Teléfono | Email | Contactos |")
    a("| --- | --- | --- | --- | --- | --- |")
    for s in suppliers:
        addr = " ".join(x for x in (s["street"], s["number"]) if x) or "—"
        cts = "; ".join(" ".join(x for x in (c["first"], c["last"]) if x) + (f" ({c['role']})" if c["role"] else "")
                        + (f" {c['phone']}" if c["phone"] else "") for c in s["contacts"]) or "—"
        a(f"| {s['name']} | {CITY_LABEL.get(s['city'], s['city']) or '—'} | {addr} | {s['phone'] or '—'} | {s['email'] or '—'} | {cts} |")
    a("")
    a("Cambios de nombre: «VentasDF Distribuid.» pasó a «Ventas DF Distribuidora»; «Amalia (San Pedro)» pasó a «Amalia» "
      "(San Pedro quedó como ciudad); «Frigorifico»/«Carniceria» se escribieron con tilde. "
      "Las dos filas «Villarino … FRIMSA» se unieron en **un** proveedor «Frimsa» con dos contactos (Agustín y Matías Villarino, cada uno con su teléfono). "
      "Los teléfonos quedaron solo con dígitos.")
    a("")
    a("## Catálogo")
    a("")
    for cname, _ in CATEGORIES:
        items = cats.get(cname, [])
        a(f"### {cname} ({len(items)})")
        a("")
        if not items:
            a("Sin productos por ahora.")
            a("")
            continue
        a("| Código | Producto | Mostrador (final, 01/10) | Reparto (base) |")
        a("| --- | --- | --- | --- |")
        for p in items:
            def pr(lst):
                v = prices[lst].get(p["name"])
                return money(v) if v is not None else "—"
            a(f"| {p['code']} | {p['name']} | {pr('Mostrador')} | {pr('Reparto')} |")
        a("")
    a("Los códigos 001, 002, … son **provisorios**: se asignan por categoría y luego por nombre, y se reemplazarán cuando se integre la balanza.")
    a("")
    a("## Productos unidos (un mismo corte en varias listas)")
    a("")
    a("Un corte presente en varias planillas es **un solo producto** con un precio por lista. "
      "Los siguientes aparecen a la vez en las listas de reparto y en las de mostrador (las hojas «Vacuna» y «MEDIA» son idénticas):")
    a("")
    for name, srcs in merges:
        if {"Reparto", "Clientes"} & set(srcs) and {"Vacuna", "MEDIA"} & set(srcs):
            a(f"- {name}: {', '.join(srcs)}")
    a("")
    a("## Para revisar")
    a("")
    n = 1

    def flag(text):
        nonlocal n
        a(f"{n}. {text}")
        n += 1

    flag("**Milanesa de ternera de bola de lomo** es un producto de ejemplo (provisorio, categoría Milanesas) y **no tiene precio**; "
         "se puede editar o dar de baja. Cargale un precio cuando lo definas.")
    for f in flags:
        if isinstance(f, str):
            flag(f)
    sinp = [f for f in flags if isinstance(f, tuple) and f[0] == "achura-sin-precio"]
    for _, name, orig, final in sinp:
        flag(f"**{name}** (Achuras) dice «{final}» en «Precio con aumento» (precio original {money(dec(orig))}): se creó el producto **sin precio**.")
    inc = [f for f in flags if isinstance(f, tuple) and f[0] == "achura"]
    if inc:
        flag("**Achuras: los porcentajes no cierran.** Se usó siempre la columna «Precio con aumento» como precio final de Mostrador. "
             "Comparación con precio original × (1 + %):")
        a("")
        a("   | Producto | Original | % | Calculado | Precio con aumento (usado) |")
        a("   | --- | --- | --- | --- | --- |")
        for _, name, orig, pct, exp, fin in inc:
            a(f"   | {name} | {money(orig)} | {pct * 100:.0f} % | {money(exp)} | {money(fin)} |")
        a("")
    # Reparto composition check
    bad = []
    rep_final = {display(r[0]): Decimal(str(r[3][6])) for r in reparto}
    for name, base in prices["Reparto"].items():
        composed = base * Decimal("1.45")
        if composed != rep_final[name]:
            bad.append((name, base, composed, rep_final[name]))
    if bad:
        flag("**Reparto: el precio compuesto no coincide con el PRECIO FINAL de la planilla** en:")
        a("")
        for name, base, comp, fin in bad:
            a(f"   - {name}: base {money(base)} × 1,45 = {money(comp)}; la planilla dice {money(fin)}.")
        a("")
    else:
        flag(f"Reparto: base × 1,45 coincide con el PRECIO FINAL de la planilla en los {len(prices['Reparto'])} cortes.")
    # The Mostrador base prices of 004 (Reparto's base where the product exists there, else final / 1.48).
    derived = mostrador_base(prices)
    below = [(n, b * Decimal("1.48"), prices["Reparto"][n] * Decimal("1.45"))
             for n, b in derived.items() if n in prices["Reparto"] and b * Decimal("1.48") < prices["Reparto"][n] * Decimal("1.45")]
    shared = sum(1 for n in derived if n in prices["Reparto"])
    counter_only = len(derived) - shared
    flag(f"**Mostrador pasa a precio base** (seed 004, rige desde {MOSTRADOR_BASE_FROM[8:]}/{MOSTRADOR_BASE_FROM[5:7]}/{MOSTRADOR_BASE_FROM[:4]}): "
         "IVA 10,5 % + IB 2,5 % + Remarcación 35 % sobre la base (sin flete, ×1,48). La base de cada corte que también está en Reparto es la base de Reparto "
         f"({shared} cortes, p. ej. Bola de lomo: 11.400 × 1,48 = 16.872 contra 18.000 antes); "
         f"la de los cortes que solo se venden en mostrador sale del precio final cargado dividido 1,48 y redondeado a centavos ({counter_only} cortes, p. ej. Lengua: 11.570 / 1,48 = 7.817,57). "
         "Los precios anteriores de Mostrador quedan en el historial.")
    if below:
        flag("**Mostrador quedaría por debajo de Reparto** (la lista Mostrador tiene a Reparto como piso): "
             + ", ".join(f"{n} ({money(m)} < {money(r)})" for n, m, r in below) + ".")
    else:
        flag("Piso de Mostrador: ningún corte con precio en las dos listas queda por debajo de Reparto (×1,48 contra ×1,45 sobre la misma base). "
             "Los cortes que solo se venden en mostrador no tienen precio de Reparto con qué comparar.")
    flag("Mostrador baja respecto del precio final anterior en los cortes que también están en Reparto. "
         "La «Lista Clientes» original (14.500 para Asado completo) no se carga: Reparto compone ese corte en 15.370.")
    no_mostrador = sorted((p["name"] for p in products if p["name"] not in prices["Mostrador"] and p["name"] != PROVISIONAL),
                          key=fold)
    flag(f"Productos **sin precio en Mostrador** ({len(no_mostrador)}; solo existen en la lista de Reparto o no tienen precio): "
         + ", ".join(no_mostrador) + ".")
    only_retail = sorted((p["name"] for p in products if p["category"] == "Vacuno" and p["name"] not in prices["Reparto"]
                          and p["name"] in prices["Mostrador"]), key=fold)
    flag("Cortes de mostrador **sin precio en Reparto** (esa planilla es de cortes al por mayor): "
         + ", ".join(only_retail) + ".")
    flag("**«Nalga» (mostrador) y «Nalga con tapa» / «Nalga sin tapa» / «Nalga feteada» (reparto)** quedaron como productos distintos "
         "porque no son el mismo nombre. «Cuadrada», «Matambre», «Paleta», «Peceto», «Tapa de asado», «Tapa de nalga», "
         "«Colita de cuadril», «Entraña», «Entraña vaca congelada», «Vacío» y «Bola de lomo» sí se unieron entre listas. Avisá si querés unir o separar alguno.")
    flag("**«Vacío novillo seleccionado»** y **«Vacío marca Santa Inés»** (esta última escrita «Ines» en la planilla) son productos separados de **«Vacío»**.")
    flag("**Cerdo:** los cortes que comparten nombre con uno vacuno se llamaron «… de cerdo» "
         "(Costilla, Matambre, Cuadril, Lomo, Cuadrada, Nalga, Bola de lomo, Picada). «Bondiola», «Pulpa», «Papada», «Pernil», «Solomillo», «Pechito» y «Tortuguita» quedaron igual.")
    flag("**«Pollo»** pasó a **«Pollo entero»**. «Bondiola Salda» (error de tipeo) pasó a **«Bondiola salada»**. "
         "«Medallón de Pollo Jamón y Ques» pasó a **«Medallón de pollo jamón y queso»**. «Milanesas Pechuga» pasó a **«Milanesas de pechuga»** (como «Milanesas de muslo»).")
    flag("**«Patitas»** (Pollo) y **«Medallón de pollo»** tienen el mismo precio (10.700): puede ser un error de carga.")
    flag("**Sin ral:** se dejó el nombre como en la planilla; no está claro si es «Sin ral» u otra palabra. Confirmar.")
    flag("La planilla de Reparto tiene **25** cortes (5 colgados y 20 al vacío), no 26.")
    for name, note in sorted(notes.items(), key=lambda kv: fold(kv[0])):
        flag(f"Nota de la planilla Clientes sobre **{name}**: «{note}». Los productos no tienen campo de descripción, por eso queda solo acá.")
    flag("**Proveedor Swift:** el contacto «Nelson Luis Beber Valdemarin» se dejó **entero** en el nombre (no se sabe si «Nelson Luis» es nombre compuesto); "
         "su email se cargó en el proveedor y en el contacto.")
    flag("**Ventas DF Distribuidora:** la celda decía «Paso contacto Gustavo del deposito»; se cargó el contacto «Gustavo» con rol «Depósito». "
         "El teléfono de la planilla quedó en el proveedor (no se sabe si es el de Gustavo). La ciudad decía «Munro Florida GBA»: se usó **Munro**.")
    flag("**Ciudades con homónimos:** «Pilar» se cargó como Pilar (Buenos Aires), «San Pedro» como San Pedro (Buenos Aires) y «Merlo» como Merlo (Buenos Aires); "
         "la planilla no aclara la provincia. «Capital Federal» es Ciudad de Buenos Aires.")
    flag("**Gorina, Frigolar y Frimsa** no tienen ciudad en la planilla: quedaron **sin ciudad** para no adivinar.")
    flag("**Frimsa** quedó sin teléfono propio: cada contacto tiene el suyo.")
    flag("**Carnicería Cow** y **Amalia** no tienen contacto en la planilla. La dirección «Av nazca 5096» / «Elias Alippi 1890 Merlo» se separó en calle y número.")
    flag("Todos los proveedores se categorizaron como **Carne** (la planilla no distingue).")
    a("")
    return "\n".join(L)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--proveedores", required=True)
    ap.add_argument("--achuras", required=True)
    ap.add_argument("--precios", required=True)
    ap.add_argument("--vacuno-cerdo-pollo", required=True)
    ap.add_argument("--out-dir", default=HERE)
    a = ap.parse_args()

    flags, merges = [], []
    suppliers = build_suppliers(a.proveedores, flags)
    products, prices, notes, reparto = build_catalog(a.precios, a.vacuno_cerdo_pollo, a.achuras, flags, merges)

    sup_sql, contacts = suppliers_sql(suppliers)
    cat_sql = catalog_sql(products, prices)
    rep = report(suppliers, products, prices, notes, reparto, flags, merges, contacts)

    for name, content in (("002_vaca_verde_suppliers.sql", sup_sql), ("003_vaca_verde_catalog.sql", cat_sql),
                          ("report-catalogo.md", rep)):
        with open(os.path.join(a.out_dir, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
    print(f"suppliers={len(suppliers)} contacts={contacts} categories={len(CATEGORIES)} products={len(products)} "
          f"mostrador={len(prices['Mostrador'])} reparto={len(prices['Reparto'])}")


if __name__ == "__main__":
    main()
