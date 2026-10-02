# Vaca Verde master data seed

Versioned, idempotent SQL that loads Vaca Verde's cities, business types and
customers into an organization named "Vaca Verde".

> **Contains real customer data** (names, phones, addresses, CUIT/DNI). Keeping
> it in the repository is an explicit owner decision, so the same data travels
> local -> dev -> prod. Do not copy it into other organizations or public forks.

## Why a seed and not a migration

Organization ids are generated at runtime, so a migration that runs before the
organization exists would be a permanent no-op. The seed resolves the
organization **by name** (`lower(btrim(name)) = 'vaca verde'`) and the creating
user from that organization's `business-admin` (`users.roles` contains it, not
revoked, not a customer login). If either is missing it prints a `NOTICE` and
changes nothing (no error). Schema lives in migration `0027`.

## Contents

| Table | Rows | Notes |
| --- | --- | --- |
| `cities` | 25 | 23 reference cities keep their original id, `sort_order`, `is_active` and `created_at_utc`/`updated_at_utc`; 2 new (Río Tala, San Nicolás). The reference city "Sarmiento" is renamed **Capitán Sarmiento** (key `capitan_sarmiento`, owner decision), everything else unchanged. |
| `business_types` | 11 | Deterministic ids. |
| `customers` | 87 | Wholesale, enabled, deduplicated; tax id split out of observations (`Cuit`/`Dni`/`None`); other observations in `notes`. |

## Sources

Outside the repository, supplied by the owner: `CLIENTES.xlsx` (sheet
"Clientes Vaca Verde"), `CLIENTES1.xlsx` (sheet "Hoja1") and `cities_rows.csv`
(reference cities). `CLIENTES.xltx` is ignored (incomplete copy). The decisions,
discarded rows, merges and open questions for the owner are in `report.md`
(Spanish).

## Regenerate

Requires Python 3 and `openpyxl`. Source paths are arguments:

```sh
python normalize.py --clientes CLIENTES.xlsx --clientes1 CLIENTES1.xlsx \
    --cities-csv cities_rows.csv --out-dir build
python generate_seed.py --data-dir build
```

`normalize.py` writes the intermediate JSON into `build/` (git-ignored) and
refreshes `report.md`; `generate_seed.py` rewrites
`001_vaca_verde_master_data.sql` deterministically. Never edit the SQL by hand.

## Apply

The organization must already be provisioned (with its business admin). Run as
the database owner, with `ON_ERROR_STOP`:

```sh
# local: done automatically by deploy/dev/provision-admin.ps1; manually:
docker compose -f deploy/dev/compose.yaml exec -T postgres \
    psql -v ON_ERROR_STOP=1 -U commerce_owner -d commerce_dev -f - < deploy/db/seeds/vaca-verde/001_vaca_verde_master_data.sql

# dev / prod (after the organization exists)
psql "$OWNER_DATABASE_URL" -v ON_ERROR_STOP=1 -f deploy/db/seeds/vaca-verde/001_vaca_verde_master_data.sql
```

If the owner role is subject to row level security, scope the session first:
`PGOPTIONS="-c app.current_org_id=<organization uuid>"`. The file always sets
`app.current_org_id` (transaction-local) once the organization is resolved.

## Idempotency guarantees

- One transaction: all or nothing.
- Every row has a deterministic id (reference cities keep theirs; the rest use
  `md5('vaca-verde:<kind>:<key>')::uuid`) and is inserted with
  `ON CONFLICT DO NOTHING`, which also covers the unique name and key indexes.
- Re-running adds nothing and **never overwrites** edits made later in the app.
- Without the organization or its business admin: a `NOTICE`, no changes.
