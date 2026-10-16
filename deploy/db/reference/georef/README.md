# Georef reference data (countries, provinces, cities)

Source of the geography loaded by migration
`deploy/db/migrations/0028_core_geography.sql`: the official Argentine
**Georef** service of datos.gob.ar.

| | |
| --- | --- |
| Provinces | `https://apis.datos.gob.ar/georef/api/provincias?campos=id,nombre,iso_id&max=30` (24 rows: INDEC id `06`, ISO 3166-2 `AR-B`) |
| Localities | `https://apis.datos.gob.ar/georef/api/localidades?max=5000&campos=id,nombre,provincia.id,departamento.nombre` (4037 rows; `max` goes up to 5000, so one request holds them all; the generator still pages with `inicio` if that ever changes) |
| Fetched | 2026-10-02 |
| Data licence | Creative Commons Attribution 4.0 (CC BY 4.0), as declared by the dataset "Servicio de normalizacion de direcciones y unidades territoriales de Argentina" in the datos.gob.ar catalogue (`https://datos.gob.ar/dataset/servicio-de-normalizacion-de-direcciones-y-unidades-territoriales-de-argentina`, CKAN field `license_id`). Attribution: *Direccion Nacional de Datos e Informacion Publica, Secretaria de Innovacion Publica / datos.gob.ar, Georef; base data from INDEC and IGN.* |
| API software licence | MIT (`datosgobar/georef-ar-api`), not relevant to the data. |

The licence was read from the catalogue metadata on the fetch date; the
datos.gob.ar terms page was not re-checked beyond that, so treat the exact
attribution wording as **unverified** if it matters for publication.

## What the data looks like

- `provincias.id` is the INDEC province code (`"06"`); `iso_id` is the ISO
  3166-2 code. Ciudad Autonoma de Buenos Aires is province `02` (`AR-C`).
- `localidades.id` is either an 8-digit census locality (`06140010`, Capitan
  Sarmiento) or a 10-digit sub-locality / barrio (`0208401004`, Villa Urquiza in
  CABA). The source repeats a name inside one department when a locality has a
  sub-locality (`06441030` and `0644103015` are both "La Plata"), so names are
  **not** unique; the INDEC id is.
- There is no row called "Capital Federal": the representative locality of the
  city is `02014010` "Ciudad de Buenos Aires".

## How it ends up in the database

`generate.py` rewrites the block between the two `GENERATED GEOREF DATA`
markers of the migration (nothing else in the file). Rows are sorted by INDEC id
and every locality gets a deterministic UUIDv5 (`georef-localidad:<indec id>`),
so every environment has the same primary keys and the output is byte-stable for
the same API data. The inserts are `ON CONFLICT DO NOTHING`: re-running never
overwrites an edit a system administrator made.

## Refreshing

Georef changes rarely. To pick up changes, do **not** edit 0028 once it has been
released: generate the new rows into a **new** migration (copy the generated
block, keep `ON CONFLICT (indec_id) DO UPDATE` only for the columns that should
follow the source). For an unreleased 0028:

```sh
python deploy/db/reference/georef/generate.py                  # fetches the API, rewrites the block
python deploy/db/reference/georef/generate.py --save-dir cache # also keeps the raw JSON
python deploy/db/reference/georef/generate.py --input-dir cache --fetched 2026-10-02   # offline
```

Then mirror the migration into `deploy/dev/db/init-rls.sql` (the repository
convention; `CoreGeographyMigrationTests` asserts it is verbatim) and re-run
`dotnet test`.
