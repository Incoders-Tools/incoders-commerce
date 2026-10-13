# Vehicles

## Objective

Register the business's vehicles (one truck today) as a lookup table, and let a
delivery run pick one, defaulting to the only active vehicle. Basic data now
(plate, brand, model, year, notes), with room for insurance, registration,
fines, services and maintenance later (item 11 of the owner's 2026-10-11
request).

## Where it stands (2026-10-11)

- `delivery_runs.vehicle` is free text (0045), typed per run.

## Scope

- `vehicles`: organization-scoped, plate (unique in the organization), brand,
  model, year, notes, active flag; forced RLS like every tenant table.
- Web: "Vehículos" under the lookup tables (same pattern as the other ABMs).
- Delivery runs: choose a vehicle from the list; with a single active vehicle
  it is preselected. Old runs keep their free-text value.

## Out of scope (later)

- Insurance, registration, fines, services, maintenance records.

## Tasks

- [x] V1 Migration + API (CRUD, alphabetical by plate).
  Migration 0057 (appended to `init-rls.sql`, applied locally): `vehicles` (plate normalized and unique in the
  organization, brand, model, `model_year`, notes, active; forced RLS; audit "vehicle") and `delivery_runs.vehicle_id`
  (for V3; the run's `vehicle` text stays). `VehicleRules` (domain): a plate is written any way and kept in capitals
  without spaces or dashes, 5 to 10 letters or digits; year 1950 to next year; label "AB123CD · Ford F-4000".
  `PostgresVehicleStore` + `GET /vehicles` (any staff permission, `includeInactive`), `POST /vehicles`, `PUT
  /vehicles/{id}` (ManageUsers, as the other lookup tables; 409 `vehicle-plate-in-use`). The POS has no delivery runs:
  vehicles stay web only. RED: did not compile; GREEN: 4; regression (RLS, fulfillment, delivery, device operator) 119.
- [ ] V2 Web ABM under lookup tables.
- [ ] V3 Delivery runs pick a vehicle (default: the only active one).

## Constraints

Same as `owner-requests-quick-adjustments.md`.

## Progress

- Feature document created 2026-10-11.
