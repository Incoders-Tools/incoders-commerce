-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- vehicles V1/V3: the business's vehicles as a lookup table of the organization (plate, brand, model, year, notes, an
-- active flag), and the vehicle a delivery run uses. The plate is kept normalized (capitals, no spaces or dashes) and is
-- unique in the organization. `delivery_runs.vehicle_id` points at the vehicle picked; `delivery_runs.vehicle` (0045)
-- stays the text the run shows ("AB123CD · Ford F-4000" when picked; whatever was typed on the runs before this).
--
-- APPLIED AFTER: 0056_employee_goods.sql.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE delivery_runs DROP COLUMN IF EXISTS vehicle_id;
--   DROP TABLE IF EXISTS vehicles;

BEGIN;

CREATE TABLE IF NOT EXISTS vehicles (
    organization_id    uuid        NOT NULL,
    id                 uuid        NOT NULL,
    plate              text        NOT NULL CHECK (plate ~ '^[A-Z0-9]{5,10}$'),
    brand              text        NULL CHECK (brand IS NULL OR char_length(brand) <= 60),
    model              text        NULL CHECK (model IS NULL OR char_length(model) <= 60),
    model_year         integer     NULL CHECK (model_year IS NULL OR model_year BETWEEN 1950 AND 2200),
    notes              text        NULL CHECK (notes IS NULL OR char_length(notes) <= 500),
    is_active          boolean     NOT NULL DEFAULT true,
    created_by_user_id uuid        NOT NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    updated_at_utc     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT vehicles_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT vehicles_plate_uk UNIQUE (organization_id, plate)
);

ALTER TABLE vehicles ENABLE ROW LEVEL SECURITY;
ALTER TABLE vehicles FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON vehicles FROM PUBLIC;
GRANT SELECT, INSERT ON vehicles TO app_runtime;
GRANT UPDATE (plate, brand, model, model_year, notes, is_active, updated_at_utc) ON vehicles TO app_runtime;
DROP POLICY IF EXISTS vehicles_tenant_isolation ON vehicles;
CREATE POLICY vehicles_tenant_isolation ON vehicles
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

ALTER TABLE delivery_runs ADD COLUMN IF NOT EXISTS vehicle_id uuid NULL;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'delivery_runs_vehicle_fk') THEN
        ALTER TABLE delivery_runs ADD CONSTRAINT delivery_runs_vehicle_fk
            FOREIGN KEY (organization_id, vehicle_id) REFERENCES vehicles (organization_id, id);
    END IF;
END $$;
GRANT UPDATE (vehicle_id) ON delivery_runs TO app_runtime;

COMMIT;
