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

- [x] F1 (closed by T6, 9037adb) Master data PUT reactivated an entry when `isActive` was omitted; it now keeps the stored value.
- [x] F2 (closed by T7, 8308fb1) Customer PUT keep-on-omit could lose a concurrent update; the keep-on-omit is now decided in SQL and the optional token is part of the UPDATE's WHERE.
- [x] F3 (closed by T8, 3cdbe7d) Endpoint and domain disagreed on separators; the PUT endpoint already normalized dashed values, the domain constructor did not (it rejected a dotted DNI and stored dashed Cuit/Cuil) and now goes through `TaxIdRules.TryNormalize` too.
- [ ] F4 Search accent folding differs between SQL and the tax-id path (`PostgresCustomerStore.cs:274-277`, `370-375`).
- [ ] F5 Seed: a city whose global id already exists in another org is skipped silently (`001_vaca_verde_master_data.sql:62-94`, `118`); raise a notice.
- [ ] F6 `provision-admin.ps1` decides "applied/skipped" from a NOTICE string (`:469-474`).
- [x] F7 (closed by owner: email search not wanted; contact search replaces it, T10) Web: the customer list's stale-response guard is untested; email is no longer searched (`CustomersScreen.tsx:61`).
- [ ] F8 Seed tests' link assertions are weak (`VacaVerdeSeedTests.cs:144-145`).
- [ ] F9 Investigate the `PosAdminClientCompositionTests` full-run failure.
- Accepted by owner: real customer data committed to the repo (R1-pii-committed); org resolved by name.

## Scope change (owner, 2026-10-02)

- Cities are a **core, organization-independent catalog** (same for every
  business), with country and province codes from the official Argentine
  source. Managed by the system admin only.
- Customers get a **contacts sub-table** (a company can have several
  contacts: first name, last name, phone at least). `contact_name` is migrated
  into it and removed. Customer search matches contact first/last name.
- F7: not searching by email is accepted by the owner (closed, won't fix).
- Business types stay organization-scoped.

Source (verified 2026-10-02): Georef API of datos.gob.ar
(`https://apis.datos.gob.ar/georef/api/`): 24 provinces with INDEC id and
ISO 3166-2 code (e.g. `06` / `AR-B` Buenos Aires), 4037 `localidades` with
INDEC ids (e.g. Capitán Sarmiento `06140010`, Río Tala `06770040`). Load all
of them. Licence/attribution to be confirmed and recorded in the README.

## Tasks (round 2)

- [x] T6 F1: master-data PUT keeps the stored `isActive` when omitted (route: delegated backend writer) - commit 9037adb; RED: 2/2 theory cases failed (omitted `isActive` reactivated the entry), GREEN: 18/18 `CustomerMasterDataEndpointTests`
- [x] T7 F2: customer PUT optimistic concurrency (`expectedUpdatedAtUtc` token, 409 `customer-modified` on mismatch; omitted token = legacy POS path, no check) (route: delegated backend writer) - commit 8308fb1; RED: 2 new tests failed (stale token answered 200; two concurrent writers both 200), GREEN: 185/185 customer tests incl. 5 concurrent-writer rounds (exactly one 200 and one 409)
- [x] T8 F3: customer PUT accepts and normalizes legacy dashed Cuit/Cuil (route: delegated backend writer) - commit 3cdbe7d; RED: 3/3 domain cases failed (constructor kept separators; endpoint PUT cases already passed), GREEN: 190/190 customer tests (one legacy assertion that pinned the dashed value updated)
- [x] T9 Core geography: global `countries`, `provinces`, `cities` seeded from Georef in a migration; customers point to global cities; org `cities` retired with data mapped; Vaca Verde seed updated; sysadmin-only write API and city search endpoint (route: delegated backend writer) - commit 528d84e (migration `0028_core_geography.sql`, generator `deploy/db/reference/georef/`); RED: 7/7 migration tests failed with 0028 absent, 6/6 endpoint tests failed (404), GREEN: 7/7 + 6/6 + 297 related tests; local `commerce_dev`: 1 country, 24 provinces, 4037 cities, 23 of 25 organization cities mapped, 72 of 87 customers with a city
- [x] T10 Customer contacts sub-table (first/last name, phone, email, role, primary), `contact_name` migrated and dropped, search by contact name, seed contacts from the CLIENTE column (route: delegated backend writer) - commit 16a3b17 (migration `0029_customer_contacts.sql`); RED: 10/10 new tests failed (table and API absent), GREEN: 10/10 + 125 related; local `commerce_dev`: 39 contacts (all primary), seed applied twice (second run inserted 0 and updated 0)
- [x] T11 Web: cities ABM moved to the system-admin area with province/country; city picker with server search; customer form contacts editor; send `updatedAtUtc` and show the 409 conflict (route: delegated web writer) - commits aac40a1 (sysadmin cities screen, `CityPicker`, nav/route gating, e2e) and 7f94f15 (contacts editor, `expectedUpdatedAtUtc`, 409 reload, primary contact in the list); RED: CityPicker import failure then 1 failing picker assertion, CitiesScreen import failure (api/cities removed), 3 customer form/list tests, then 12 contacts/concurrency tests; GREEN: `npm test` 59 files / 414 tests passed, `npm run lint` exit 0 (pre-existing warnings only), `npm run build` ok; e2e specs updated by hand, not run (CI-only). Decisions: status filter is "active only" / "include inactive" (the API has no inactive-only query); picker lists the first page on focus; fully blank contact rows are dropped on save; first contact added is primary; Recargar remounts the form with fresh data (unsaved edits are discarded).

- Round 2 backend (T6-T10) done on `dev`, not pushed. Full `dotnet test`: Integration 1749 passed, 1 failed (known `PublicRateLimitTests...IsUnreachable_AndAppStillStarts`, launcher wwwroot), 0 skipped; Upgrade 123 passed; Bootstrap 1 passed (the flaky `PosAdminClientCompositionTests` passed this run).
- API changes for the web writer (T11): `/customers/cities` is gone; use `GET /geo/provinces`, `GET /geo/cities?search&provinceId&limit&offset&includeInactive`, `GET /geo/cities/{id}`, sysadmin-only `POST /geo/cities` and `PUT /geo/cities/{id}`. Customer JSON: `cityId`, `cityName`, `provinceId`, `provinceName`, `contacts[]` (no `contactName`); PUT carries `expectedUpdatedAtUtc` (409 `{"error":"customer-modified"}`) and `contacts` (replace-set, omitted = keep).
- Decisions (round 2): the global table keeps the name `cities` (the organization table is renamed to `org_cities_retired` inside 0028, mapped and dropped, so the final schema reads `customers.city_id -> cities`); migrations 0027 gained re-run guards so the whole chain can still be re-applied after 0028/0029 (the test fixtures do that); Buenos Aires city is Georef's province `02` with its barrios and the representative locality `02014010` ("Capital Federal" maps to it); the Georef data licence (CC BY 4.0) was read from the datos.gob.ar catalogue metadata, the attribution wording is marked unverified in the README; the seed restores the owner's original city audit dates on the global rows only while a row is untouched (created = updated and later than the original).
- City mapping of the 25 owner cities (migration NOTICEs): mapped 23, unmapped 2 (Doyle: only "Pueblo Doyle" 06770030 exists; Urquiza: ambiguous, candidates Villa Urquiza CABA 0208401004 / Villa Urquiza Entre Rios 30084300 / General Urquiza Misiones 54098040). San Nicolas mapped by name prefix to San Nicolas de los Arroyos (06763050) and Cordoba/Santiago del Estero by exact name outside Buenos Aires: owner to confirm. No customer used Doyle or Urquiza.

## Next step

Owner review of `deploy/db/seeds/vaca-verde/report.md` (open questions: possible duplicates, "Ruta 9" locality, customers without city or business type); then promote `dev` to `main` and run the README runbook per environment once the organization is provisioned there.
