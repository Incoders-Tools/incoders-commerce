-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- commerce-price-composition slice 1 (specs/price-list-management/spec.md:
-- "Price List Rate Components", "Rate Components Are Scoped To The List, Not
-- The Organization", "Organization Default Rate Components", "Explicit
-- Calculation Base Per Component", "Append-Only Effective-Dated Rate
-- Component History", "Organization-Scoped Component Persistence With RLS").
--
-- APPLIED AFTER: 0012_admin_console.sql, the last shipped migration. Its
-- real data dependencies are only 0003's `organizations` and 0009's
-- `price_lists`; 0012 touches `users`/`platform_admins` and is orthogonal.
--
-- INVERSE (rollback), shipped as comments — NOT executed by this file:
--   DROP TABLE rate_components;
--   DROP TABLE rate_component_sets;
-- Lossy once a real rate set has been published: the base/composed split is
-- unrecoverable from `price_list_entries` alone. Prefer forward-fix after
-- first real use.
--
-- RATES ARE ROWS, NEVER COLUMNS (design.md "Component shape"): nothing here
-- names VAT, gross-receipts tax, freight or markup, so a zone surcharge or a
-- new IIBB perception is an INSERT, not a migration.
--
-- Both tables follow 0009's exact symmetric tenant-isolation policy shape,
-- including the `NULLIF(..., '')::uuid` pooler-safety hardening from 0001.
-- NEVER regress that fix.

-- ===========================================================================
-- rate_component_sets — the effective-dated unit
-- ===========================================================================
--
-- The SET, not the individual row, is what carries `effective_from`
-- (design.md "Versioning granularity"): a dated set fully REPLACES the
-- previously effective set for its owner, so changing one rate publishes a
-- new complete set. Composing from independently dated rows would let an
-- accidental omission silently drop a tax instead of failing visibly.
--
-- APPEND-ONLY with deliberately NO `effective_to`, exactly mirroring
-- `price_list_entries`: "no components effective for this date" is exactly
-- ZERO matching rows, never a gap between two ranges.
--
-- OWNERSHIP: `price_list_id` names the owning list, or is NULL for the
-- organization's inheritable default set. Components live on the LIST
-- (design.md decision 1): the real sheet is titled "Reparto" — its 7%
-- freight exists because it is the DELIVERY list, and a counter list in the
-- same organization would not carry it.

CREATE TABLE IF NOT EXISTS rate_component_sets (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    price_list_id      uuid NULL REFERENCES price_lists (id) ON DELETE CASCADE,
    effective_from     date NOT NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid NOT NULL
);

-- Two partial indexes rather than one `UNIQUE (price_list_id,
-- effective_from)`: in SQL two NULLs never compare equal, so a single index
-- would let an organization publish unlimited same-day default sets and make
-- "the effective default for this date" a coin flip. A same-day
-- double-publish is a 23505 for the endpoint to turn into a 409, exactly as
-- `price_list_entries_one_per_day` does.
CREATE UNIQUE INDEX IF NOT EXISTS rate_component_sets_list_day_uk
    ON rate_component_sets (price_list_id, effective_from)
    WHERE price_list_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS rate_component_sets_org_default_day_uk
    ON rate_component_sets (organization_id, effective_from)
    WHERE price_list_id IS NULL;

-- The resolution query: latest set at or before a date, for a list or for
-- the organization default.
CREATE INDEX IF NOT EXISTS rate_component_sets_resolution_idx
    ON rate_component_sets (organization_id, price_list_id, effective_from DESC);

-- ===========================================================================
-- rate_components — the open rate rows inside one set
-- ===========================================================================
--
-- `percentage` is a percentage NUMBER, not a fraction: 10.5 means 10.5%,
-- never 0.105 (the choice the proposal deferred, fixed here and mirrored by
-- `Commerce.Domain.Pricing.RateComponent.Percentage`). numeric(9,4) keeps
-- sub-basis-point rates exact and never float.
--
-- `calculation_base` admits only 'Base' and 'Subtotal' — deliberately NOT
-- the domain enum's `Unspecified` sentinel. The spec says a component
-- without a declared calculation base MUST be rejected; this CHECK makes
-- that true of every write path, not just of the ones that go through the
-- domain constructor.
--
-- `component_order`, not `order`: `order` is a reserved word and would force
-- quoting at every call site.

CREATE TABLE IF NOT EXISTS rate_components (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    set_id           uuid NOT NULL REFERENCES rate_component_sets (id) ON DELETE CASCADE,
    code             text NOT NULL,
    label            text NOT NULL,
    percentage       numeric(9,4) NOT NULL CHECK (percentage >= 0),
    calculation_base text NOT NULL CHECK (calculation_base IN ('Base','Subtotal')),
    component_order  integer NOT NULL CHECK (component_order >= 0),
    CONSTRAINT rate_components_one_code_per_set  UNIQUE (set_id, code),
    -- Two components sharing an order make the composition depend on row
    -- identity, which is unreviewable the moment one of them is 'Subtotal'.
    CONSTRAINT rate_components_one_order_per_set UNIQUE (set_id, component_order)
);
CREATE INDEX IF NOT EXISTS rate_components_set_idx ON rate_components (set_id, component_order);

-- ===========================================================================
-- RLS and grants
-- ===========================================================================

ALTER TABLE rate_component_sets ENABLE ROW LEVEL SECURITY;
ALTER TABLE rate_component_sets FORCE  ROW LEVEL SECURITY;
ALTER TABLE rate_components     ENABLE ROW LEVEL SECURITY;
ALTER TABLE rate_components     FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON rate_component_sets, rate_components FROM PUBLIC;
-- Append-only is a GRANT, not a comment: no UPDATE and no DELETE, so
-- "a persisted set MUST NOT be mutated or deleted" holds even against a
-- future endpoint that forgets.
GRANT SELECT, INSERT ON rate_component_sets TO app_runtime;
GRANT SELECT, INSERT ON rate_components     TO app_runtime;

DROP POLICY IF EXISTS rate_component_sets_tenant_isolation ON rate_component_sets;
CREATE POLICY rate_component_sets_tenant_isolation ON rate_component_sets
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS rate_components_tenant_isolation ON rate_components;
CREATE POLICY rate_components_tenant_isolation ON rate_components
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
