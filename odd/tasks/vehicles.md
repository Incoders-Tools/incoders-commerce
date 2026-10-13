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
- [x] V2 Web ABM under lookup tables.
  "Vehículos" after "Puestos" (`VehiclesScreen`, `/app/vehicles`, texts under `fulfillment` → `vehicles`): every vehicle
  by plate with the inactive ones marked; "Nuevo vehículo" and "Editar" (patente, marca, modelo, año, observaciones,
  activo on edit). The form checks the plate as the server (5 to 10 letters or digits once spaces and dashes go) and the
  year (1950 to next year), and explains a plate in use. RED: the screen did not exist and the sidebar lacked the item;
  GREEN: web 828 (98 files; one run had `CustomerForm` time out, green alone and on the rerun, A10).
- [x] V3 Delivery runs pick a vehicle (default: the only active one).
  Server: `DeliveryRunRequest.VehicleId` picks an active vehicle and the run's `vehicle` text becomes its label
  ("AB123CD · Ford F-4000", what the run screen and the remito show); 409 `vehicle-not-found` (none of the
  organization's) or `vehicle-inactive` (unless it is the one the run already had, deactivated since); an empty id is a
  400; without a vehicle picked the typed text stays as before. The run reads `vehicleId`. Web: "Vehículo" is a list
  ("Sin vehículo", the active vehicles; a new run takes the only one by itself); editing an older run keeps "<texto>
  (escrito a mano)" and a run's vehicle deactivated since. The POS has no delivery runs. RED: did not compile (server),
  no list in the form (web); GREEN: vehicles + fulfillment 13, regression 121, web 832.

## Follow-ups

- [x] W1 V1 review (`review-7b5b401ca07948d5`, approved), fixed right after `c37a71e`: an edit without `isActive`
  reactivated the vehicle; now an omitted flag is active on create and kept on edit. Also proved: a duplicate is a 409
  (status, not only the code), an edit cannot take another vehicle's plate, a brand over 60 characters is a 400. RED:
  the edited vehicle came back active; GREEN: 4.

## Constraints

Same as `owner-requests-quick-adjustments.md`.

## Progress

- Feature document created 2026-10-11.
