# Customer Master Data and Vaca Verde Seed

## Objective

Populate the system with Vaca Verde's real customer list so the owner can see
how the product behaves with real data. Add Cities and Business Types catalogs
with full CRUD (ABM), extend Customer with the fields the source lists carry,
and ship the data as a versioned, idempotent seed that travels local -> dev ->
prod.

## Why

The owner keeps the customer list in spreadsheets with duplicates, mixed
casing, tax ids buried in an "observations" column and free-text localities.
The system has no seed data at all, so the UI cannot be evaluated with real
content.

## Sources (outside the repo)

- `CLIENTES.xlsx`, sheet "Clientes Vaca Verde": two blocks (main list and
  "LISTA CLIENTES COTO"), columns NOMBRE, DIRECCION, TELEFONO, LOCALIDAD,
  CLIENTE (contact person), ESPECIFICACIONES (business type), OBSERVACIONES
  (CUIT/DNI). The block of people repeats without tax ids (duplicates).
- `CLIENTES1.xlsx`, Hoja1: NOMBRE, DIRECCION, TELEFONO, LOCALIDAD; overlaps
  the main list.
- `CLIENTES.xltx`: ignored (owner: incomplete copy of CLIENTES.xlsx).
- `cities_rows.csv`: 23 reference cities with id, name, is_active,
  sort_order, created_at, updated_at, key. Audit dates must be preserved.

## Decisions

- Owner: the seed is versioned in the repo (contains real personal data, the
  owner accepted this) so it goes through every environment.
- Seed shape: organization ids are generated at runtime, so a schema migration
  that runs before the organization exists would be a permanent no-op. The
  seed is therefore a versioned, idempotent SQL file under
  `deploy/db/seeds/vaca-verde/` that resolves the organization by name and
  the creating user from that organization's business admin, applied after
  provisioning (locally from `deploy/dev/provision-admin.ps1`; in dev/prod
  via the documented runbook). Schema changes stay in `0027`.
- Cities and Business Types are organization-scoped catalogs, modelled on
  Categories (migration 0018 pattern, composite FK from customers).
- Customer gains `city_id`, `business_type_id`, `contact_name`; tax id type
  gains `Dni`; observations use the existing `notes` field.
- Customer `locality` free text is kept for compatibility; the city reference
  is the source of truth going forward.

## Tasks

- [x] T1 Migration 0027 (cities, business_types, customer columns, Dni) + init-rls parity + migration tests (route: delegated writer) - commit cfcf210; RED: 9/9 failed (relation missing), GREEN: 9/9 passed; applied to local `commerce_dev`
- [x] T2 Cloud.Api: Cities and Business Types CRUD endpoints/stores with audit; Customer endpoints/store/domain carry the new fields (route: delegated writer) - commit a8f8575; RED: compile failure (missing types) then 1 behavioral failure (enums serialized as numbers), GREEN: 179/179 customer tests; full `dotnet test`: 1717 passed, 1 failed (known launcher/wwwroot environmental failure), 0 skipped
- [ ] T3 Normalize the spreadsheets: dedupe, Title Case, split tax ids out of observations, map localities and business types; anomaly report for owner review (route: delegated, data only)
- [ ] T4 Seed SQL `deploy/db/seeds/vaca-verde/` (cities with original audit dates, business types, customers), idempotent; wired into provision-admin; applied to local `commerce_dev` (route: delegated writer)
- [x] T5 Web: Cities and Business Types ABM screens; customer form and list with city, business type, contact, tax id, observations, and filters by city and business type (route: delegated writer) - commits 48970af (ABM screens, nav, e2e nav checks) and 9176692 (customer form/list); RED: 2 new test files failing on import + nav tests, then 10 customer tests failing; GREEN: `npm test` 58 files / 389 tests passed, `npm run build` ok, `npm run lint` 0 errors (pre-existing warnings only); e2e specs updated by hand, not run (CI-only)

## Acceptance criteria

- Running the seed twice leaves the same data (idempotent).
- Local admin for Vaca Verde sees ~100 deduplicated customers, sorted, with
  city, business type, contact, tax id and observations in separate fields.
- Cities keep the CSV created/updated timestamps.
- Cities and Business Types can be created, edited and disabled from the web.
- `dotnet test`, `npm test`, `npm run lint`, `npm run build` pass.

## Constraints

- TDD: Strict (RED -> GREEN -> REFACTOR), source: global config. Runners:
  `dotnet test` (xUnit, integration tests against local Postgres
  `commerce_test`), `npm test` (Vitest) in `src/Commerce.Web`.
- Migrations forward-only, idempotent, appended to `deploy/dev/db/init-rls.sql`
  (parity test). RLS forced, `app_runtime` grants, org-scoped policy.
- POS (WPF) customer replica must keep working; new fields are optional.
- Commit to `dev`, Conventional Commits, no AI attribution.

## Progress

- Feature document created 2026-10-02.
- T1 and T2 done (see task lines). API: `/customers/cities`, `/customers/business-types` (GET `?includeInactive`, POST, PUT /{id}); `GET /customers?search&cityId&businessTypeId`; customer JSON now carries cityId, cityName, businessTypeId, businessTypeName, contactName, and its enums as strings.

## Next step

T1 + T2 (backend writer) and T3 (data normalization) in parallel.
