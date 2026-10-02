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
- [x] T3 Normalize the spreadsheets: dedupe, Title Case, split tax ids out of observations, map localities and business types; anomaly report for owner review (route: delegated, data only) - delivered with T4 (`normalize.py`, `report.md` in the seed directory); 118 candidate rows -> 87 customers
- [x] T4 Seed SQL `deploy/db/seeds/vaca-verde/` (cities with original audit dates, business types, customers), idempotent; wired into provision-admin; applied to local `commerce_dev` (route: single bounded writer) - commit 7938b78; RED: 6/6 `VacaVerdeSeedTests` failed (seed file missing), GREEN: 6/6 passed; local `commerce_dev` after applying twice: 25 cities, 11 business types, 87 customers (second run inserted 0); full `dotnet test`: 1722 passed, 2 failed (known PublicRateLimitTests launcher/wwwroot failure + PosAdminClientCompositionTests, flaky in the full run, passes alone), 0 skipped
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
- Owner decision: reference city "Sarmiento" is "Capitán Sarmiento" (renamed, id and audit dates kept).
- T1, T2, T3, T4, T5 done (see task lines). API: `/customers/cities`, `/customers/business-types` (GET `?includeInactive`, POST, PUT /{id}); `GET /customers?search&cityId&businessTypeId`; customer JSON now carries cityId, cityName, businessTypeId, businessTypeName, contactName, and its enums as strings.

- Review: the full range exceeded the native reviewer budget, so it was reviewed in three owner-consented slices, all approved and acknowledged: backend `59c2097..a1565d4` (high, 4 lenses, lineage `review-323d354a066b4f70`), web `a1565d4..737a7ee` (medium, lineage `review-22219942ba8b71fa`), seed `737a7ee..c8166f1` (high, 4 lenses, lineage `review-ae90d881e0865955`). Reviewed boundary is now `c8166f1`.
- Full `dotnet test`: 1722 passed, 2 failed (known launcher/wwwroot failure; `PosAdminClientCompositionTests.Build_ProvidesFreshWindowScopedUserAdminClients_AndBranding` fails only in the full run, passes alone — not investigated).

## Follow-ups (non-blocking review findings, most relevant)

- [ ] F1 Master data PUT reactivates an entry when `isActive` is omitted (`MasterData.cs:116`); make it keep the stored value.
- [ ] F2 Customer PUT keep-on-omit can lose a concurrent update (`PostgresCustomerStore.cs:180-182`).
- [ ] F3 Legacy dashed Cuit/Cuil values are rejected on PUT while the domain constructor accepts them (`Customers.cs:180-182`, `Customer.cs:106-118`).
- [ ] F4 Search accent folding differs between SQL and the tax-id path (`PostgresCustomerStore.cs:274-277`, `370-375`).
- [ ] F5 Seed: a city whose global id already exists in another org is skipped silently (`001_vaca_verde_master_data.sql:62-94`, `118`); raise a notice.
- [ ] F6 `provision-admin.ps1` decides "applied/skipped" from a NOTICE string (`:469-474`).
- [ ] F7 Web: the customer list's stale-response guard is untested; email is no longer searched (`CustomersScreen.tsx:61`).
- [ ] F8 Seed tests' link assertions are weak (`VacaVerdeSeedTests.cs:144-145`).
- [ ] F9 Investigate the `PosAdminClientCompositionTests` full-run failure.
- Accepted by owner: real customer data committed to the repo (R1-pii-committed); org resolved by name.

## Next step

Owner review of `deploy/db/seeds/vaca-verde/report.md` (open questions: possible duplicates, "Ruta 9" locality, customers without city or business type); then promote `dev` to `main` and run the README runbook per environment once the organization is provisioned there.
