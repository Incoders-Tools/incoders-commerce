# -*- coding: utf-8 -*-
"""Regenerate the Georef data block of 0028_core_geography.sql.

Fetches the Argentine provinces and localities from the official Georef API
(datos.gob.ar) and rewrites the text between the two GENERATED markers of the
migration, in place. Everything else in the migration is hand-written.

Usage:
    python generate.py                      # fetch from the API, rewrite the migration
    python generate.py --input-dir cache    # read provincias.json / localidades.json instead
    python generate.py --save-dir cache     # also keep the raw API responses

The output is deterministic: rows are sorted by INDEC id, ids of the localities
are UUIDv5 values derived from the INDEC id (so every environment ends up with
the same primary keys), and the SQL is plain UTF-8 with LF line endings. The
same API data always yields the same bytes (apart from the --fetched date that
is printed in the block's header comment).

See README.md in this directory for the source, licence and refresh procedure.
"""
import argparse
import datetime
import json
import os
import sys
import urllib.parse
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_MIGRATION = os.path.normpath(
    os.path.join(HERE, "..", "..", "migrations", "0028_core_geography.sql"))

API = "https://apis.datos.gob.ar/georef/api"
PAGE = 5000  # the API's documented maximum for `max`
NAMESPACE = uuid.UUID("5d0b4d0e-6a0b-4f44-9a39-7a3c1b6f2e11")  # fixed: ids must never change

BEGIN = "-- >>> BEGIN GENERATED GEOREF DATA (deploy/db/reference/georef/generate.py) - do not edit by hand >>>"
END = "-- <<< END GENERATED GEOREF DATA <<<"
BATCH = 400


def get_json(path, params):
    url = f"{API}/{path}?{urllib.parse.urlencode(params, safe=',.')}"
    with urllib.request.urlopen(url, timeout=60) as response:
        return json.loads(response.read().decode("utf-8"))


def fetch_all(path, key, params):
    """Page through a Georef listing until `total` rows are collected."""
    rows, start = [], 0
    while True:
        page = get_json(path, {**params, "max": PAGE, "inicio": start})
        rows.extend(page[key])
        start += page["cantidad"]
        if start >= page["total"] or page["cantidad"] == 0:
            break
    if len(rows) != page["total"]:
        sys.exit(f"{path}: expected {page['total']} rows, got {len(rows)}")
    return rows


def q(v):
    """SQL literal: NULL or a single-quoted string (standard_conforming_strings is on)."""
    return "NULL" if v is None else "'" + str(v).replace("'", "''") + "'"


def clean(v):
    v = (v or "").strip()
    return v or None


def build_block(provinces, localities, fetched):
    provinces = sorted(provinces, key=lambda p: p["id"])
    localities = sorted(localities, key=lambda l: (l["provincia"]["id"], l["id"]))
    province_ids = {p["id"] for p in provinces}
    ids = [l["id"] for l in localities]
    if len(set(ids)) != len(ids):
        sys.exit("duplicate locality ids in the source")
    for l in localities:
        if l["provincia"]["id"] not in province_ids:
            sys.exit(f"locality {l['id']} references an unknown province")

    out = [BEGIN,
           f"-- Source: {API}/provincias and /localidades (datos.gob.ar, Georef; CC BY 4.0, see README.md).",
           f"-- Fetched {fetched}: {len(provinces)} provinces, {len(localities)} localities.",
           "",
           "INSERT INTO countries (code, iso3, name) VALUES ('AR', 'ARG', 'Argentina')",
           "ON CONFLICT (code) DO NOTHING;",
           "",
           "INSERT INTO provinces (id, country_code, iso_code, name) VALUES"]
    out.append(",\n".join(
        f"    ({q(p['id'])}, 'AR', {q(p['iso_id'])}, {q(clean(p['nombre']))})" for p in provinces))
    out.append("ON CONFLICT (id) DO NOTHING;")

    for i in range(0, len(localities), BATCH):
        out.append("")
        out.append("INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES")
        out.append(",\n".join(
            "    ({}, {}, {}, {}, {})".format(
                q(uuid.uuid5(NAMESPACE, "georef-localidad:" + l["id"])), q(l["id"]), q(clean(l["nombre"])),
                q(l["provincia"]["id"]), q(clean((l.get("departamento") or {}).get("nombre"))))
            for l in localities[i:i + BATCH]))
        out.append("ON CONFLICT (indec_id) DO NOTHING;")
    out.append(END)
    return "\n".join(out)


def load(args):
    if args.input_dir:
        with open(os.path.join(args.input_dir, "provincias.json"), encoding="utf-8") as f:
            provinces = json.load(f)["provincias"]
        with open(os.path.join(args.input_dir, "localidades.json"), encoding="utf-8") as f:
            localities = json.load(f)["localidades"]
        return provinces, localities
    provinces = fetch_all("provincias", "provincias", {"campos": "id,nombre,iso_id"})
    localities = fetch_all("localidades", "localidades", {"campos": "id,nombre,provincia.id,departamento.nombre"})
    if args.save_dir:
        os.makedirs(args.save_dir, exist_ok=True)
        for name, key, rows in (("provincias", "provincias", provinces), ("localidades", "localidades", localities)):
            with open(os.path.join(args.save_dir, name + ".json"), "w", encoding="utf-8", newline="\n") as f:
                json.dump({key: rows}, f, ensure_ascii=False)
    return provinces, localities


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--migration", default=DEFAULT_MIGRATION, help="migration file whose generated block is replaced")
    ap.add_argument("--input-dir", help="read provincias.json and localidades.json from here instead of the API")
    ap.add_argument("--save-dir", help="write the raw API responses here")
    ap.add_argument("--fetched", default=datetime.date.today().isoformat(), help="fetch date printed in the block header")
    args = ap.parse_args()

    provinces, localities = load(args)
    block = build_block(provinces, localities, args.fetched)

    with open(args.migration, encoding="utf-8", newline="") as f:
        text = f.read().replace("\r\n", "\n")
    start, end = text.find(BEGIN), text.find(END)
    if start < 0 or end < 0 or end < start:
        sys.exit("generated-block markers not found in " + args.migration)
    text = text[:start] + block + text[end + len(END):]
    with open(args.migration, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"wrote {args.migration}: {len(provinces)} provinces, {len(localities)} localities")


if __name__ == "__main__":
    main()
