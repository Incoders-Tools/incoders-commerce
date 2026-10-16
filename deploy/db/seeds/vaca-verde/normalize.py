# -*- coding: utf-8 -*-
"""Normalize the Vaca Verde customer spreadsheets.

Usage:
    python normalize.py --clientes CLIENTES.xlsx --clientes1 CLIENTES1.xlsx \
        --cities-csv cities_rows.csv --out-dir build

Outputs cities.json, business_types.json and customers.json into
--out-dir, and report.md (Spanish review report) next to this script.
generate_seed.py turns the JSON files into the seed SQL.
Requires openpyxl.
"""
import argparse, openpyxl, csv, json, re, unicodedata, os
from collections import OrderedDict

_ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
_ap.add_argument("--clientes", required=True, help="path to CLIENTES.xlsx")
_ap.add_argument("--clientes1", required=True, help="path to CLIENTES1.xlsx")
_ap.add_argument("--cities-csv", required=True, help="path to cities_rows.csv (reference cities)")
_ap.add_argument("--out-dir", default="build", help="output directory (default: build)")
ARGS = _ap.parse_args()
CSV_CITIES = ARGS.cities_csv
HERE = ARGS.out_dir
os.makedirs(HERE, exist_ok=True)
F1, F2 = os.path.basename(ARGS.clientes), os.path.basename(ARGS.clientes1)


def strip_acc(s):
    return "".join(c for c in unicodedata.normalize("NFKD", s) if not unicodedata.combining(c))


def nkey(s):
    return re.sub(r"[^a-z0-9]+", " ", strip_acc(s).lower()).strip()


def clean(s):
    if s is None:
        return None
    s = re.sub(r"\s+", " ", str(s)).strip()
    return s or None


PARTICLES = {"de", "del", "y"}


def tcase(s):
    """Title Case only when the text is all-caps; mixed-case input is kept (PyP, Shell...)."""
    s = clean(s)
    if not s:
        return None
    if re.search(r"[a-záéíóúñü]", s):
        return s
    out = []
    for i, tok in enumerate(s.split(" ")):
        low = tok.lower()
        if i > 0 and low in PARTICLES:
            out.append(low)
            continue
        out.append(re.sub(r"[^\W\d_]+", lambda m: m.group(0).capitalize(), tok))
    return " ".join(out)


TYPE_ACCENT = {"almacen": "Almacén", "carniceria": "Carnicería", "polleria": "Pollería",
               "rotiseria": "Rotisería", "bodegon": "Bodegón", "sangucheria": "Sanguchería"}


def fix_type_words(s):
    return " ".join(TYPE_ACCENT.get(strip_acc(w).lower(), w) for w in s.split(" ")) if s else s


NAME_ALIAS = {"aut servicio arr": "Autoservicio Arrecifes", "autoservicio arrec": "Autoservicio Arrecifes"}


def name_norm(s):
    s = clean(s)
    if not s:
        return None
    s = clean(s.replace("?", " "))
    alias = NAME_ALIAS.get(nkey(s))
    if alias:
        return alias
    return fix_type_words(tcase(s))


def addr_norm(s):
    s = clean(s)
    if not s:
        return None
    s = tcase(s)
    return re.sub(r"\bAv\b(?!\.)", "Av.", s)


# ---- cities
CITY_NAMES = {
    "arroyo_dulce": "Arroyo Dulce", "pergamino": "Pergamino", "ramallo": "Ramallo", "rojas": "Rojas",
    "santa_lucia": "Santa Lucía", "arrecifes": "Arrecifes", "la_plata": "La Plata", "baradero": "Baradero",
    "chacabuco": "Chacabuco", "zarate": "Zárate", "san_andres_de_giles": "San Andrés de Giles",
    "capitan_sarmiento": "Capitán Sarmiento", "carmen_de_areco": "Carmen de Areco", "santiago_del_estero": "Santiago del Estero",
    "salto": "Salto", "san_pedro": "San Pedro", "san_antonio_de_areco": "San Antonio de Areco",
    "capital_federal": "Capital Federal", "perez_millan": "Pérez Millán", "cordoba": "Córdoba",
    "urquiza": "Urquiza", "doyle": "Doyle", "ines_indart": "Inés Indart"}
# Owner decision: the reference city "Sarmiento" is "Capitán Sarmiento".
# Only name and key change; id, sort_order, is_active and audit dates are kept.
CITY_KEY_RENAMES = {"sarmiento": "capitan_sarmiento"}
cities = []
with open(CSV_CITIES, encoding="utf-8-sig", newline="") as f:
    for r in csv.DictReader(f):
        key = CITY_KEY_RENAMES.get(r["key"], r["key"])
        cities.append(OrderedDict(id=r["id"], key=key, name=CITY_NAMES[key],
                                  isActive=r["is_active"] == "true", sortOrder=int(r["sort_order"]),
                                  createdAt=r["created_at"], updatedAt=r["updated_at"], new=False))
cities.sort(key=lambda c: c["sortOrder"])
maxsort = max(c["sortOrder"] for c in cities)

LOC = {  # normalized locality -> (city key | None, flag)
    "arrecifes": ("arrecifes", None), "s areco": ("san_antonio_de_areco", None),
    "san antonio areco": ("san_antonio_de_areco", None), "san antonio de areco": ("san_antonio_de_areco", None),
    "salto": ("salto", None), "san pedro": ("san_pedro", None), "baradero": ("baradero", None),
    "pergamino": ("pergamino", None), "chacabuco": ("chacabuco", None),
    "sarmiento": ("capitan_sarmiento", None), "capitan sarmiento": ("capitan_sarmiento", None),
    "san nicolas": ("san_nicolas", "new"), "rio tala": ("rio_tala", "new"),
    "ruta 9": (None, "ruta9"),
}
NEW_CITIES = {"san_nicolas": "San Nicolás", "rio_tala": "Río Tala"}

BT = {"resto bar": "Resto Bar", "restobar": "Resto Bar", "parrilla": "Parrilla", "almacen": "Almacén",
      "polleria": "Pollería", "carniceria": "Carnicería", "supermercado": "Supermercado",
      "rotiseria": "Rotisería", "rotiseri": "Rotisería", "sangucheria": "Sanguchería",
      "estacion de servicio": "Estación de Servicio", "bodegon": "Bodegón", "carrito": "Carrito"}
BT_NAME_HINT = [("almacen", "Almacén"), ("despensa", "Almacén"), ("carrito", "Carrito"), ("parrilla", "Parrilla"),
                ("carniceria", "Carnicería"), ("autoservicio", "Supermercado"), ("super", "Supermercado")]


def cuit_ok(d):
    if len(d) != 11:
        return False
    w = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2]
    s = sum(int(a) * b for a, b in zip(d[:10], w))
    c = 11 - s % 11
    c = 0 if c == 11 else 9 if c == 10 else c
    return c == int(d[10])


def phone_norm(v):
    if v is None:
        return None
    s = str(v).strip()
    if re.fullmatch(r"\d+\.0", s):
        s = s[:-2]
    return re.sub(r"\D", "", s) or None


# ---- read
rows = []
dropped = []
stats = dict(raw=0, titles=0, headers=0, empty=0, garbage=0)


def read_sheet(path, sheetname, colmap, fname):
    ws = openpyxl.load_workbook(path)[sheetname]
    for r in ws.iter_rows(min_row=1):
        vals = {k: r[i].value for k, i in colmap.items()}
        rn = r[0].row
        src = f"{fname}!{sheetname}:fila {rn}"
        if all(v in (None, "") for v in vals.values()):
            continue
        stats["raw"] += 1
        txt = [str(v) for v in vals.values() if v is not None]
        if any("=AI(" in t for t in txt):
            stats["garbage"] += 1
            dropped.append((src, "fórmula =AI(...) (basura de Google Sheets), sin nombre ni datos reales"))
            continue
        n = clean(vals["nombre"]) or ""
        if n.upper() in ("LISTA DE CLIENTES", "LISTA CLIENTES COTO") and not vals.get("tel"):
            stats["titles"] += 1
            dropped.append((src, f"título de sección «{n}»"))
            continue
        if n.upper() == "NOMBRE":
            stats["headers"] += 1
            dropped.append((src, "fila de encabezado repetida"))
            continue
        if not n:
            stats["empty"] += 1
            dropped.append((src, "fila sin nombre"))
            continue
        vals["src"] = src
        rows.append(vals)


read_sheet(ARGS.clientes, "Clientes Vaca Verde",
           dict(nombre=1, dir=2, tel=3, loc=4, cliente=5, espec=6, obs=7), F1)
read_sheet(ARGS.clientes1, "Hoja1", dict(nombre=0, dir=1, tel=2, loc=3), F2)

review = []
assumptions = []
used_new_cities = set()
flag_ruta9 = []


def process(v):
    c = OrderedDict()
    c["displayName"] = name_norm(v["nombre"])
    rawname = clean(v["nombre"])
    if "?" in rawname:
        review.append(("Nombre con «?»", f"«{rawname}» ({v['src']}): se quitó el «?» → «{c['displayName']}». Confirmar el nombre real."))
    addr = addr_norm(v.get("dir"))
    notes = []
    if addr and "?" in addr:
        review.append(("Dirección incompleta", f"«{addr}» ({v['src']}) para {c['displayName']}: la dirección original trae «?»; se dejó tal cual."))
    elif addr and addr.lower() == "ruta":
        review.append(("Dirección incompleta", f"«{addr}» ({v['src']}) para {c['displayName']}: falta el número de ruta."))
    cityKey = None
    loc = clean(v.get("loc"))
    if loc:
        k = nkey(loc)
        if k not in LOC:
            raise SystemExit(f"unmapped locality {loc}")
        ck, flag = LOC[k]
        if flag == "ruta9":
            addr = f"{addr}, Ruta 9" if addr else "Ruta 9"
            flag_ruta9.append(f"{c['displayName']} ({v['src']})")
        else:
            cityKey = ck
            if flag == "new":
                used_new_cities.add(ck)
    c["contactName"] = None
    cont = tcase(clean(v.get("cliente")))
    if cont:
        if nkey(cont) in BT:
            review.append(("Contacto descartado", f"{c['displayName']} ({v['src']}): la columna CLIENTE decía «{cont}» (es un rubro, no una persona); se dejó sin contacto."))
        elif nkey(cont) == nkey(c["displayName"]):
            review.append(("Contacto igual al nombre", f"{c['displayName']} ({v['src']}): el contacto era idéntico al nombre; se dejó sin contacto."))
        else:
            c["contactName"] = {"Ignacion": "Ignacio"}.get(cont, cont)
    c["addressStreet"] = addr
    c["phone"] = phone_norm(v.get("tel"))
    c["cityKey"] = cityKey
    btk = None
    extra = []
    esp = clean(v.get("espec"))
    if esp:
        parts = [p.strip() for p in re.split(r"/|\s+Y\s+", esp, flags=re.I) if p.strip()]
        keys = [BT.get(nkey(p)) for p in parts]
        if all(keys):
            btk, extra = keys[0], keys[1:]
        else:
            if "carnic" in nkey(esp) or nkey(esp) == "super campeones":
                btk = "Carnicería"
            notes.append(f"Especificación original: {esp}")
            review.append(("Especificación no estándar", f"{c['displayName']} ({v['src']}): «{esp}» → rubro {btk}; texto original guardado en notas."))
    if not btk and not esp:
        nk = nkey(c["displayName"])
        for w, t in BT_NAME_HINT:
            if nk.startswith(w) or (w == "super" and f" {w} " in f" {nk} "):
                btk = t
                assumptions.append(f"Rubro «{t}» inferido del nombre para {c['displayName']} ({v['src']}).")
                break
        if not btk and nk in BT:
            btk = BT[nk]
            assumptions.append(f"Rubro «{btk}» inferido del nombre para {c['displayName']} ({v['src']}).")
    if extra:
        notes.append("También: " + ", ".join(extra))
    c["businessTypeKey"] = btk
    c["taxIdType"] = "None"
    c["taxId"] = None
    obs = v.get("obs")
    if obs not in (None, ""):
        o = str(obs).strip()
        if re.fullmatch(r"\d{2}-\d{8}-\d", o):
            d = o.replace("-", "")
            c["taxIdType"], c["taxId"] = "Cuit", d
            if not cuit_ok(d):
                review.append(("CUIT con dígito verificador inválido", f"{c['displayName']} ({v['src']}): {o}. Se conservó."))
        elif re.fullmatch(r"\d{7,8}\.0", o) or re.fullmatch(r"\d{1,2}\.\d{3}\.\d{3}", o) or re.fullmatch(r"\d{7,8}", o):
            d = o[:-2] if o.endswith(".0") else o.replace(".", "")
            c["taxIdType"], c["taxId"] = "Dni", d
        else:
            notes.append(clean(o))
    c["_notes"] = notes
    c["_src"] = v["src"]
    return c


recs = [process(v) for v in rows]

# ---- dedupe (union-find on normalized name or phone)
parent = list(range(len(recs)))
evid = {}


def find(i):
    while parent[i] != i:
        parent[i] = parent[parent[i]]
        i = parent[i]
    return i


def union(a, b, why):
    ra, rb = find(a), find(b)
    if ra != rb:
        parent[rb] = ra
    evid[frozenset((a, b))] = why


byname, byphone = {}, {}
for i, r in enumerate(recs):
    k = nkey(r["displayName"])
    if k in byname:
        union(byname[k], i, "mismo nombre")
    else:
        byname[k] = i
    if r["phone"]:
        if r["phone"] in byphone:
            union(byphone[r["phone"]], i, "mismo teléfono")
        else:
            byphone[r["phone"]] = i
groups = {}
for i in range(len(recs)):
    groups.setdefault(find(i), []).append(i)


def nonascii(s):
    return sum(1 for ch in s if ord(ch) > 127)


customers = []
merges = []
for g in groups.values():
    mem = [recs[i] for i in g]
    mem.sort(key=lambda r: r["taxId"] is None)  # stable: tax-id row first
    m = OrderedDict(mem[0])
    for r in mem[1:]:
        for f in ("contactName", "phone", "cityKey", "businessTypeKey"):
            if not m[f] and r[f]:
                m[f] = r[f]
            elif m[f] and r[f] and m[f] != r[f]:
                review.append(("Conflicto al unificar", f"{m['displayName']}: {f} «{m[f]}» ({m['_src']}) vs «{r[f]}» ({r['_src']}); se conservó el primero."))
        if r["addressStreet"] and (not m["addressStreet"] or len(r["addressStreet"]) > len(m["addressStreet"])):
            m["addressStreet"] = r["addressStreet"]
        if m["taxId"] is None and r["taxId"]:
            m["taxId"], m["taxIdType"] = r["taxId"], r["taxIdType"]
        if nonascii(r["displayName"]) > nonascii(m["displayName"]):
            m["displayName"] = r["displayName"]
        m["_notes"] = list(dict.fromkeys(m["_notes"] + r["_notes"]))
    srcs = sorted((r["_src"] for r in mem), key=lambda s: (s.split("!")[0], int(s.rsplit(" ", 1)[1])))
    if len(mem) > 1:
        why = sorted({evid[frozenset((a, b))] for a in g for b in g if frozenset((a, b)) in evid})
        merges.append((m["displayName"], srcs, " + ".join(why), m["taxId"] is not None))
    cust = OrderedDict(displayName=m["displayName"], contactName=m["contactName"], addressStreet=m["addressStreet"],
                       phone=m["phone"], cityKey=m["cityKey"], businessTypeKey=m["businessTypeKey"],
                       taxIdType=m["taxIdType"], taxId=m["taxId"], notes="; ".join(m["_notes"]) or None,
                       customerKind="Wholesale", sources=srcs)
    customers.append(cust)

bts = [OrderedDict(key=nkey(n).replace(" ", "_"), name=n, sortOrder=i)
       for i, n in enumerate(sorted(set(BT.values()), key=nkey), 1)]
BTKEY = {b["name"]: b["key"] for b in bts}
for c in customers:
    c["businessTypeKey"] = BTKEY.get(c["businessTypeKey"])
customers.sort(key=lambda c: (nkey(c["displayName"]), c["displayName"]))

o = maxsort
for k in sorted(used_new_cities):
    o += 1
    cities.append(OrderedDict(id=None, key=k, name=NEW_CITIES[k], isActive=True, sortOrder=o,
                              createdAt=None, updatedAt=None, new=True))

# ---- review items
names = {nkey(c["displayName"]) for c in customers}


def has(n):
    return nkey(n) in names


for t in flag_ruta9:
    review.append(("Localidad «Ruta 9»", f"{t}: no es una ciudad; sin ciudad, «Ruta 9» agregado a la dirección. Recomendación: asignar Baradero si el dueño lo confirma."))
if has("Los Abraham") and has("Los Abraham Luis"):
    review.append(("Posible duplicado", "«Los Abraham» (San Pedro, tel. 3329625588, contacto Luis) vs «Los Abraham Luis» (Río Tala, sin teléfono). El contacto de San Pedro es Luis pero la localidad difiere: se dejaron separados. Recomendación: confirmar con el dueño; probablemente sea el mismo cliente."))
if has("Coti") and has("Cliente Coti"):
    review.append(("Posible duplicado", "«Coti» (Salto, Arredondo 167, tel. 2474683008) vs «Cliente Coti» (Capitán Sarmiento, sin teléfono, Carnicería): ciudades distintas, se dejaron separados. Recomendación: mantener separados salvo que el dueño diga lo contrario."))
if has("Parrilla Javier"):
    review.append(("Posible duplicado", "«Parrilla Javier» (Baradero, Ruta 9, tel. 3329627731, contacto Javier Bargas) vs «Javier» (unificado: Baradero + Ruta 9, sin teléfono) vs «La Picasa» (Baradero, contacto Javier, tel. 3329691185). Los dos «Javier» se unificaron por nombre idéntico. Recomendación: probablemente «Javier» = «Parrilla Javier» (misma ruta); confirmar y unir. «La Picasa» es otro local."))
review.append(("Nombre con posible error de tipeo", "«Miuñoz-Pipi» (Arrecifes): ¿será «Muñoz-Pipi»? Se dejó como está."))
review.append(("Nombre abreviado", "«Facundo D Valle»: ¿será «Facundo del Valle»? Se dejó como está."))
review.append(("Registros de «LISTA CLIENTES COTO»", "«Willie Carnicería» tenía ESPECIFICACIONES «Súper Campeones» (rubro Carnicería por el nombre; original en notas) y dirección «(Fonavi/El Campeon)»; «Baratini» tenía «CARNIC \"LA MARCA\"» (Carnicería; original en notas); «Carnicería» (contacto Joaquín) es solo un rubro como nombre: pedir el nombre real. Lo mismo con «Bodegón» (Capitán Sarmiento, contacto Gastón, rubro Resto Bar)."))
review.append(("Clientes sin ciudad", f"{sum(1 for c in customers if not c['cityKey'])} clientes sin localidad (sobre todo los individuos con CUIT/DNI): completar a mano."))
review.append(("Clientes sin rubro", f"{sum(1 for c in customers if not c['businessTypeKey'])} clientes sin rubro (ESPECIFICACIONES vacía y no inferible del nombre)."))
review.append(("Tildes en nombres propios", "No se agregaron tildes a apellidos sin evidencia (p. ej. Nestor Bilbao, Baez, Condor). Se restauraron tildes solo en rubros (Almacén, Carnicería, Bodegón...) y ciudades."))

decisions = [
    "La ciudad de referencia «Sarmiento» es «Capitán Sarmiento» (decisión del dueño): se renombró a «Capitán Sarmiento» (clave `capitan_sarmiento`) conservando su id, orden, estado y fechas de creación/actualización originales. «SARMIENTO» y «CAPITÁN SARMIENTO» de las planillas apuntan a ella.",
]
assumptions += [
    "Los CUIT/CUIL con formato NN-NNNNNNNN-N se cargaron todos como «Cuit» (la planilla no distingue CUIL; los prefijos 20/23/27 son de personas).",
    "DNI = números de 7-8 dígitos (con puntos o flotante). Cualquier otro texto de OBSERVACIONES iría a notas (no hubo casos).",
    "Mayúsculas: solo se re-capitalizan textos completamente en mayúsculas; los ya en formato mixto (PyP, Shell, nombres de personas) se respetan. «de», «del», «y» en minúscula si no son la primera palabra; «El/La/Los» quedan capitalizados en nombres comerciales.",
    "Teléfonos: solo dígitos con característica, sin agregar 0/15.",
    "«Autoservicio Arrecifes» = «AUT SERVICIO ARR» (CLIENTES) = «AUTOSERVICIO ARREC» (CLIENTES1): mismo teléfono 2478520672.",
    "Las ciudades «San Nicolás» y «Río Tala» se agregan al catálogo (sort_order a continuación del máximo, 24 y 25).",
    "Tipo de cliente: todos «Wholesale».",
    "Rubros compuestos: el primero es el rubro; los demás van a notas como «También: ...». «Bodegón» queda en el catálogo aunque hoy solo figura como rubro secundario.",
    "Se ignoró CLIENTES.xltx (instrucción del dueño).",
    "Al unificar se prefiere la fila con CUIT/DNI, el nombre con más tildes y la dirección más larga.",
]

with open(os.path.join(HERE, "cities.json"), "w", encoding="utf-8") as f:
    json.dump(cities, f, ensure_ascii=False, indent=2)
with open(os.path.join(HERE, "business_types.json"), "w", encoding="utf-8") as f:
    json.dump(bts, f, ensure_ascii=False, indent=2)
with open(os.path.join(HERE, "customers.json"), "w", encoding="utf-8") as f:
    json.dump(customers, f, ensure_ascii=False, indent=2)

seen = set()
rev = []
for t, x in review:
    if (t, x) not in seen:
        seen.add((t, x))
        rev.append((t, x))
L = ["# Reporte de normalización de clientes Vaca Verde\n", "## Conteos\n"]
L.append(f"- Filas con contenido leídas (ambos archivos, sin filas totalmente vacías): **{stats['raw']}**")
L.append(f"- Descartadas: **{len(dropped)}** (títulos {stats['titles']}, encabezados repetidos {stats['headers']}, fórmula =AI(...) {stats['garbage']}, sin nombre {stats['empty']})")
L.append(f"- Filas de cliente candidatas: **{len(rows)}**")
L.append(f"- Absorbidas por unificación: **{len(rows) - len(customers)}** en {len(merges)} grupos")
L.append(f"- Clientes finales: **{len(customers)}**")
L.append(f"- Ciudades: {len(cities)} ({sum(c['new'] for c in cities)} nuevas); rubros: {len(bts)}")
L.append(f"- Con CUIT/DNI: {sum(1 for c in customers if c['taxId'])}; con ciudad: {sum(1 for c in customers if c['cityKey'])}; con rubro: {sum(1 for c in customers if c['businessTypeKey'])}; con teléfono: {sum(1 for c in customers if c['phone'])}\n")
L.append("## Filas descartadas\n")
L += [f"- {s}: {w}" for s, w in dropped]
L.append("\n## Unificaciones (evidencia)\n")
for n, s, w, t in sorted(merges, key=lambda m: nkey(m[0])):
    L.append(f"- **{n}** ← {len(s)} filas ({w}{'; se conservó el dato de la fila con CUIT/DNI' if t else ''}): " + "; ".join(s))
L.append("\n## Decisiones del dueño\n")
L += [f"- {d}" for d in decisions]
L.append("\n## Supuestos\n")
L += [f"- {a}" for a in assumptions]
L.append("\n## Para revisar\n")
L += [f"- **{t}.** {x}" for t, x in rev]
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "report.md"), "w", encoding="utf-8") as f:
    f.write("\n".join(L) + "\n")
print(len(rows), len(customers), len(merges), len(dropped))
