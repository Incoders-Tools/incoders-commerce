-- Development/reference PostgreSQL RLS policy for the cloud sync inbox.
--
-- This is the REAL production policy shape (ADR-002 / design.md: tenant keys,
-- claim-derived filters, RLS with default-deny, non-owner runtime role). It
-- is applied automatically by deploy/dev/compose.yaml for local/dev
-- validation and CI RLS runs when a real PostgreSQL instance is available.
--
-- `Commerce.Cloud.Api.CloudInboxStore` proves the same deny/allow semantics
-- in-memory for environments (such as this apply session) where no live
-- PostgreSQL instance is reachable. Wiring the real Npgsql-backed adapter
-- against this schema is tracked as follow-up production work.

CREATE TABLE IF NOT EXISTS sync_inbox (
    operation_id       uuid PRIMARY KEY,
    organization_id    uuid NOT NULL,
    branch_id          uuid NOT NULL,
    aggregate_id       uuid NOT NULL,
    aggregate_version  bigint NOT NULL,
    actor_id           uuid NOT NULL,
    correlation_id     uuid NOT NULL,
    occurred_at_utc    timestamptz NOT NULL,
    payload_kind       text NOT NULL,
    payload            jsonb NOT NULL,
    status             text NOT NULL DEFAULT 'Pending',
    acknowledged_at_utc timestamptz NULL
);

-- Row Level Security defaults to deny once enabled: with FORCE and no policy,
-- even the table owner gets zero rows back until an explicit policy exists.
ALTER TABLE sync_inbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE sync_inbox FORCE ROW LEVEL SECURITY;

-- Non-owner runtime role: the application connects as this role, never as
-- the table owner, so RLS cannot be bypassed by owner privilege.
--
-- LOGIN + a fixed password here is a LOCAL-DEV-ONLY convenience so
-- PostgresCloudInboxStoreTests / PoolerScopingTests can connect as this role
-- directly against `deploy/dev/compose.yaml`. Each real environment
-- (staging/production) provisions its OWN `app_runtime` password as a
-- Railway/Supabase secret per deploy/README.md — never this literal value.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_runtime') THEN
        CREATE ROLE app_runtime WITH LOGIN PASSWORD 'dev-only-password';
    ELSE
        ALTER ROLE app_runtime WITH LOGIN PASSWORD 'dev-only-password';
    END IF;
END
$$;

REVOKE ALL ON sync_inbox FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON sync_inbox TO app_runtime;

-- Tenant isolation policy: every row read/write is scoped to the
-- authenticated claim's organization, never a caller-submitted value.
--
-- NULLIF(..., ''): discovered during Unit 2's pooler PoC. Once a session has
-- ever run a transaction-local `set_config('app.current_org_id', v, true)`
-- (is_local = true, i.e. `SET LOCAL` semantics) and that transaction
-- committed, PostgreSQL does NOT revert the custom placeholder GUC back to
-- "unset"/NULL for the rest of the session — it reverts to an empty string
-- ''. On a connection-pooled/reused session (exactly PgBouncer transaction
-- pooling's shape), a query that forgets to re-apply tenant scope before
-- running would otherwise hit `''::uuid`, which RAISES A POSTGRES ERROR
-- rather than filtering to zero rows. `PostgresCloudInboxStore` always sets
-- scope as the first statement of every transaction, so this never happens
-- on the real code path — but the policy itself is hardened here so an
-- unscoped query fails CLOSED (zero rows) instead of failing with a
-- confusing cast error, regardless of caller discipline.
CREATE POLICY sync_inbox_tenant_isolation ON sync_inbox
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Commerce user credentials (deploy/db/migrations/0002_users.sql), hand-synced
-- verbatim here per the existing 0001/init-rls.sql convention. See 0002 for
-- the asymmetric user_directory rationale.

CREATE TABLE IF NOT EXISTS users (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    email           text NOT NULL,
    password_hash   text NOT NULL,
    branch_scope    uuid[] NOT NULL DEFAULT '{}',
    roles           jsonb NOT NULL DEFAULT '[]',
    is_revoked      boolean NOT NULL DEFAULT false,
    is_system_admin boolean NOT NULL DEFAULT false,
    created_at_utc  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS users_org_email_unique ON users (organization_id, email);

CREATE TABLE IF NOT EXISTS user_directory (
    email_normalized text PRIMARY KEY,
    organization_id  uuid NOT NULL,
    user_id          uuid NOT NULL
);

ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE ROW LEVEL SECURITY;
ALTER TABLE user_directory ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_directory FORCE ROW LEVEL SECURITY;

REVOKE ALL ON users, user_directory FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON users TO app_runtime;
GRANT SELECT, INSERT ON user_directory TO app_runtime;

DROP POLICY IF EXISTS users_tenant_isolation ON users;
CREATE POLICY users_tenant_isolation ON users
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS user_directory_lookup ON user_directory;
CREATE POLICY user_directory_lookup ON user_directory
    USING (true)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Commerce organization/branch persistence
-- (deploy/db/migrations/0003_organizations_branches.sql), hand-synced
-- verbatim here per the existing 0001/0002/init-rls.sql convention. Note the
-- organizations policy compares `id`, not `organization_id` — the
-- organization row IS the tenant; branches carries its own organization_id
-- for a direct-column-comparison policy, symmetric with users_tenant_isolation.

CREATE TABLE IF NOT EXISTS organizations (
    id         uuid PRIMARY KEY,
    name       text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS branches (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS branches_org_name_unique ON branches (organization_id, name);
CREATE INDEX IF NOT EXISTS branches_organization_id_idx ON branches (organization_id);

ALTER TABLE organizations ENABLE ROW LEVEL SECURITY;
ALTER TABLE organizations FORCE ROW LEVEL SECURITY;
ALTER TABLE branches      ENABLE ROW LEVEL SECURITY;
ALTER TABLE branches      FORCE ROW LEVEL SECURITY;

REVOKE ALL ON organizations, branches FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON organizations TO app_runtime;
GRANT SELECT, INSERT, UPDATE ON branches      TO app_runtime;

DROP POLICY IF EXISTS organizations_tenant_isolation ON organizations;
CREATE POLICY organizations_tenant_isolation ON organizations
    USING (id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS branches_tenant_isolation ON branches;
CREATE POLICY branches_tenant_isolation ON branches
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Commerce POS installation identity (deploy/db/migrations/0004_device_credentials.sql),
-- hand-synced verbatim here per the existing 0001/0002/0003/init-rls.sql
-- convention. See 0004 for the asymmetric RLS rationale (same class of
-- problem as user_directory_lookup: verification resolves the credential
-- BEFORE any tenant scope is known).

CREATE TABLE IF NOT EXISTS device_credentials (
    token_hash             text PRIMARY KEY,
    id                     uuid NOT NULL UNIQUE,
    organization_id        uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    branch_id              uuid NOT NULL REFERENCES branches (id) ON DELETE CASCADE,
    installation_id        uuid NOT NULL,
    issued_to_user_id      uuid NOT NULL,
    replaces_credential_id uuid NULL,
    is_revoked             boolean NOT NULL DEFAULT false,
    issued_at              timestamptz NOT NULL DEFAULT now(),
    revoked_at             timestamptz NULL
);
CREATE INDEX IF NOT EXISTS device_credentials_installation_idx
    ON device_credentials (installation_id) WHERE NOT is_revoked;

ALTER TABLE device_credentials ENABLE ROW LEVEL SECURITY;
ALTER TABLE device_credentials FORCE ROW LEVEL SECURITY;

REVOKE ALL ON device_credentials FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON device_credentials TO app_runtime;

DROP POLICY IF EXISTS device_credentials_lookup ON device_credentials;
CREATE POLICY device_credentials_lookup ON device_credentials
    FOR SELECT USING (true);

DROP POLICY IF EXISTS device_credentials_issue ON device_credentials;
CREATE POLICY device_credentials_issue ON device_credentials
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS device_credentials_revoke ON device_credentials;
CREATE POLICY device_credentials_revoke ON device_credentials
    FOR UPDATE USING (true) WITH CHECK (is_revoked);

-- commerce-password-recovery: password_reset_tokens (hand-kept sync of
-- deploy/db/migrations/0005_password_recovery.sql — see that file's remarks).
CREATE TABLE IF NOT EXISTS password_reset_tokens (
    token_hash      text PRIMARY KEY,
    user_id         uuid NOT NULL,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    requested_at    timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    consumed_at     timestamptz NULL
);
CREATE INDEX IF NOT EXISTS password_reset_tokens_user_active_idx
    ON password_reset_tokens (user_id) WHERE consumed_at IS NULL;
CREATE INDEX IF NOT EXISTS password_reset_tokens_expires_idx ON password_reset_tokens (expires_at);

ALTER TABLE password_reset_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE password_reset_tokens FORCE ROW LEVEL SECURITY;
REVOKE ALL ON password_reset_tokens FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE, DELETE ON password_reset_tokens TO app_runtime;

DROP POLICY IF EXISTS password_reset_tokens_lookup ON password_reset_tokens;
CREATE POLICY password_reset_tokens_lookup ON password_reset_tokens FOR SELECT USING (true);

DROP POLICY IF EXISTS password_reset_tokens_issue ON password_reset_tokens;
CREATE POLICY password_reset_tokens_issue ON password_reset_tokens
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS password_reset_tokens_consume ON password_reset_tokens;
CREATE POLICY password_reset_tokens_consume ON password_reset_tokens
    FOR UPDATE USING (true) WITH CHECK (consumed_at IS NOT NULL);

DROP POLICY IF EXISTS password_reset_tokens_purge ON password_reset_tokens;
CREATE POLICY password_reset_tokens_purge ON password_reset_tokens
    FOR DELETE USING (expires_at < now() - interval '7 days');

ALTER TABLE users ADD COLUMN IF NOT EXISTS session_version integer NOT NULL DEFAULT 0;

-- commerce-role-taxonomy: 0006_role_taxonomy.sql (data rewrite, hand-synced
-- verbatim per the existing convention). See 0006 for the FORCE RLS toggle
-- rationale. Fresh dev databases seed no legacy "admin" rows, so this is a
-- no-op there — it exists for parity with `MigrationRlsTests`, which applies
-- 0006 directly against seeded rows.
DO $$
BEGIN
    ALTER TABLE users NO FORCE ROW LEVEL SECURITY;

    UPDATE users
       SET roles = (
           SELECT jsonb_agg(
               CASE WHEN e->>'name' = 'admin'
                    THEN jsonb_set(e, '{name}', '"business-admin"')
                    ELSE e END)
           FROM jsonb_array_elements(roles) AS e)
     WHERE roles @> '[{"name":"admin"}]';

    ALTER TABLE users FORCE ROW LEVEL SECURITY;

    IF EXISTS (SELECT 1 FROM users WHERE roles @> '[{"name":"admin"}]') THEN
        RAISE EXCEPTION '0006: legacy "admin" role entries survived the rewrite';
    END IF;
END
$$;

-- commerce-role-taxonomy: 0007_platform_administration.sql, hand-synced
-- verbatim per the existing convention. See 0007 for the asymmetric RLS
-- rationale (platform_admins genesis-only INSERT; audit_log append-only;
-- platform_readonly's column-scoped, TO-scoped organizations read).

CREATE TABLE IF NOT EXISTS platform_admins (
    id                  uuid PRIMARY KEY,
    email               text NOT NULL UNIQUE,
    password_hash       text NOT NULL,
    created_at_utc      timestamptz NOT NULL DEFAULT now(),
    last_sign_in_at_utc timestamptz NULL
);

CREATE TABLE IF NOT EXISTS audit_log (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at_utc timestamptz NOT NULL DEFAULT now(),
    actor_kind      text NOT NULL,
    actor_id        uuid NOT NULL,
    organization_id uuid NULL,
    entity_type     text NOT NULL,
    entity_id       uuid NOT NULL,
    action          text NOT NULL,
    old_value       jsonb NULL,
    new_value       jsonb NULL
);
CREATE INDEX IF NOT EXISTS audit_log_entity_idx ON audit_log (entity_type, entity_id, occurred_at_utc DESC);
CREATE INDEX IF NOT EXISTS audit_log_org_idx    ON audit_log (organization_id, occurred_at_utc DESC);

ALTER TABLE platform_admins ENABLE ROW LEVEL SECURITY;
ALTER TABLE platform_admins FORCE ROW LEVEL SECURITY;
ALTER TABLE audit_log       ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_log       FORCE ROW LEVEL SECURITY;
REVOKE ALL ON platform_admins, audit_log FROM PUBLIC;

GRANT SELECT, INSERT ON platform_admins TO app_runtime;
GRANT UPDATE (last_sign_in_at_utc) ON platform_admins TO app_runtime;
GRANT INSERT ON audit_log TO app_runtime;

DROP POLICY IF EXISTS platform_admins_lookup ON platform_admins;
CREATE POLICY platform_admins_lookup ON platform_admins FOR SELECT USING (true);
DROP POLICY IF EXISTS platform_admins_touch ON platform_admins;
CREATE POLICY platform_admins_touch  ON platform_admins FOR UPDATE USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS platform_admins_genesis ON platform_admins;
CREATE POLICY platform_admins_genesis ON platform_admins FOR INSERT
    WITH CHECK (NOT EXISTS (SELECT 1 FROM platform_admins));

DROP POLICY IF EXISTS audit_log_append ON audit_log;
CREATE POLICY audit_log_append ON audit_log FOR INSERT
    WITH CHECK (organization_id IS NULL
                OR organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'platform_readonly') THEN
        CREATE ROLE platform_readonly WITH LOGIN PASSWORD 'dev-only-platform-readonly-password';
    ELSE
        ALTER ROLE platform_readonly WITH LOGIN PASSWORD 'dev-only-platform-readonly-password';
    END IF;
END $$;
GRANT USAGE ON SCHEMA public TO platform_readonly;
GRANT SELECT (id, name, created_at) ON organizations TO platform_readonly;
DROP POLICY IF EXISTS organizations_platform_read ON organizations;
CREATE POLICY organizations_platform_read ON organizations
    FOR SELECT TO platform_readonly USING (true);

-- commerce-customer-identity: customers, customer_ordering_access,
-- users.customer_id (hand-synced verbatim from
-- deploy/db/migrations/0008_customer_registry.sql per the existing
-- convention). See 0008 for the "no orders table" verified deviation and the
-- asymmetric customer_ordering_access RLS rationale (the device_credentials
-- precedent).

CREATE TABLE IF NOT EXISTS customers (
    id                  uuid PRIMARY KEY,
    organization_id     uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    customer_kind       text NOT NULL CHECK (customer_kind IN ('Retail','Wholesale')),
    display_name        text NOT NULL,
    legal_name          text NULL,
    tax_id_type         text NOT NULL DEFAULT 'None'
                             CHECK (tax_id_type IN ('None','Cuit','Cuil')),
    tax_id              text NULL,
    tax_condition       text NOT NULL DEFAULT 'NoAplica'
                             CHECK (tax_condition IN ('ConsumidorFinal','ResponsableInscripto',
                                                      'Monotributo','Exento','NoAplica')),
    phone               text NULL,
    email               text NULL,
    address_street      text NULL,
    address_number      text NULL,
    neighborhood        text NULL,
    locality            text NULL,
    province            text NULL,
    postal_code         text NULL,
    delivery_notes      text NULL,
    discount_percentage numeric(5,2) NULL,
    payment_terms       text NULL,
    notes               text NULL,
    is_enabled          boolean NOT NULL DEFAULT true,
    created_at_utc      timestamptz NOT NULL DEFAULT now(),
    created_by_user_id  uuid NOT NULL,
    updated_at_utc      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customers_tax_id_requires_type
        CHECK ((tax_id_type = 'None' AND tax_id IS NULL) OR
               (tax_id_type <> 'None' AND tax_id IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS customers_org_idx     ON customers (organization_id);
CREATE INDEX IF NOT EXISTS customers_org_updated ON customers (organization_id, updated_at_utc);

CREATE TABLE IF NOT EXISTS customer_ordering_access (
    credential_hash text PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    customer_id     uuid NOT NULL REFERENCES customers (id) ON DELETE CASCADE,
    is_enabled      boolean NOT NULL DEFAULT true,
    issued_at_utc   timestamptz NOT NULL DEFAULT now(),
    issued_by_user_id uuid NOT NULL,
    revoked_at_utc  timestamptz NULL
);
CREATE INDEX IF NOT EXISTS customer_ordering_access_customer_idx
    ON customer_ordering_access (customer_id) WHERE is_enabled;

ALTER TABLE users ADD COLUMN IF NOT EXISTS customer_id uuid NULL
    REFERENCES customers (id) ON DELETE RESTRICT;
CREATE INDEX IF NOT EXISTS users_customer_idx ON users (customer_id) WHERE customer_id IS NOT NULL;

ALTER TABLE users DROP CONSTRAINT IF EXISTS users_customer_has_no_roles;
ALTER TABLE users ADD CONSTRAINT users_customer_has_no_roles
    CHECK (customer_id IS NULL OR roles = '[]'::jsonb);

ALTER TABLE customers                ENABLE ROW LEVEL SECURITY;
ALTER TABLE customers                FORCE  ROW LEVEL SECURITY;
ALTER TABLE customer_ordering_access ENABLE ROW LEVEL SECURITY;
ALTER TABLE customer_ordering_access FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON customers, customer_ordering_access FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON customers                TO app_runtime;
GRANT SELECT, INSERT, UPDATE ON customer_ordering_access TO app_runtime;

DROP POLICY IF EXISTS customers_tenant_isolation ON customers;
CREATE POLICY customers_tenant_isolation ON customers
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS customer_ordering_access_lookup ON customer_ordering_access;
CREATE POLICY customer_ordering_access_lookup ON customer_ordering_access
    FOR SELECT USING (true);
DROP POLICY IF EXISTS customer_ordering_access_issue ON customer_ordering_access;
CREATE POLICY customer_ordering_access_issue ON customer_ordering_access
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
DROP POLICY IF EXISTS customer_ordering_access_revoke ON customer_ordering_access;
CREATE POLICY customer_ordering_access_revoke ON customer_ordering_access
    FOR UPDATE USING (true) WITH CHECK (NOT is_enabled);

-- commerce-pricing-engine: 0009_catalog_and_pricing.sql Part A (products,
-- presentations) + Part B (price_lists, price_list_entries), appended
-- verbatim per the hand-kept parity convention MigrationRlsTests asserts.

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
CREATE UNIQUE INDEX IF NOT EXISTS presentations_org_code_uk
    ON presentations (organization_id, identification_code)
    WHERE identification_code IS NOT NULL;

ALTER TABLE products      ENABLE ROW LEVEL SECURITY;
ALTER TABLE products      FORCE  ROW LEVEL SECURITY;
ALTER TABLE presentations ENABLE ROW LEVEL SECURITY;
ALTER TABLE presentations FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON products, presentations FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON products      TO app_runtime;
GRANT SELECT, INSERT, UPDATE ON presentations TO app_runtime;

DROP POLICY IF EXISTS products_tenant_isolation ON products;
CREATE POLICY products_tenant_isolation ON products
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS presentations_tenant_isolation ON presentations;
CREATE POLICY presentations_tenant_isolation ON presentations
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

CREATE TABLE IF NOT EXISTS price_lists (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL,
    is_default      boolean NOT NULL DEFAULT false,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid NOT NULL
);
CREATE INDEX IF NOT EXISTS price_lists_org_idx ON price_lists (organization_id);
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
        UNIQUE (price_list_id, presentation_id, effective_from)
);
CREATE INDEX IF NOT EXISTS price_list_entries_resolution_idx
    ON price_list_entries (price_list_id, presentation_id, effective_from DESC);

ALTER TABLE price_lists        ENABLE ROW LEVEL SECURITY;
ALTER TABLE price_lists        FORCE  ROW LEVEL SECURITY;
ALTER TABLE price_list_entries ENABLE ROW LEVEL SECURITY;
ALTER TABLE price_list_entries FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON price_lists, price_list_entries FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON price_lists       TO app_runtime;
GRANT SELECT, INSERT       ON price_list_entries  TO app_runtime;

DROP POLICY IF EXISTS price_lists_tenant_isolation ON price_lists;
CREATE POLICY price_lists_tenant_isolation ON price_lists
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS price_list_entries_tenant_isolation ON price_list_entries;
CREATE POLICY price_list_entries_tenant_isolation ON price_list_entries
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- commerce-pricing-engine: 0009_catalog_and_pricing.sql Part C
-- (supplier_price_mappings, price_import_batches, price_import_rows),
-- appended verbatim per the hand-kept parity convention MigrationRlsTests
-- asserts.

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
    reject_reason   text NULL
);
CREATE INDEX IF NOT EXISTS price_import_rows_batch_idx ON price_import_rows (batch_id);

ALTER TABLE supplier_price_mappings ENABLE ROW LEVEL SECURITY;
ALTER TABLE supplier_price_mappings FORCE  ROW LEVEL SECURITY;
ALTER TABLE price_import_batches    ENABLE ROW LEVEL SECURITY;
ALTER TABLE price_import_batches    FORCE  ROW LEVEL SECURITY;
ALTER TABLE price_import_rows       ENABLE ROW LEVEL SECURITY;
ALTER TABLE price_import_rows       FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON supplier_price_mappings, price_import_batches, price_import_rows FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON supplier_price_mappings TO app_runtime;
GRANT SELECT, INSERT, UPDATE ON price_import_batches    TO app_runtime;
GRANT SELECT, INSERT         ON price_import_rows       TO app_runtime;

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

-- commerce-guest-ordering: 0010_guest_ordering.sql, appended verbatim per the
-- hand-kept parity convention MigrationRlsTests asserts.

CREATE TABLE IF NOT EXISTS guest_order_verifications (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    document_id       text NOT NULL,
    contact_channel   text NOT NULL CHECK (contact_channel IN ('Email')),
    contact_address   text NOT NULL,
    code_hash         text NOT NULL,
    attempt_count     integer NOT NULL DEFAULT 0 CHECK (attempt_count <= 5),
    requested_at      timestamptz NOT NULL DEFAULT now(),
    expires_at        timestamptz NOT NULL,
    confirmed_at      timestamptz NULL,
    consumed_at       timestamptz NULL,
    consumed_order_id uuid NULL
);
CREATE INDEX IF NOT EXISTS guest_order_verifications_contact_idx
    ON guest_order_verifications (organization_id, contact_address, requested_at DESC);

ALTER TABLE guest_order_verifications ENABLE ROW LEVEL SECURITY;
ALTER TABLE guest_order_verifications FORCE ROW LEVEL SECURITY;
REVOKE ALL ON guest_order_verifications FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON guest_order_verifications TO app_runtime;

DROP POLICY IF EXISTS guest_order_verifications_lookup ON guest_order_verifications;
CREATE POLICY guest_order_verifications_lookup ON guest_order_verifications FOR SELECT USING (true);

DROP POLICY IF EXISTS guest_order_verifications_issue ON guest_order_verifications;
CREATE POLICY guest_order_verifications_issue ON guest_order_verifications
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS guest_order_verifications_update ON guest_order_verifications;
CREATE POLICY guest_order_verifications_update ON guest_order_verifications
    FOR UPDATE USING (true) WITH CHECK (true);

-- commerce-payments: 0011_payments.sql, appended verbatim per the hand-kept
-- parity convention MigrationRlsTests asserts.

CREATE TABLE IF NOT EXISTS payment_entries (
    entry_id            uuid PRIMARY KEY,
    organization_id     uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    operation_id        uuid NOT NULL UNIQUE,
    subject_kind        text NOT NULL CHECK (subject_kind IN ('Order','Sale')),
    subject_id          uuid NOT NULL,
    entry_kind          text NOT NULL CHECK (entry_kind IN ('Payment','Reversal')),
    method               text NOT NULL CHECK (method IN
                          ('Cash','AccountCredit','BankTransfer','Card','MercadoPago')),
    amount              numeric(12,2) NOT NULL CHECK (amount > 0),
    approval_state      text NOT NULL CHECK (approval_state IN
                          ('Approved','Declined','Unavailable')),
    reverses_entry_id   uuid NULL REFERENCES payment_entries (entry_id),
    provider_reference  text NULL,
    actor_id            uuid NOT NULL,
    recorded_at_utc     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payment_entries_reversal_requires_target
        CHECK ((entry_kind = 'Reversal') = (reverses_entry_id IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS payment_entries_org_idx ON payment_entries (organization_id);
CREATE INDEX IF NOT EXISTS payment_entries_subject_idx ON payment_entries (subject_kind, subject_id);

ALTER TABLE customers ADD COLUMN IF NOT EXISTS billing_instrument_reference text NULL;
ALTER TABLE customers DROP CONSTRAINT IF EXISTS customers_instrument_not_pan_shaped;
ALTER TABLE customers ADD CONSTRAINT customers_instrument_not_pan_shaped
    CHECK (billing_instrument_reference IS NULL
           OR billing_instrument_reference !~ '^[0-9]{13,19}$');

ALTER TABLE payment_entries ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment_entries FORCE ROW LEVEL SECURITY;
REVOKE ALL ON payment_entries FROM PUBLIC;
GRANT SELECT, INSERT ON payment_entries TO app_runtime;

DROP POLICY IF EXISTS payment_entries_tenant_isolation ON payment_entries;
CREATE POLICY payment_entries_tenant_isolation ON payment_entries
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);



-- commerce-admin-console: dev initialization represents the final 0012 shape.
ALTER TABLE users ADD COLUMN IF NOT EXISTS is_system_admin boolean NOT NULL DEFAULT false;
DROP TABLE IF EXISTS platform_admins;

-- commerce-price-composition: 0013_rate_components.sql, appended verbatim per
-- the hand-kept parity convention. Rates are ROWS, never columns; the SET is
-- the effective-dated unit; append-only is a GRANT, not a comment.

CREATE TABLE IF NOT EXISTS rate_component_sets (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    price_list_id      uuid NULL REFERENCES price_lists (id) ON DELETE CASCADE,
    effective_from     date NOT NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid NOT NULL
);

-- Two partial indexes, not one: in SQL two NULLs never compare equal, so a
-- single index would let an organization publish unlimited same-day default
-- sets and make "the effective default for this date" a coin flip.
CREATE UNIQUE INDEX IF NOT EXISTS rate_component_sets_list_day_uk
    ON rate_component_sets (price_list_id, effective_from)
    WHERE price_list_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS rate_component_sets_org_default_day_uk
    ON rate_component_sets (organization_id, effective_from)
    WHERE price_list_id IS NULL;
CREATE INDEX IF NOT EXISTS rate_component_sets_resolution_idx
    ON rate_component_sets (organization_id, price_list_id, effective_from DESC);

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
    CONSTRAINT rate_components_one_order_per_set UNIQUE (set_id, component_order)
);
CREATE INDEX IF NOT EXISTS rate_components_set_idx ON rate_components (set_id, component_order);

ALTER TABLE rate_component_sets ENABLE ROW LEVEL SECURITY;
ALTER TABLE rate_component_sets FORCE  ROW LEVEL SECURITY;
ALTER TABLE rate_components     ENABLE ROW LEVEL SECURITY;
ALTER TABLE rate_components     FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON rate_component_sets, rate_components FROM PUBLIC;
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
