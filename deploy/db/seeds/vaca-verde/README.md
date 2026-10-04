# Vaca Verde master data seed

Versioned, idempotent SQL that loads Vaca Verde's business types, customers and
customer contacts into an organization named "Vaca Verde". Cities are not part of it any more:
they are global Georef data (migration `0028`) and customers point at them.

> **Contains real customer data** (names, phones, addresses, CUIT/DNI). Keeping
> it in the repository is an explicit owner decision, so the same data travels
> local -> dev -> prod. Do not copy it into other organizations or public forks.

## Why a seed and not a migration

Organization ids are generated at runtime, so a migration that runs before the
organization exists would be a permanent no-op. The seed resolves the
organization **by name** (`lower(btrim(name)) = 'vaca verde'`) and the creating
user from that organization's `business-admin` (`users.roles` contains it, not
revoked, not a customer login). If either is missing it prints a `NOTICE` and
changes nothing (no error). Schema lives in migrations `0027` (customer master
data) and `0028` (core geography) and `0029` (customer contacts) and `0035` (organization number format), which must be applied first.

## Contents

| Table | Rows | Notes |
| --- | --- | --- |
| `cities` | 0 created | Customers reference the global Georef locality by INDEC id (e.g. Capitán Sarmiento `06140010`, Río Tala `06770040`, "Capital Federal" -> Ciudad de Buenos Aires `02014010`, San Nicolás -> San Nicolás de los Arroyos `06763050`; the table is `CITY_INDEC` in `generate_seed.py`). Doyle and Urquiza have no unambiguous locality, so no customer points at them. The seed also restores the owner's original `created_at_utc`/`updated_at_utc` of those 23 mapped cities on the global rows, but only while a row still carries its load time (never over a later edit). |
| `customer_contacts` | 39 | The CLIENTE column as the customer's **primary contact** (deterministic id). The name is kept whole in `first_name` and never split: one column cannot tell "Lucas Badano" (first + last) from "Juan Ignacio" (a compound first name), so last names are completed in the app. |
| `organizations` | 1 updated | `quantity_decimal_separator` = `Dot` (Vaca Verde writes kilos "1.5"; migration `0035`), only while it is still the `Comma` default and no `organization.settings_updated` audit row exists, so a later choice made in the app is never overwritten. |
| `business_types` | 11 | Deterministic ids. |
| `customers` | 87 | Wholesale, enabled, deduplicated; tax id split out of observations (`Cuit`/`Dni`/`None`); other observations in `notes`. `party_type` is `Company` for a CUIT, otherwise `Person` (needs migration `0040`; a fresh environment applies the migrations before the seed, so the seed stores it itself). |

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
- Every row has a deterministic id (`md5('vaca-verde:<kind>:<key>')::uuid`) and is inserted with
  `ON CONFLICT DO NOTHING`, which also covers the unique name and key indexes.
- Re-running adds nothing and **never overwrites** edits made later in the app (including the number format).
- Without the organization or its business admin: a `NOTICE`, no changes.

## Suppliers, catalog and customer price lists (`002`, `003`, `004`)

Applied in file-name order after `001` (`deploy/dev/provision-admin.ps1` applies every `*.sql` here, sorted).
Schema: suppliers `0030`, catalog `0009`/`0016`/`0018`, pricing `0009`/`0013`/`0014`/`0017`.
Both resolve the organization by name and the creating user from its business admin; `003` also resolves the
branch named **"Ruta 51"** (the only branch of Vaca Verde: butcher shop and distribution) and sets
`app.current_branch_id`. Any missing piece prints a `NOTICE` and changes nothing.

| File | Rows | Notes |
| --- | --- | --- |
| `002_vaca_verde_suppliers.sql` | 1 category, 9 suppliers, 8 contacts | Supplier category "Carne". The two "Villarino ... FRIMSA" rows are one supplier, "Frimsa", with two contacts. Cities are Georef rows by INDEC id (Rosario `82084270`, Pilar `06638040`, Munro `0686101005`, CABA `02014010`, Merlo `06539010`, San Pedro `06770050`). Contact names are split only when the cell clearly holds first + last name. |
| `003_vaca_verde_catalog.sql` | 8 categories, 90 products, 90 presentations, 2 price lists, 99 entries, 1 rate set | Categories Vacuno, Cerdo, Embutidos, Pollo, Achuras, Milanesas, Almacén, Bebidas (the last two empty). Each product has one Weighted "Por kg" presentation (unit = `md5('vaca-verde:unit:kg')::uuid`, there is no units table) with a **provisional** code `001`... ordered by category, then name; scale integration replaces them later. |

Price lists of "Ruta 51" as `003` loads them, all effective from 2026-10-01:

- **Mostrador**: the default list; final retail prices (Vacuna, Cerdo, Pollo, Achuras "Precio con aumento"). `004` turns them into base prices.
- **Reparto**: base prices (PRECIO BASE) with a **list-specific** rate component set (IVA 10.5 %, IB 2.5 %, Flete 7 %,
  Remarcación 25 %, all on the base: x 1.45, e.g. Asado completo 10.600 -> 15.370). It is never the organization
  default set.
- The sheet "Lista Clientes" is still read for product names and owner notes but is **no longer a price list**: customers are priced from Reparto.

`004_vaca_verde_customer_price_lists.sql` (hand-maintained, not generated; needs migration `0037`) derives everything from what `003`
loaded and is safe on a fresh environment and on one where an older `003` already ran:

| Step | Effect | Guard |
| --- | --- | --- |
| Mostrador base prices | New entries effective **2026-10-02** (the 10-01 finals stay as history): Reparto's base where Reparto sells the product (11 cuts, Bola de lomo 11.400), else `round(final / 1.48, 2)` (63 cuts, Lengua 7.817,57) | only while Mostrador has no rate set of its own and its current entry is the seeded one |
| Mostrador rate set | List-specific, effective 2026-10-02: IVA 10.5 % + IB 2.5 % + Remarcación 35 % on the base, no flete (x 1.48: Bola de lomo 16.872, Reparto 16.530) | same |
| Floor | Mostrador's `floor_price_list_id` = Reparto | same run, only while unset |
| Organization default customer list | Reparto | only while unset and no `organization.settings_updated` audit row |
| Customers | every customer without a list gets Reparto | only `price_list_id IS NULL` |
| Clientes | the list and its seeded entries are deleted | only when no customer, default, floor or rate set references it and every entry is a 2026-10-01 one |

If the branch already has an untouched empty list named "Default" (what provisioning creates), it is renamed to
Mostrador. If it has a different default list, Mostrador is created as a non-default list (a `NOTICE` says so).
Categories are resolved by name and lists by name, so manual rows with the same name are reused, not duplicated.

Regenerate (Python 3 + `openpyxl`, spreadsheet paths are arguments, nothing is hardcoded):

```sh
python generate_suppliers_catalog.py --proveedores "Proveedores Lista.xlsx" --achuras "Achuras.xlsx" \
    --precios "Precios Vaca Verde Reparto con Porcentajes.xlsx" --vacuno-cerdo-pollo "Vacuno Cerdo Pollo.xlsx"
```

It rewrites `002`, `003` and `report-catalogo.md` (never `004`) (Spanish: counts, merges, renames and the "Para revisar" list for
the owner). Never edit those files by hand.
