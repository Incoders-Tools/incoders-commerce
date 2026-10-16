-- Repo-owned, idempotent per-environment migration.
--
-- commerce-pricing-engine design.md "Verified deviation": there is NO
-- `products`/`presentations` table anywhere in this repo — `Catalog.cs`
-- builds `Product`/`Presentation` objects from the request body on every
-- call. Every downstream requirement of this change (identification code,
-- price entries referencing a presentation, import row matching) has nothing
-- to attach to without real catalog persistence, so catalog persistence is
-- Part A of this migration (Work Unit 1), landing BEFORE the price schema
-- (Part B, Work Unit 2). Part C (supplier_price_mappings,
-- price_import_batches, price_import_rows — Work Unit 9, the Excel import
-- pipeline) is added to this SAME file by a later apply batch; this batch
-- (Work Units 1-2) does not create those three tables.
--
-- Both parts follow 0008's exact symmetric tenant-isolation policy shape,
-- including the `NULLIF(..., '')::uuid` pooler-safety hardening from 0001.
-- NEVER regress that fix. Neither part grants DELETE to `app_runtime` — the
-- `customers`/`platform_admins` precedent: destruction is unavailable, not
-- merely discouraged.
--
-- DEVIATION FROM design.md's SQL EXCERPT: design.md's illustrative
-- `presentations` DDL shows `quantity_behavior text NOT NULL CHECK
-- (quantity_behavior IN ('Integral','Fractional'))`. The REAL domain enum
-- (`Commerce.Domain.Catalog.QuantityBehavior`, verified in
-- src/Commerce.Domain/Catalog/QuantityBehavior.cs) is
-- `FixedQuantity | Weighted | Bulk`. The CHECK constraint below uses the
-- real enum's values, not the design excerpt's illustrative ones — a
-- database CHECK that didn't match the actual domain type would reject
-- every real write.

-- ===========================================================================
-- Part A (Work Unit 1): products, presentations
-- ===========================================================================

CREATE TABLE IF NOT EXISTS products (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name               text NOT NULL,
    category_id        uuid NOT NULL,
    default_unit_id    uuid NOT NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid NOT NULL,
    updated_at_utc     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS products_org_idx     ON products (organization_id);
CREATE INDEX IF NOT EXISTS products_org_updated ON products (organization_id, updated_at_utc);

CREATE TABLE IF NOT EXISTS presentations (
    id                  uuid PRIMARY KEY,
    organization_id     uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    product_id          uuid NOT NULL REFERENCES products (id) ON DELETE CASCADE,
    name                text NOT NULL,
    quantity_behavior   text NOT NULL CHECK (quantity_behavior IN ('FixedQuantity','Weighted','Bulk')),
    unit_id             uuid NOT NULL,
    identification_code text NULL,
    created_at_utc      timestamptz NOT NULL DEFAULT now(),
    created_by_user_id  uuid NOT NULL,
    updated_at_utc      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS presentations_org_idx     ON presentations (organization_id);
CREATE INDEX IF NOT EXISTS presentations_org_updated ON presentations (organization_id, updated_at_utc);
CREATE INDEX IF NOT EXISTS presentations_product_idx ON presentations (product_id);

-- Per-organization, partial: two orgs may legitimately share an EAN-13, and
-- an unlabelled presentation stays unconstrained. Enforced HERE, not in UI
-- code (design.md "Identification code placement and uniqueness").
CREATE UNIQUE INDEX IF NOT EXISTS presentations_org_code_uk
    ON presentations (organization_id, identification_code)
    WHERE identification_code IS NOT NULL;

ALTER TABLE products      ENABLE ROW LEVEL SECURITY;
ALTER TABLE products      FORCE  ROW LEVEL SECURITY;
ALTER TABLE presentations ENABLE ROW LEVEL SECURITY;
ALTER TABLE presentations FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON products, presentations FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON products      TO app_runtime;  -- no DELETE
GRANT SELECT, INSERT, UPDATE ON presentations TO app_runtime;  -- no DELETE

DROP POLICY IF EXISTS products_tenant_isolation ON products;
CREATE POLICY products_tenant_isolation ON products
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS presentations_tenant_isolation ON presentations;
CREATE POLICY presentations_tenant_isolation ON presentations
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- Part B (Work Unit 2): price_lists, price_list_entries
-- ===========================================================================
--
-- APPEND-ONLY (design.md "Effective-dating shape"). `price_list_entries` has
-- NO `effective_to`: publishing a new price is a pure INSERT, never an
-- UPDATE of history. "No effective price for this date" is exactly ZERO
-- ROWS, not a gap between two ranges. `UNIQUE (price_list_id,
-- presentation_id, effective_from)` turns a same-day double-publish into a
-- 409, not a silent coin flip.

CREATE TABLE IF NOT EXISTS price_lists (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL,
    is_default      boolean NOT NULL DEFAULT false,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid NOT NULL
);
CREATE INDEX IF NOT EXISTS price_lists_org_idx ON price_lists (organization_id);
-- One default list per organization (design.md "Which price list resolves").
CREATE UNIQUE INDEX IF NOT EXISTS price_lists_one_default
    ON price_lists (organization_id) WHERE is_default;

CREATE TABLE IF NOT EXISTS price_list_entries (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    price_list_id      uuid NOT NULL REFERENCES price_lists (id) ON DELETE CASCADE,
    presentation_id    uuid NOT NULL REFERENCES presentations (id) ON DELETE CASCADE,
    unit_price         numeric(12,2) NOT NULL CHECK (unit_price > 0),
    effective_from     date NOT NULL,
    source             text NOT NULL DEFAULT 'Manual'
                            CHECK (source IN ('Manual','Import')),
    import_batch_id    uuid NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid NOT NULL,
    CONSTRAINT price_list_entries_one_per_day
        UNIQUE (price_list_id, presentation_id, effective_from)  -- 409, not a coin flip
);
CREATE INDEX IF NOT EXISTS price_list_entries_resolution_idx
    ON price_list_entries (price_list_id, presentation_id, effective_from DESC);

ALTER TABLE price_lists        ENABLE ROW LEVEL SECURITY;
ALTER TABLE price_lists        FORCE  ROW LEVEL SECURITY;
ALTER TABLE price_list_entries ENABLE ROW LEVEL SECURITY;
ALTER TABLE price_list_entries FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON price_lists, price_list_entries FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON price_lists        TO app_runtime;  -- no DELETE
GRANT SELECT, INSERT        ON price_list_entries  TO app_runtime;  -- append-only: no UPDATE, no DELETE

DROP POLICY IF EXISTS price_lists_tenant_isolation ON price_lists;
CREATE POLICY price_lists_tenant_isolation ON price_lists
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS price_list_entries_tenant_isolation ON price_list_entries;
CREATE POLICY price_list_entries_tenant_isolation ON price_list_entries
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- Part C (Work Unit 9): supplier_price_mappings, price_import_batches,
-- price_import_rows — the Excel supplier-price-import pipeline.
-- ===========================================================================
--
-- Import states are deliberately `Staged -> Committed | Rejected` plus
-- `Failed`, with NO `Uploaded` state (design.md "Import state machine"): a
-- file that fails validation never creates a batch row, so an
-- un-reviewable batch cannot exist mid-pipeline. `price_import_rows` is
-- append-only — every row's `match_status` is decided once, at parse time
-- (no UPDATE grant, mirroring `price_list_entries`).

CREATE TABLE IF NOT EXISTS supplier_price_mappings (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    supplier_name      text NOT NULL,
    sheet_name         text NOT NULL,
    header_row         integer NOT NULL CHECK (header_row > 0),
    code_column        text NOT NULL,
    price_column       text NOT NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid NOT NULL
);
-- A supplier's mapping is configured once and reused for every later
-- import from that supplier (decision (a)) — one mapping per (org, name).
CREATE UNIQUE INDEX IF NOT EXISTS supplier_price_mappings_org_name_uk
    ON supplier_price_mappings (organization_id, supplier_name);

CREATE TABLE IF NOT EXISTS price_import_batches (
    id                   uuid PRIMARY KEY,
    organization_id      uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    supplier_mapping_id  uuid NOT NULL REFERENCES supplier_price_mappings (id),
    file_name            text NOT NULL,
    row_count            integer NOT NULL,
    status               text NOT NULL DEFAULT 'Staged'
                             CHECK (status IN ('Staged', 'Committed', 'Rejected', 'Failed')),
    uploaded_at_utc      timestamptz NOT NULL DEFAULT now(),
    uploaded_by_user_id  uuid NOT NULL,
    resolved_at_utc      timestamptz NULL
);
CREATE INDEX IF NOT EXISTS price_import_batches_org_idx ON price_import_batches (organization_id);

CREATE TABLE IF NOT EXISTS price_import_rows (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    batch_id        uuid NOT NULL REFERENCES price_import_batches (id) ON DELETE CASCADE,
    row_number      integer NOT NULL,
    raw_code        text NULL,
    raw_price       text NULL,
    presentation_id uuid NULL REFERENCES presentations (id) ON DELETE SET NULL,
    current_price   numeric(12,2) NULL,
    proposed_price  numeric(12,2) NULL,
    match_status    text NOT NULL CHECK (match_status IN
                        ('Matched', 'NoChange', 'UnknownCode', 'InvalidPrice', 'DuplicateInFile')),
    reject_reason   text NULL   -- every rejected row carries its reason; never silent
);
CREATE INDEX IF NOT EXISTS price_import_rows_batch_idx ON price_import_rows (batch_id);

ALTER TABLE supplier_price_mappings ENABLE ROW LEVEL SECURITY;
ALTER TABLE supplier_price_mappings FORCE  ROW LEVEL SECURITY;
ALTER TABLE price_import_batches    ENABLE ROW LEVEL SECURITY;
ALTER TABLE price_import_batches    FORCE  ROW LEVEL SECURITY;
ALTER TABLE price_import_rows       ENABLE ROW LEVEL SECURITY;
ALTER TABLE price_import_rows       FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON supplier_price_mappings, price_import_batches, price_import_rows FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON supplier_price_mappings TO app_runtime;  -- no DELETE
GRANT SELECT, INSERT, UPDATE ON price_import_batches    TO app_runtime;  -- no DELETE (status transitions are UPDATEs)
GRANT SELECT, INSERT         ON price_import_rows       TO app_runtime;  -- append-only: no UPDATE, no DELETE

DROP POLICY IF EXISTS supplier_price_mappings_tenant_isolation ON supplier_price_mappings;
CREATE POLICY supplier_price_mappings_tenant_isolation ON supplier_price_mappings
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS price_import_batches_tenant_isolation ON price_import_batches;
CREATE POLICY price_import_batches_tenant_isolation ON price_import_batches
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS price_import_rows_tenant_isolation ON price_import_rows;
CREATE POLICY price_import_rows_tenant_isolation ON price_import_rows
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Inverse (rollback), shipped as comments — NOT executed by this file:
--   DROP TABLE price_import_rows;
--   DROP TABLE price_import_batches;
--   DROP TABLE supplier_price_mappings;
--   DROP TABLE price_list_entries;
--   DROP TABLE price_lists;
--   DROP TABLE presentations;
--   DROP TABLE products;
-- NOTE (design.md "Rollback Plan"): lossy once real price-carrying orders or
-- line-item sales exist. Prefer forward-fix after first real use.
