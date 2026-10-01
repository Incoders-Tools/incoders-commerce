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

-- commerce-price-composition review round 1: 0014_rate_component_tenancy.sql,
-- appended verbatim per the hand-kept mirror convention. Tenant-composite
-- references, an organization-scoped same-day key, and case-insensitive
-- component codes.

-- ===========================================================================
-- 1. Tenant-composite references
-- ===========================================================================
--
-- THE GAP. Row-level security filters SELECT; it does NOT run inside a
-- foreign-key check. `price_lists_tenant_isolation` therefore hides another
-- organization's list from every query the application can write, while the
-- referential-integrity trigger behind
-- `rate_component_sets.price_list_id REFERENCES price_lists (id)` happily
-- confirms that same row exists. An INSERT scoped to organization A could
-- name organization B's `price_list_id`, and — because the uniqueness key in
-- section 2 was keyed on the list alone — also PRE-EMPT B's own publication
-- for that day. `rate_components.set_id` had the identical shape one level
-- down.
--
-- THE FIX, the standard tenant-safe reference: make the tenant part of the
-- key. `(organization_id, price_list_id)` can only resolve against a
-- `price_lists` row carrying the SAME `organization_id`, so the boundary is
-- enforced by the same mechanism that enforces existence, with no policy,
-- no trigger and no application check involved.
--
-- MATCH SIMPLE (the default) is load-bearing, not an oversight: when ANY
-- referencing column is NULL the constraint is skipped entirely. A set owned
-- by the organization rather than by a list has `price_list_id IS NULL` and
-- references no list at all, so it must not be checked — and is not.

-- The referenced keys. Redundant with each table's primary key by design:
-- a composite foreign key needs a unique constraint over exactly its target
-- columns, and `(organization_id, id)` is unique for the trivial reason that
-- `id` already is.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'price_lists_org_scoped_uk'
    ) THEN
        ALTER TABLE price_lists
            ADD CONSTRAINT price_lists_org_scoped_uk UNIQUE (organization_id, id);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'rate_component_sets_org_scoped_uk'
    ) THEN
        ALTER TABLE rate_component_sets
            ADD CONSTRAINT rate_component_sets_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

-- Replace the tenant-blind references with tenant-composite ones.
ALTER TABLE rate_component_sets DROP CONSTRAINT IF EXISTS rate_component_sets_price_list_id_fkey;
ALTER TABLE rate_components     DROP CONSTRAINT IF EXISTS rate_components_set_id_fkey;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'rate_component_sets_price_list_org_fk'
    ) THEN
        ALTER TABLE rate_component_sets
            ADD CONSTRAINT rate_component_sets_price_list_org_fk
            FOREIGN KEY (organization_id, price_list_id)
            REFERENCES price_lists (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'rate_components_set_org_fk'
    ) THEN
        ALTER TABLE rate_components
            ADD CONSTRAINT rate_components_set_org_fk
            FOREIGN KEY (organization_id, set_id)
            REFERENCES rate_component_sets (organization_id, id) ON DELETE CASCADE;
    END IF;
END $$;

-- ===========================================================================
-- 2. One publication per list per day, per ORGANIZATION
-- ===========================================================================
--
-- `rate_component_sets_list_day_uk` was `(price_list_id, effective_from)`.
-- With section 1 in place a list id can no longer arrive from another
-- organization, so this is now defence in depth rather than the primary
-- control — but it is the shape the sibling index
-- `rate_component_sets_org_default_day_uk` already has, and a uniqueness key
-- on a multi-tenant table that omits the tenant is exactly the kind of thing
-- that becomes load-bearing again the day somebody relaxes something else.

DROP INDEX IF EXISTS rate_component_sets_list_day_uk;
CREATE UNIQUE INDEX IF NOT EXISTS rate_component_sets_list_day_uk
    ON rate_component_sets (organization_id, price_list_id, effective_from)
    WHERE price_list_id IS NOT NULL;

-- ===========================================================================
-- 3. Component codes are case-insensitively unique, as the domain says
-- ===========================================================================
--
-- `Commerce.Domain.Pricing.RateComponentSet` rejects duplicate codes with
-- `OrdinalIgnoreCase`; `0013`'s `UNIQUE (set_id, code)` was case-SENSITIVE.
-- The two disagreeing is worse than either rule alone. An `IVA`/`iva` pair
-- written by any path other than the domain constructor persisted without
-- complaint, and from that moment EVERY read of that set threw while
-- rebuilding the aggregate through its constructor — the components became
-- permanently unreadable, and unrepairable too, since `app_runtime` holds no
-- UPDATE and no DELETE on these tables.
--
-- Aligned toward the STRICTER side deliberately. Loosening the domain to
-- ordinal comparison would have matched the two rules just as well, but it
-- would leave `IVA` and `iva` composing side by side in one set — two rows
-- an admin reads as the same rate.

DROP INDEX IF EXISTS rate_components_one_code_per_set_ci;
ALTER TABLE rate_components DROP CONSTRAINT IF EXISTS rate_components_one_code_per_set;
CREATE UNIQUE INDEX IF NOT EXISTS rate_components_one_code_per_set_ci
    ON rate_components (set_id, lower(code));

-- commerce-organization-persistence: 0015_organization_branding.sql,
-- appended verbatim per the hand-kept mirror convention. Optional web
-- branding on organizations (T5, "lo mas simple posible, a futuro
-- ampliamos"): logo URL + primary color, nothing else.

ALTER TABLE organizations
    ADD COLUMN IF NOT EXISTS logo_url      text NULL,
    ADD COLUMN IF NOT EXISTS primary_color text NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'organizations_logo_url_length_ck'
    ) THEN
        ALTER TABLE organizations
            ADD CONSTRAINT organizations_logo_url_length_ck
            CHECK (logo_url IS NULL OR char_length(logo_url) <= 2048);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'organizations_primary_color_format_ck'
    ) THEN
        ALTER TABLE organizations
            ADD CONSTRAINT organizations_primary_color_format_ck
            CHECK (primary_color IS NULL OR primary_color ~ '^#[0-9a-fA-F]{6}$');
    END IF;
END $$;

-- B7 U4 (organization-persistence, catalog-item-identification): 0016_catalog_branch_ownership.sql,
-- appended verbatim per the hand-kept mirror convention. Products and
-- presentations become branch-owned: backfill into each organization's
-- earliest branch (creating "Main" first if none exists), branch_id NOT
-- NULL with a tenant-composite FK onto branches, identification-code
-- uniqueness moves from per-organization to per-branch, and RLS on both
-- tables now requires organization_id AND branch_id to match (fail-closed
-- when no branch is selected).

-- ===========================================================================
-- 1. The referenced key: branches (organization_id, id)
-- ===========================================================================
--
-- Same shape as 0014's `price_lists_org_scoped_uk` / VAR
-- `rate_component_sets_org_scoped_uk`: redundant with `branches`' own
-- primary key (`id`) by design — a composite foreign key needs a unique
-- constraint over exactly its target columns.

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk'
    ) THEN
        ALTER TABLE branches
            ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

-- ===========================================================================
-- 2. New nullable column, backfilled, then NOT NULL
-- ===========================================================================
--
-- design.md / spec "The migration that adds branch_id MUST backfill every
-- existing row into its organization's earliest-created branch, and for an
-- organization that owns rows but has no branch MUST first create a branch
-- named 'Main'."

ALTER TABLE products      ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE presentations ADD COLUMN IF NOT EXISTS branch_id uuid NULL;

-- 2a. Any organization that owns a product or presentation but has NO
-- branch at all gets one, named "Main", before backfill runs — otherwise
-- the backfill below would have nothing to point those rows at.
INSERT INTO branches (id, organization_id, name)
SELECT gen_random_uuid(), missing.organization_id, 'Main'
FROM (
    SELECT DISTINCT organization_id FROM products
    UNION
    SELECT DISTINCT organization_id FROM presentations
) AS missing
WHERE NOT EXISTS (
    SELECT 1 FROM branches b WHERE b.organization_id = missing.organization_id
);

-- 2b. Backfill every existing row into its organization's EARLIEST-CREATED
-- branch (ties broken by id for determinism).
UPDATE products p
SET branch_id = earliest.id
FROM (
    SELECT DISTINCT ON (organization_id) organization_id, id
    FROM branches
    ORDER BY organization_id, created_at, id
) AS earliest
WHERE earliest.organization_id = p.organization_id
  AND p.branch_id IS NULL;

UPDATE presentations pr
SET branch_id = earliest.id
FROM (
    SELECT DISTINCT ON (organization_id) organization_id, id
    FROM branches
    ORDER BY organization_id, created_at, id
) AS earliest
WHERE earliest.organization_id = pr.organization_id
  AND pr.branch_id IS NULL;

ALTER TABLE products      ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE presentations ALTER COLUMN branch_id SET NOT NULL;

-- ===========================================================================
-- 3. Tenant-composite references onto branches (organization_id, id)
-- ===========================================================================
--
-- Same rationale as 0014 section 1: RLS filters SELECT, not a foreign-key
-- check, so a plain `branch_id uuid REFERENCES branches (id)` would let a
-- row scoped to organization A reference organization B's branch. Making
-- the tenant part of the key closes that the same way 0014 closed it for
-- price lists / rate component sets.

ALTER TABLE products      DROP CONSTRAINT IF EXISTS products_branch_id_fkey;
ALTER TABLE presentations DROP CONSTRAINT IF EXISTS presentations_branch_id_fkey;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'products_branch_org_fk'
    ) THEN
        ALTER TABLE products
            ADD CONSTRAINT products_branch_org_fk
            FOREIGN KEY (organization_id, branch_id)
            REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'presentations_branch_org_fk'
    ) THEN
        ALTER TABLE presentations
            ADD CONSTRAINT presentations_branch_org_fk
            FOREIGN KEY (organization_id, branch_id)
            REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS products_branch_idx      ON products (branch_id);
CREATE INDEX IF NOT EXISTS presentations_branch_idx ON presentations (branch_id);

-- ===========================================================================
-- 4. Identification-code uniqueness becomes PER BRANCH
-- ===========================================================================
--
-- catalog-item-identification spec "Branch-Owned Catalog": "the same code
-- MAY exist in two branches of one organization." The old org-scoped
-- partial unique index would have refused that; replaced with a
-- branch-scoped one, same partial shape (an unlabelled presentation stays
-- unconstrained).

DROP INDEX IF EXISTS presentations_org_code_uk;
CREATE UNIQUE INDEX IF NOT EXISTS presentations_org_branch_code_uk
    ON presentations (organization_id, branch_id, identification_code)
    WHERE identification_code IS NOT NULL;

-- ===========================================================================
-- 5. Row-level security: require BOTH organization AND branch to match
-- ===========================================================================
--
-- organization-persistence spec "Branch-Owned Business Data": "Row-level
-- security on each branch-owned table MUST require both organization_id
-- and branch_id to match the transaction's scoped organization and
-- branch, ... so a transaction without a scoped branch reads and writes
-- nothing (fail-closed)." `NULLIF(current_setting('app.current_branch_id',
-- true), '')::uuid` is NULL when no branch is selected, and `branch_id =
-- NULL` is never true for any real row — exactly the fail-closed shape the
-- spec calls for, with no separate "no branch selected" case to write.

DROP POLICY IF EXISTS products_tenant_isolation ON products;
CREATE POLICY products_tenant_isolation ON products
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS presentations_tenant_isolation ON presentations;
CREATE POLICY presentations_tenant_isolation ON presentations
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

-- B7 U5 (organization-persistence, price-list-management, supplier-price-import):
-- 0017_pricing_branch_ownership.sql, appended verbatim per the hand-kept mirror
-- convention. price_lists, price_list_entries, rate_component_sets,
-- rate_components, supplier_price_mappings, price_import_batches and
-- price_import_rows become branch-owned: backfill into each organization's
-- earliest branch (creating "Main" first if none exists), branch_id NOT NULL
-- with a tenant-composite FK onto branches, entries/rate sets carry a
-- same-branch FK onto their price list (and, for entries, their
-- presentation), one-default-price-list/supplier-mapping-name uniqueness
-- moves to per-branch, and RLS on all seven tables now requires
-- organization_id AND branch_id to match (fail-closed when no branch is
-- selected).

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- B7 U5 (organization-persistence spec "Branch-Owned Business Data",
-- price-list-management spec "Branch-Owned Price Lists", supplier-price-import
-- spec "Branch-Owned Supplier Mappings And Imports"): price_lists,
-- price_list_entries, rate_component_sets, rate_components,
-- supplier_price_mappings, price_import_batches and price_import_rows move
-- from organization-owned to branch-owned — the same shape 0016 gave
-- products/presentations.
--
-- APPLIED AFTER: 0016_catalog_branch_ownership.sql. Touches `price_lists`,
-- `price_list_entries` (0009), `rate_component_sets`, `rate_components`
-- (0013/0014), `supplier_price_mappings`, `price_import_batches`,
-- `price_import_rows` (0009 Part C) additively: new column, new/replaced
-- indexes, replaced RLS policy. Also adds a composite unique key on
-- `presentations (branch_id, id)` (from 0016) so `price_list_entries` can
-- carry a tenant+branch-composite reference onto it, mirroring 0014's
-- `price_lists_org_scoped_uk` pattern one level down.
--
-- INVERSE (rollback), shipped as comments — NOT executed by this file:
--   ALTER TABLE price_import_rows       DROP CONSTRAINT price_import_rows_branch_org_fk;
--   ALTER TABLE price_import_batches    DROP CONSTRAINT price_import_batches_branch_org_fk;
--   ALTER TABLE price_import_batches    DROP CONSTRAINT price_import_batches_mapping_branch_fk;
--   ALTER TABLE supplier_price_mappings DROP CONSTRAINT supplier_price_mappings_branch_org_fk;
--   ALTER TABLE rate_components         DROP CONSTRAINT rate_components_branch_org_fk;
--   ALTER TABLE rate_components         DROP CONSTRAINT rate_components_set_branch_fk;
--   ALTER TABLE rate_component_sets     DROP CONSTRAINT rate_component_sets_branch_org_fk;
--   ALTER TABLE rate_component_sets     DROP CONSTRAINT rate_component_sets_price_list_branch_fk;
--   ALTER TABLE price_list_entries      DROP CONSTRAINT price_list_entries_branch_org_fk;
--   ALTER TABLE price_list_entries      DROP CONSTRAINT price_list_entries_price_list_branch_fk;
--   ALTER TABLE price_list_entries      DROP CONSTRAINT price_list_entries_presentation_branch_fk;
--   ALTER TABLE price_lists             DROP CONSTRAINT price_lists_branch_org_fk;
--   -- drop every branch_id NOT NULL / DROP COLUMN, restore the pre-0017
--   -- indexes and RLS policies shown in 0009/0013/0014's own files --
-- Rolling back is lossy the moment two branches of one organization hold
-- pricing rows: prefer forward-fix after first real multi-branch use, same
-- caveat as 0016.
-- ATOMIC (0006's precedent): no dropped FKs / NOT NULL / old RLS half-applied.

BEGIN;

-- ===========================================================================
-- 1. Referenced keys: presentations (branch_id, id), needed as an FK target
-- ===========================================================================

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'presentations_branch_scoped_uk'
    ) THEN
        ALTER TABLE presentations
            ADD CONSTRAINT presentations_branch_scoped_uk UNIQUE (branch_id, id);
    END IF;
END $$;

-- ===========================================================================
-- 2. New nullable columns, backfilled, then NOT NULL
-- ===========================================================================
--
-- Same two-step shape as 0016: any organization that owns a pricing row but
-- has NO branch at all gets one named "Main" first, then every row backfills
-- into its organization's EARLIEST-CREATED branch.

ALTER TABLE price_lists             ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE price_list_entries      ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE rate_component_sets     ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE rate_components         ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE supplier_price_mappings ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE price_import_batches    ADD COLUMN IF NOT EXISTS branch_id uuid NULL;
ALTER TABLE price_import_rows       ADD COLUMN IF NOT EXISTS branch_id uuid NULL;

INSERT INTO branches (id, organization_id, name)
SELECT gen_random_uuid(), missing.organization_id, 'Main'
FROM (
    SELECT DISTINCT organization_id FROM price_lists
    UNION SELECT DISTINCT organization_id FROM price_list_entries
    UNION SELECT DISTINCT organization_id FROM rate_component_sets
    UNION SELECT DISTINCT organization_id FROM rate_components
    UNION SELECT DISTINCT organization_id FROM supplier_price_mappings
    UNION SELECT DISTINCT organization_id FROM price_import_batches
    UNION SELECT DISTINCT organization_id FROM price_import_rows
) AS missing
WHERE NOT EXISTS (
    SELECT 1 FROM branches b WHERE b.organization_id = missing.organization_id
);

UPDATE price_lists t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

-- Entries take THEIR PRESENTATION's branch (0016 already made it
-- branch-owned), not the list's earliest-branch guess above.
UPDATE price_list_entries t
SET branch_id = pres.branch_id
FROM presentations pres
WHERE pres.id = t.presentation_id AND t.branch_id IS NULL;

-- Mismatched entries split onto a per-branch COPY of their list (one per
-- source-list/target-branch pair); a rerun sees zero mismatches (no-op).
CREATE TEMP TABLE pricing_branch_copies ON COMMIT DROP AS
SELECT gen_random_uuid() AS new_id, pl.id AS source_id, need.branch_id,
       pl.organization_id, pl.name, pl.is_default AS source_default,
       pl.created_at_utc, pl.created_by_user_id
FROM (SELECT DISTINCT e.price_list_id, e.branch_id
      FROM price_list_entries e
      JOIN price_lists src ON src.id = e.price_list_id
      WHERE e.branch_id <> src.branch_id) AS need
JOIN price_lists pl ON pl.id = need.price_list_id;

-- Section 5's per-branch default index, moved up: the old org-wide one would
-- refuse a 2nd default row in another branch.
DROP INDEX IF EXISTS price_lists_one_default;
CREATE UNIQUE INDEX IF NOT EXISTS price_lists_one_default_per_branch
    ON price_lists (organization_id, branch_id) WHERE is_default;

INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_at_utc, created_by_user_id)
SELECT c.new_id, c.organization_id, c.branch_id, c.name,
       c.source_default AND NOT EXISTS (
           SELECT 1 FROM price_lists d
           WHERE d.organization_id = c.organization_id AND d.branch_id = c.branch_id AND d.is_default),
       c.created_at_utc, c.created_by_user_id
FROM pricing_branch_copies c;

UPDATE price_list_entries e
SET price_list_id = c.new_id
FROM pricing_branch_copies c
WHERE c.source_id = e.price_list_id AND c.branch_id = e.branch_id;

UPDATE rate_component_sets t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

UPDATE rate_components t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

UPDATE supplier_price_mappings t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

UPDATE price_import_batches t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

UPDATE price_import_rows t
SET branch_id = earliest.id
FROM (SELECT DISTINCT ON (organization_id) organization_id, id FROM branches ORDER BY organization_id, created_at, id) AS earliest
WHERE earliest.organization_id = t.organization_id AND t.branch_id IS NULL;

ALTER TABLE price_lists             ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE price_list_entries      ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE rate_component_sets     ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE rate_components         ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE supplier_price_mappings ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE price_import_batches    ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE price_import_rows       ALTER COLUMN branch_id SET NOT NULL;

-- ===========================================================================
-- 3. Tenant-composite references onto branches (organization_id, id)
-- ===========================================================================

CREATE INDEX IF NOT EXISTS price_lists_branch_idx             ON price_lists (branch_id);
CREATE INDEX IF NOT EXISTS price_list_entries_branch_idx      ON price_list_entries (branch_id);
CREATE INDEX IF NOT EXISTS rate_component_sets_branch_idx     ON rate_component_sets (branch_id);
CREATE INDEX IF NOT EXISTS rate_components_branch_idx         ON rate_components (branch_id);
CREATE INDEX IF NOT EXISTS supplier_price_mappings_branch_idx ON supplier_price_mappings (branch_id);
CREATE INDEX IF NOT EXISTS price_import_batches_branch_idx    ON price_import_batches (branch_id);
CREATE INDEX IF NOT EXISTS price_import_rows_branch_idx       ON price_import_rows (branch_id);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_lists_branch_org_fk') THEN
        ALTER TABLE price_lists
            ADD CONSTRAINT price_lists_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_component_sets_branch_org_fk') THEN
        ALTER TABLE rate_component_sets
            ADD CONSTRAINT rate_component_sets_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_components_branch_org_fk') THEN
        ALTER TABLE rate_components
            ADD CONSTRAINT rate_components_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'supplier_price_mappings_branch_org_fk') THEN
        ALTER TABLE supplier_price_mappings
            ADD CONSTRAINT supplier_price_mappings_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_import_batches_branch_org_fk') THEN
        ALTER TABLE price_import_batches
            ADD CONSTRAINT price_import_batches_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_import_rows_branch_org_fk') THEN
        ALTER TABLE price_import_rows
            ADD CONSTRAINT price_import_rows_branch_org_fk
            FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE;
    END IF;
END $$;

-- price_list_entries carries NO organization+branch FK to `branches` of its
-- own: its `(branch_id, price_list_id)` reference below already pins it to a
-- price list that itself references `branches (organization_id, id)`, and a
-- second direct reference would be redundant, not additionally safe.

-- ===========================================================================
-- 4. Same-branch consistency: entries and rate sets reference a price list
--    (and, for entries, a presentation) of the SAME branch
-- ===========================================================================
--
-- price-list-management spec "Branch-Owned Price Lists": "A price entry or
-- rate component set MUST reference a price list and presentation of the
-- same branch." Same technique as section 3: make the branch part of the
-- foreign key so the database refuses a cross-branch reference outright.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_lists_branch_scoped_uk') THEN
        ALTER TABLE price_lists ADD CONSTRAINT price_lists_branch_scoped_uk UNIQUE (branch_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_component_sets_branch_scoped_uk') THEN
        ALTER TABLE rate_component_sets ADD CONSTRAINT rate_component_sets_branch_scoped_uk UNIQUE (branch_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'supplier_price_mappings_branch_scoped_uk') THEN
        ALTER TABLE supplier_price_mappings ADD CONSTRAINT supplier_price_mappings_branch_scoped_uk UNIQUE (branch_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_import_batches_branch_scoped_uk') THEN
        ALTER TABLE price_import_batches ADD CONSTRAINT price_import_batches_branch_scoped_uk UNIQUE (branch_id, id);
    END IF;
END $$;

ALTER TABLE price_list_entries  DROP CONSTRAINT IF EXISTS price_list_entries_price_list_id_fkey;
ALTER TABLE price_list_entries  DROP CONSTRAINT IF EXISTS price_list_entries_presentation_id_fkey;
ALTER TABLE rate_component_sets DROP CONSTRAINT IF EXISTS rate_component_sets_price_list_org_fk;
ALTER TABLE rate_components     DROP CONSTRAINT IF EXISTS rate_components_set_org_fk;
ALTER TABLE price_import_batches DROP CONSTRAINT IF EXISTS price_import_batches_supplier_mapping_id_fkey;
ALTER TABLE price_import_rows    DROP CONSTRAINT IF EXISTS price_import_rows_batch_id_fkey;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_list_entries_price_list_branch_fk') THEN
        ALTER TABLE price_list_entries
            ADD CONSTRAINT price_list_entries_price_list_branch_fk
            FOREIGN KEY (branch_id, price_list_id) REFERENCES price_lists (branch_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_list_entries_presentation_branch_fk') THEN
        ALTER TABLE price_list_entries
            ADD CONSTRAINT price_list_entries_presentation_branch_fk
            FOREIGN KEY (branch_id, presentation_id) REFERENCES presentations (branch_id, id) ON DELETE CASCADE;
    END IF;

    -- MATCH SIMPLE (the default): price_list_id IS NULL for an
    -- organization/branch default set skips this check entirely, exactly
    -- like 0014's rate_component_sets_price_list_org_fk did for the
    -- organization-only shape.
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_component_sets_price_list_branch_fk') THEN
        ALTER TABLE rate_component_sets
            ADD CONSTRAINT rate_component_sets_price_list_branch_fk
            FOREIGN KEY (branch_id, price_list_id) REFERENCES price_lists (branch_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_components_set_branch_fk') THEN
        ALTER TABLE rate_components
            ADD CONSTRAINT rate_components_set_branch_fk
            FOREIGN KEY (branch_id, set_id) REFERENCES rate_component_sets (branch_id, id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_import_batches_mapping_branch_fk') THEN
        ALTER TABLE price_import_batches
            ADD CONSTRAINT price_import_batches_mapping_branch_fk
            FOREIGN KEY (branch_id, supplier_mapping_id) REFERENCES supplier_price_mappings (branch_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_import_rows_batch_branch_fk') THEN
        ALTER TABLE price_import_rows
            ADD CONSTRAINT price_import_rows_batch_branch_fk
            FOREIGN KEY (branch_id, batch_id) REFERENCES price_import_batches (branch_id, id) ON DELETE CASCADE;
    END IF;
END $$;

-- price_import_rows.presentation_id keeps its original single-column
-- `REFERENCES presentations (id) ON DELETE SET NULL` shape unchanged: a
-- composite `(branch_id, presentation_id)` target with `ON DELETE SET NULL`
-- would null out `branch_id` too, which is NOT NULL. The row's own
-- `branch_id` (matching its batch) already carries the tenant/branch scope
-- this table needs; the presentation match itself was computed by the
-- import endpoint through a branch-scoped catalog lookup at parse time.

-- ===========================================================================
-- 5. Uniqueness rules become PER BRANCH
-- ===========================================================================
--
-- price-list-management spec: "each branch MUST have at most one default
-- price list." supplier-price-import spec (unchanged text, same rule as
-- 0009's per-organization mapping name): now per branch.

-- price_lists' swap already ran in section 2 (must precede its INSERT).
DROP INDEX IF EXISTS supplier_price_mappings_org_name_uk;
CREATE UNIQUE INDEX IF NOT EXISTS supplier_price_mappings_branch_name_uk
    ON supplier_price_mappings (organization_id, branch_id, supplier_name);

-- rate_component_sets: "one publication per owner per day" becomes per
-- branch too — the list-owned index already implies the branch via the FK
-- added in section 4, but the branch is folded into the key itself for the
-- same defence-in-depth reason 0014 gave for the organization column.
DROP INDEX IF EXISTS rate_component_sets_list_day_uk;
CREATE UNIQUE INDEX IF NOT EXISTS rate_component_sets_list_day_uk
    ON rate_component_sets (organization_id, branch_id, price_list_id, effective_from)
    WHERE price_list_id IS NOT NULL;

DROP INDEX IF EXISTS rate_component_sets_org_default_day_uk;
CREATE UNIQUE INDEX IF NOT EXISTS rate_component_sets_branch_default_day_uk
    ON rate_component_sets (organization_id, branch_id, effective_from)
    WHERE price_list_id IS NULL;

-- ===========================================================================
-- 6. Row-level security: require BOTH organization_id AND branch_id
-- ===========================================================================

DROP POLICY IF EXISTS price_lists_tenant_isolation ON price_lists;
CREATE POLICY price_lists_tenant_isolation ON price_lists
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS price_list_entries_tenant_isolation ON price_list_entries;
CREATE POLICY price_list_entries_tenant_isolation ON price_list_entries
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS rate_component_sets_tenant_isolation ON rate_component_sets;
CREATE POLICY rate_component_sets_tenant_isolation ON rate_component_sets
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS rate_components_tenant_isolation ON rate_components;
CREATE POLICY rate_components_tenant_isolation ON rate_components
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS supplier_price_mappings_tenant_isolation ON supplier_price_mappings;
CREATE POLICY supplier_price_mappings_tenant_isolation ON supplier_price_mappings
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS price_import_batches_tenant_isolation ON price_import_batches;
CREATE POLICY price_import_batches_tenant_isolation ON price_import_batches
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS price_import_rows_tenant_isolation ON price_import_rows;
CREATE POLICY price_import_rows_tenant_isolation ON price_import_rows
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

COMMIT;

-- catalog-categories: 0018_catalog_categories.sql, appended verbatim per the hand-kept
-- mirror convention (organization-owned categories, backfill, same-org FK).

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- catalog-categories spec: product categories become a real,
-- ORGANIZATION-owned table (shared by every branch of the organization),
-- each with a name and an icon key from a fixed set. Until now
-- `products.category_id` (0009) was a bare uuid that referenced nothing, so
-- no category name existed anywhere and the POS had nothing to replicate.
--
-- APPLIED AFTER: 0017_pricing_branch_ownership.sql. Adds `categories`, then
-- rewrites `products.category_id` (0009/0016) and constrains it.
--
-- Products stay BRANCH-owned (0016); categories are ORGANIZATION-owned, so
-- the policy on `categories` compares only `organization_id` (fail-closed
-- when no organization is scoped) — the same shape as `customers` (0008).
--
-- The whole file runs in ONE transaction and can be re-run safely: the table
-- and indexes use IF NOT EXISTS, the backfill only touches organizations and
-- products that still lack a real category, and the foreign key is guarded.
--
-- INVERSE (rollback), shipped as comments — NOT executed by this file:
--   ALTER TABLE products DROP CONSTRAINT products_category_org_fk;
--   DROP TABLE categories;
-- The rewritten `products.category_id` values are not restored (they held
-- meaningless ids that referenced nothing).

BEGIN;

-- ===========================================================================
-- 1. The categories table
-- ===========================================================================
--
-- `icon_key` is the POS/web icon vocabulary; the CHECK keeps it a closed set
-- at the database level. Adding a key later is a forward migration that
-- replaces this constraint.

CREATE TABLE IF NOT EXISTS categories (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    icon_key        text NOT NULL CHECK (icon_key IN (
        'meat', 'poultry', 'fish', 'wine', 'drinks', 'charcoal',
        'grocery', 'cleaning', 'bakery', 'dairy', 'produce', 'generic')),
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    -- Redundant with the primary key on purpose: a composite foreign key from
    -- `products (organization_id, category_id)` needs a unique constraint over
    -- exactly its target columns (same shape as 0014/0016).
    CONSTRAINT categories_org_scoped_uk UNIQUE (organization_id, id)
);

-- Unique name per organization, ignoring case and surrounding whitespace.
CREATE UNIQUE INDEX IF NOT EXISTS categories_org_name_uk
    ON categories (organization_id, lower(btrim(name)));

CREATE INDEX IF NOT EXISTS categories_org_updated ON categories (organization_id, updated_at_utc);

ALTER TABLE categories ENABLE ROW LEVEL SECURITY;
ALTER TABLE categories FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON categories FROM PUBLIC;
-- DELETE is granted (unlike most catalog tables) because an admin can remove
-- an unused category; the foreign key below refuses removal while any
-- product still references it.
GRANT SELECT, INSERT, UPDATE, DELETE ON categories TO app_runtime;

DROP POLICY IF EXISTS categories_tenant_isolation ON categories;
CREATE POLICY categories_tenant_isolation ON categories
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 2. Backfill: one "Sin categoría" per organization that owns products
-- ===========================================================================

INSERT INTO categories (id, organization_id, name, icon_key)
SELECT gen_random_uuid(), owners.organization_id, 'Sin categoría', 'generic'
FROM (SELECT DISTINCT organization_id FROM products) AS owners
WHERE NOT EXISTS (
    SELECT 1 FROM categories c
    WHERE c.organization_id = owners.organization_id
      AND lower(btrim(c.name)) = lower('Sin categoría')
);

-- Point every product whose category_id does not reference a real category of
-- its own organization at that organization's default category, and mark it
-- changed so devices re-sync it with its category.
UPDATE products p
SET category_id = c.id, updated_at_utc = now()
FROM categories c
WHERE c.organization_id = p.organization_id
  AND lower(btrim(c.name)) = lower('Sin categoría')
  AND NOT EXISTS (
      SELECT 1 FROM categories own
      WHERE own.organization_id = p.organization_id AND own.id = p.category_id
  );

-- ===========================================================================
-- 3. Same-organization foreign key from products
-- ===========================================================================
--
-- RLS filters reads, not foreign-key checks, so a plain `REFERENCES
-- categories (id)` would let a product reference another organization's
-- category. Making the organization part of the key closes that (0014/0016).
-- RESTRICT: a category that any product in ANY branch still uses cannot be
-- deleted (RI checks are not subject to RLS).

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'products_category_org_fk'
    ) THEN
        ALTER TABLE products
            ADD CONSTRAINT products_category_org_fk
            FOREIGN KEY (organization_id, category_id)
            REFERENCES categories (organization_id, id) ON DELETE RESTRICT;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS products_category_idx ON products (category_id);

COMMIT;

-- branch-discount-pin: 0019_branch_discount_pin.sql, appended verbatim per the hand-kept
-- mirror convention (branch-owned discount PIN verifier, org+branch RLS).

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- branch-discount-pin spec: each branch may have ONE shared discount PIN that
-- authorizes POS discounts. Only a slow salted hash is stored (PBKDF2-SHA256,
-- parameters kept beside the hash so they can be strengthened later), with a
-- monotonically increasing version and the time/actor of the last rotation.
-- The PIN itself is never stored.
--
-- APPLIED AFTER: 0018_catalog_categories.sql.
--
-- BRANCH-owned data, so the policy compares BOTH the organization and the
-- selected branch and fails closed when either is unset (same shape as
-- `products` in 0016): a scope on another branch, another organization, or no
-- branch at all sees no row.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment — NOT executed by this file:
--   DROP TABLE branch_discount_pins;

BEGIN;

CREATE TABLE IF NOT EXISTS branch_discount_pins (
    branch_id       uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    algorithm       text NOT NULL CHECK (algorithm = 'pbkdf2-sha256'),
    iterations      integer NOT NULL CHECK (iterations >= 100000),
    salt            bytea NOT NULL CHECK (octet_length(salt) >= 16),
    pin_hash        bytea NOT NULL CHECK (octet_length(pin_hash) >= 16),
    version         bigint NOT NULL CHECK (version >= 1),
    rotated_at_utc  timestamptz NOT NULL DEFAULT now(),
    rotated_by      uuid NOT NULL,
    -- Tenant-composite reference (0014/0016): a plain branch_id reference would
    -- let a row point at another organization's branch, because RLS filters
    -- reads but not foreign-key checks.
    CONSTRAINT branch_discount_pins_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

ALTER TABLE branch_discount_pins ENABLE ROW LEVEL SECURITY;
ALTER TABLE branch_discount_pins FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON branch_discount_pins FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON branch_discount_pins TO app_runtime;

DROP POLICY IF EXISTS branch_discount_pins_tenant_isolation ON branch_discount_pins;
CREATE POLICY branch_discount_pins_tenant_isolation ON branch_discount_pins
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id    = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

COMMIT;

-- branch-codes: 0021_branch_codes.sql, appended verbatim per the hand-kept
-- mirror convention (per-organization short branch code, allocation trigger).

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- organization-persistence "Branch Short Code": every branch carries a short,
-- numeric, per-organization code (1, 2, 3 ... 999) that the SERVER assigns at
-- creation and that never changes afterwards. It is the `{branch}` part of the
-- human document numbers (`V01-C2-125`), so changing one would orphan printed
-- and reported numbers. APPLIED AFTER: 0020_operate_pos_permission.sql.
--
-- Allocation has ONE source of truth: the BEFORE INSERT trigger below. When an
-- INSERT leaves `code` NULL it takes a transaction-scoped advisory lock keyed on
-- the organization, then assigns MAX(code)+1. The lock serializes concurrent
-- creations for the SAME organization only (different organizations hash to
-- different 64-bit keys; should two ever collide, the only effect is some extra,
-- harmless serialization, never a wrong code), and it is released at COMMIT/ROLLBACK, so the next creator
-- always sees the previous one's committed row (READ COMMITTED takes a fresh
-- snapshot per statement). A gap left by a rolled-back creation is reused,
-- because the MAX is over committed rows; branches are never deleted, so a
-- committed code is never reissued. Raw-SQL inserts that omit `code` therefore
-- keep working, and the application needs no allocation logic of its own.
--
-- `branches` has `FORCE ROW LEVEL SECURITY` (0003): with no
-- `app.current_org_id` even the owner sees ZERO rows, so the backfill would
-- "succeed" while touching nothing (same hazard 0006/0020 document). FORCE is
-- switched off for the owner ONLY inside this one transaction; the
-- post-condition aborts the whole transaction if any branch is still without a
-- code, and FORCE is restored before COMMIT.
--
-- Backfill numbers existing branches per organization in `created_at, id`
-- order starting after any code already present, so a re-run is a no-op.
--
-- INVERSE (rollback), shipped as a comment — NOT executed by this file:
--   BEGIN;
--   DROP TRIGGER IF EXISTS branches_code_immutable ON branches;
--   DROP TRIGGER IF EXISTS branches_code_allocate ON branches;
--   DROP FUNCTION IF EXISTS branches_reject_code_change();
--   DROP FUNCTION IF EXISTS branches_allocate_code();
--   ALTER TABLE branches DROP COLUMN code;
--   COMMIT;

BEGIN;

ALTER TABLE branches ADD COLUMN IF NOT EXISTS code smallint;

CREATE OR REPLACE FUNCTION branches_allocate_code() RETURNS trigger AS $$
BEGIN
    IF NEW.code IS NULL THEN
        PERFORM pg_advisory_xact_lock(hashtextextended(NEW.organization_id::text, 0));
        SELECT COALESCE(MAX(code), 0) + 1 INTO NEW.code
          FROM branches
         WHERE organization_id = NEW.organization_id;
    END IF;
    RETURN NEW;
END
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION branches_reject_code_change() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'branches.code is immutable once assigned'
        USING ERRCODE = 'integrity_constraint_violation';
END
$$ LANGUAGE plpgsql;

ALTER TABLE branches NO FORCE ROW LEVEL SECURITY;

WITH numbered AS (
    SELECT b.id,
           row_number() OVER (PARTITION BY b.organization_id ORDER BY b.created_at, b.id)
           + COALESCE((SELECT MAX(x.code) FROM branches x WHERE x.organization_id = b.organization_id), 0) AS next_code
      FROM branches b
     WHERE b.code IS NULL)
UPDATE branches b
   SET code = n.next_code
  FROM numbered n
 WHERE b.id = n.id;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM branches WHERE code IS NULL) THEN
        RAISE EXCEPTION '0021: a branch survived without a code';
    END IF;
END
$$;

ALTER TABLE branches FORCE ROW LEVEL SECURITY;

ALTER TABLE branches ALTER COLUMN code SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_code_range_ck') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_code_range_ck CHECK (code BETWEEN 1 AND 999);
    END IF;
END
$$;

CREATE UNIQUE INDEX IF NOT EXISTS branches_org_code_uk ON branches (organization_id, code);

DROP TRIGGER IF EXISTS branches_code_allocate ON branches;
CREATE TRIGGER branches_code_allocate
    BEFORE INSERT ON branches
    FOR EACH ROW EXECUTE FUNCTION branches_allocate_code();

DROP TRIGGER IF EXISTS branches_code_immutable ON branches;
CREATE TRIGGER branches_code_immutable
    BEFORE UPDATE ON branches
    FOR EACH ROW WHEN (OLD.code IS DISTINCT FROM NEW.code)
    EXECUTE FUNCTION branches_reject_code_change();

COMMIT;

-- terminal-registers: 0022_terminal_registers.sql, appended verbatim per the hand-kept
-- mirror convention (per-branch register numbers, never reused).

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- pos-installation-identity "Register Number": every paired POS terminal
-- (installation) carries a small numeric register number, unique within its
-- branch, assigned by the SERVER at pairing and kept stable. It is the
-- `C{register}` part of the human sale numbers (`V01-C2-125`).
-- APPLIED AFTER: 0021_branch_codes.sql (and 0004_device_credentials.sql).
--
-- NUMBERS ARE NEVER REUSED. Sale numbers are generated OFFLINE by each
-- terminal from (branch, register, local sequence). If a freed register number
-- were handed to a different installation, two machines could produce the same
-- `V01-C2-125`. So one row per (branch, installation) is kept FOREVER and a
-- release only stamps `released_at`; the next number of a branch is always
-- MAX(ever assigned)+1 over released and live rows alike. An installation that
-- comes back to a branch it already held gets ITS OWN old number again (safe:
-- the (branch, register) pair still names that single installation). The cost
-- is that a branch burns one number per distinct installation it ever
-- hosted, which is why the range is 1..999 and not 1..99: reinstalling a POS
-- creates a new installation, and a long-lived branch must not run dry after a
-- few reinstalls. The `C{n}` part of a sale number is not zero-padded, so the
-- width of the number is free.
--
-- Allocation has ONE source of truth, the function terminal_registers_assign():
--   1. serialize on the installation and on the branch (transaction-scoped
--      advisory locks, always taken in that order, pgbouncer-safe);
--   2. when p_release_others (pairing only), release the installation's live
--      register in any OTHER branch or organization (re-pairing to another
--      branch frees the old slot); the identity refresh passes false;
--   3. keep the live register of this branch, or re-activate the one the
--      installation held here before, or allocate MAX+1.
-- Exhaustion (number 1000) fails the CHECK `terminal_registers_number_ck`; the
-- API maps that to a typed error. The caller must already hold the tenant scope
-- (`app.current_org_id`) of p_organization_id.
--
-- RLS: the asymmetric `device_credentials` precedent (0004). A re-pairing
-- into organization B must release the organization A row while scoped to B, so
-- SELECT is unscoped and an UPDATE that RELEASES (sets released_at) is allowed
-- from any scope; INSERT and the re-activating UPDATE are pinned to the current
-- organization. A trigger makes the identity columns immutable so no UPDATE can
-- rewrite who owns a number.
--
-- Backfill: every installation with a LIVE (non-revoked) device credential and
-- no register yet gets one, per branch in `issued_at, installation_id` order
-- after any number already present, so a re-run changes nothing. The new table
-- has FORCE ROW LEVEL SECURITY, so FORCE is switched off for the owner ONLY
-- inside this transaction (otherwise the owner could not insert) and restored
-- before COMMIT; the post-condition aborts if a live installation has no row.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP FUNCTION IF EXISTS terminal_registers_assign(uuid, uuid, uuid, boolean);
--   DROP TABLE IF EXISTS terminal_registers;
--   DROP FUNCTION IF EXISTS terminal_registers_reject_identity_change();
--   COMMIT;

BEGIN;

-- Same guard 0016 installs (the composite foreign key needs a unique
-- constraint over exactly its target columns); a no-op where 0016 already ran.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS terminal_registers (
    organization_id uuid        NOT NULL,
    branch_id       uuid        NOT NULL,
    installation_id uuid        NOT NULL,
    register_number smallint    NOT NULL,
    assigned_at     timestamptz NOT NULL DEFAULT now(),
    released_at     timestamptz NULL,
    CONSTRAINT terminal_registers_pk PRIMARY KEY (organization_id, branch_id, register_number),
    CONSTRAINT terminal_registers_installation_uk UNIQUE (organization_id, branch_id, installation_id),
    CONSTRAINT terminal_registers_number_ck CHECK (register_number BETWEEN 1 AND 999),
    -- Tenant-composite reference (0014/0016): a plain branch_id reference would
    -- let a row point at another organization's branch.
    CONSTRAINT terminal_registers_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

-- One LIVE register per installation, across every branch and organization.
CREATE UNIQUE INDEX IF NOT EXISTS terminal_registers_live_installation_uk
    ON terminal_registers (installation_id) WHERE released_at IS NULL;

ALTER TABLE terminal_registers ENABLE ROW LEVEL SECURITY;
ALTER TABLE terminal_registers FORCE ROW LEVEL SECURITY;
REVOKE ALL ON terminal_registers FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON terminal_registers TO app_runtime;

DROP POLICY IF EXISTS terminal_registers_lookup ON terminal_registers;
CREATE POLICY terminal_registers_lookup ON terminal_registers
    FOR SELECT USING (true);

DROP POLICY IF EXISTS terminal_registers_insert ON terminal_registers;
CREATE POLICY terminal_registers_insert ON terminal_registers
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS terminal_registers_release ON terminal_registers;
CREATE POLICY terminal_registers_release ON terminal_registers
    FOR UPDATE USING (true) WITH CHECK (released_at IS NOT NULL);

DROP POLICY IF EXISTS terminal_registers_reactivate ON terminal_registers;
CREATE POLICY terminal_registers_reactivate ON terminal_registers
    FOR UPDATE
    USING (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

CREATE OR REPLACE FUNCTION terminal_registers_reject_identity_change() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'terminal_registers identity columns are immutable'
        USING ERRCODE = 'integrity_constraint_violation';
END
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS terminal_registers_identity_immutable ON terminal_registers;
CREATE TRIGGER terminal_registers_identity_immutable
    BEFORE UPDATE ON terminal_registers
    FOR EACH ROW WHEN (
        OLD.organization_id IS DISTINCT FROM NEW.organization_id
        OR OLD.branch_id IS DISTINCT FROM NEW.branch_id
        OR OLD.installation_id IS DISTINCT FROM NEW.installation_id
        OR OLD.register_number IS DISTINCT FROM NEW.register_number)
    EXECUTE FUNCTION terminal_registers_reject_identity_change();

-- An earlier draft of this migration had a 3-argument signature; drop it so a
-- database that ran the draft does not end up with two overloads (ambiguous calls).
DROP FUNCTION IF EXISTS terminal_registers_assign(uuid, uuid, uuid);
-- Also drop the 4-argument form: 0024 changes its result type, and CREATE OR
-- REPLACE cannot go back, so re-running this migration after 0024 (test
-- fixtures replay the whole chain) must start from nothing. 0024 re-creates it.
DROP FUNCTION IF EXISTS terminal_registers_assign(uuid, uuid, uuid, boolean);

CREATE OR REPLACE FUNCTION terminal_registers_assign(
    p_organization_id uuid, p_branch_id uuid, p_installation_id uuid, p_release_others boolean DEFAULT true)
RETURNS smallint AS $$
DECLARE
    v_number   smallint;
    v_released timestamptz;
    v_exists   boolean;
BEGIN
    -- The second argument of hashtextextended() is a fixed seed that gives each
    -- lock family its own key space: seed 1 = per-branch allocation (shared with
    -- no other family), seed 2 = per-installation. 0021 uses seed 0 for the
    -- per-organization branch-code lock. Taking installation then branch, always
    -- in this order, keeps two concurrent callers from deadlocking.
    PERFORM pg_advisory_xact_lock(hashtextextended(p_installation_id::text, 2));
    PERFORM pg_advisory_xact_lock(hashtextextended(p_branch_id::text, 1));

    -- The identity refresh (p_release_others = false) acts for a bearer
    -- credential checked at the START of the request. Re-verify, now that the
    -- installation is locked, that a live credential still binds this
    -- installation to this branch; a re-pairing that revoked it wins and the
    -- caller gets NULL (nothing written, nothing released).
    IF NOT p_release_others AND NOT EXISTS (
        SELECT 1 FROM device_credentials
         WHERE organization_id = p_organization_id
           AND branch_id = p_branch_id
           AND installation_id = p_installation_id
           AND NOT is_revoked) THEN
        RETURN NULL;
    END IF;

    SELECT register_number, released_at INTO v_number, v_released
      FROM terminal_registers
     WHERE organization_id = p_organization_id
       AND branch_id = p_branch_id
       AND installation_id = p_installation_id;
    v_exists := FOUND;

    -- Only the PAIRING releases other branches (p_release_others = true).
    -- `GET /device/identity` passes false: it only fills in a missing number for
    -- the branch its still-live credential names and never frees anything, so a
    -- slow identity call carrying an old credential cannot undo a re-pairing.
    IF p_release_others THEN
        UPDATE terminal_registers
           SET released_at = now()
         WHERE installation_id = p_installation_id
           AND released_at IS NULL
           AND NOT (organization_id = p_organization_id AND branch_id = p_branch_id);
    END IF;

    IF v_exists THEN
        IF v_released IS NOT NULL THEN
            UPDATE terminal_registers
               SET released_at = NULL
             WHERE organization_id = p_organization_id
               AND branch_id = p_branch_id
               AND installation_id = p_installation_id;
        END IF;
        RETURN v_number;
    END IF;

    INSERT INTO terminal_registers (organization_id, branch_id, installation_id, register_number)
    SELECT p_organization_id, p_branch_id, p_installation_id, COALESCE(MAX(register_number), 0) + 1
      FROM terminal_registers
     WHERE organization_id = p_organization_id
       AND branch_id = p_branch_id
    RETURNING register_number INTO v_number;
    RETURN v_number;
END
$$ LANGUAGE plpgsql;

REVOKE ALL ON FUNCTION terminal_registers_assign(uuid, uuid, uuid, boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION terminal_registers_assign(uuid, uuid, uuid, boolean) TO app_runtime;

ALTER TABLE terminal_registers NO FORCE ROW LEVEL SECURITY;

WITH live AS (
    SELECT DISTINCT ON (dc.installation_id)
           dc.organization_id, dc.branch_id, dc.installation_id, dc.issued_at
      FROM device_credentials dc
     WHERE NOT dc.is_revoked
       AND NOT EXISTS (SELECT 1 FROM terminal_registers r WHERE r.installation_id = dc.installation_id)
     ORDER BY dc.installation_id, dc.issued_at DESC),
numbered AS (
    SELECT l.organization_id, l.branch_id, l.installation_id, l.issued_at,
           row_number() OVER (PARTITION BY l.organization_id, l.branch_id ORDER BY l.issued_at, l.installation_id)
           + COALESCE((SELECT MAX(r.register_number) FROM terminal_registers r
                        WHERE r.organization_id = l.organization_id AND r.branch_id = l.branch_id), 0) AS register_number
      FROM live l)
INSERT INTO terminal_registers (organization_id, branch_id, installation_id, register_number, assigned_at)
SELECT organization_id, branch_id, installation_id, register_number, issued_at
  FROM numbered;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM device_credentials dc
         WHERE NOT dc.is_revoked
           AND NOT EXISTS (SELECT 1 FROM terminal_registers r WHERE r.installation_id = dc.installation_id)) THEN
        RAISE EXCEPTION '0022: a live terminal survived without a register';
    END IF;
END
$$;

ALTER TABLE terminal_registers FORCE ROW LEVEL SECURITY;

COMMIT;

-- pos-sales: 0023_pos_sales.sql, appended verbatim per the hand-kept
-- mirror convention (server projection of POS sales, unique human sale numbers).

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- pos-scan-sale "Sale Number" / branch-offline-sync "Sale Number Projection":
-- `pos_sales` is the server-side projection of POS sales. Until now a sale only
-- existed as a jsonb `sync_inbox` row; this table gives each sale a real row and,
-- above all, makes the human sale number `V{branch}-C{register}-{sequence}`
-- (`V01-C2-125`) UNIQUE per organization.
-- APPLIED AFTER: 0022_terminal_registers.sql.
--
-- The terminal numbers sales OFFLINE from its register and a local counter, so the
-- server can only verify, never assign. The ingestion transaction projects each
-- sale envelope here and validates the claimed number against the registry
-- (`terminal_registers`: that installation held that register in that branch) and
-- against `branches.code`. A number that fails validation or collides with another
-- sale is stored as NULL and an audit row `sale.number_conflict` records the claim:
-- the sale itself is ALWAYS ingested, a numbering problem never blocks the sync.
--
--   register_number / sale_sequence   NULL for sales made before numbering existed,
--                                     by a terminal that did not know its register,
--                                     or whose claim was rejected.
--   uniqueness                        partial UNIQUE index over (organization,
--                                     branch, register, sequence) WHERE the
--                                     sequence is not null, so unnumbered sales
--                                     never collide with each other.
--   total_amount                      plain numeric: the projection must never
--                                     fail on an amount the terminal accepted.
--
-- Append-only like `payment_entries` (0011): app_runtime gets SELECT, INSERT, no
-- UPDATE or DELETE; a sale is never rewritten. FORCE ROW LEVEL SECURITY with the
-- symmetric tenant-isolation policy (NULLIF pooler-safety hardening from 0001 -
-- never regress it). The composite foreign key to branches follows 0014/0016.
--
-- Deploy order: apply this BEFORE the API version that projects sales. An API
-- deployed ahead of it still ingests every sale (the projection runs in a
-- savepoint and is skipped with a log line), but sales received in that window have
-- no `pos_sales` row (they remain in `sync_inbox` and can be projected afterwards).
-- Prefer applying the migration first.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS pos_sales;
--   COMMIT;

BEGIN;

-- Same guard 0016/0022 install (the composite foreign key needs a unique
-- constraint over exactly its target columns); a no-op where they already ran.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS pos_sales (
    organization_id uuid        NOT NULL,
    branch_id       uuid        NOT NULL,
    sale_id         uuid        NOT NULL,
    register_number smallint    NULL,
    sale_sequence   integer     NULL,
    operation_id    uuid        NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    total_amount    numeric     NOT NULL,
    recorded_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pos_sales_pk PRIMARY KEY (organization_id, sale_id),
    -- Both parts or neither (a CHECK passes on NULL, so the pair is compared explicitly).
    CONSTRAINT pos_sales_number_ck CHECK (
        (register_number IS NULL) = (sale_sequence IS NULL)
        AND (register_number IS NULL OR (register_number BETWEEN 1 AND 999 AND sale_sequence >= 1))),
    CONSTRAINT pos_sales_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

-- A sale number is unique per organization and branch; unnumbered sales are exempt.
CREATE UNIQUE INDEX IF NOT EXISTS pos_sales_number_uk
    ON pos_sales (organization_id, branch_id, register_number, sale_sequence)
    WHERE sale_sequence IS NOT NULL;

CREATE INDEX IF NOT EXISTS pos_sales_branch_time_idx
    ON pos_sales (organization_id, branch_id, occurred_at_utc);

ALTER TABLE pos_sales ENABLE ROW LEVEL SECURITY;
ALTER TABLE pos_sales FORCE ROW LEVEL SECURITY;
REVOKE ALL ON pos_sales FROM PUBLIC;
GRANT SELECT, INSERT ON pos_sales TO app_runtime;   -- no UPDATE, no DELETE: append-only

DROP POLICY IF EXISTS pos_sales_tenant_isolation ON pos_sales;
CREATE POLICY pos_sales_tenant_isolation ON pos_sales
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;

-- terminal-registers-assign-result: 0024_terminal_registers_assign_result.sql, appended verbatim per the
-- hand-kept mirror convention (the assign function reports whether it allocated).

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- pos-installation-identity "Register Number": terminal_registers_assign()
-- now REPORTS whether it allocated a brand-new number, instead of the caller
-- inferring it with a second query (`assigned_at = now()`). The caller audits a
-- new allocation (`terminal.register.assigned`) only when `newly_allocated`.
-- APPLIED AFTER: 0022_terminal_registers.sql.
--
-- A function cannot change its result type with CREATE OR REPLACE, so the
-- 0022 signature (returns smallint) is dropped and re-created with two OUT
-- columns:
--   assigned_number  smallint - the register, or NULL when p_release_others is
--                    false and no live credential binds the installation to the
--                    branch (nothing written);
--   newly_allocated  boolean  - true ONLY when this call inserted the row;
--                    a live number or a re-activation reports false.
-- The OUT columns are deliberately not called `register_number`, which would be
-- ambiguous with the table column inside the body. Semantics, locks and grants
-- are otherwise exactly those of 0022. The dev/test databases already ran
-- 0022, which is why this is a new migration and not an edit of that file.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   re-run the terminal_registers_assign() definition of 0022 after
--   DROP FUNCTION IF EXISTS terminal_registers_assign(uuid, uuid, uuid, boolean);

BEGIN;

DROP FUNCTION IF EXISTS terminal_registers_assign(uuid, uuid, uuid, boolean);

CREATE FUNCTION terminal_registers_assign(
    p_organization_id uuid, p_branch_id uuid, p_installation_id uuid, p_release_others boolean DEFAULT true,
    OUT assigned_number smallint, OUT newly_allocated boolean)
AS $$
DECLARE
    v_released timestamptz;
    v_exists   boolean;
BEGIN
    newly_allocated := false;
    PERFORM pg_advisory_xact_lock(hashtextextended(p_installation_id::text, 2));
    PERFORM pg_advisory_xact_lock(hashtextextended(p_branch_id::text, 1));

    IF NOT p_release_others AND NOT EXISTS (
        SELECT 1 FROM device_credentials
         WHERE organization_id = p_organization_id
           AND branch_id = p_branch_id
           AND installation_id = p_installation_id
           AND NOT is_revoked) THEN
        assigned_number := NULL;
        RETURN;
    END IF;

    SELECT register_number, released_at INTO assigned_number, v_released
      FROM terminal_registers
     WHERE organization_id = p_organization_id
       AND branch_id = p_branch_id
       AND installation_id = p_installation_id;
    v_exists := FOUND;

    IF p_release_others THEN
        UPDATE terminal_registers
           SET released_at = now()
         WHERE installation_id = p_installation_id
           AND released_at IS NULL
           AND NOT (organization_id = p_organization_id AND branch_id = p_branch_id);
    END IF;

    IF v_exists THEN
        IF v_released IS NOT NULL THEN
            UPDATE terminal_registers
               SET released_at = NULL
             WHERE organization_id = p_organization_id
               AND branch_id = p_branch_id
               AND installation_id = p_installation_id;
        END IF;
        RETURN;
    END IF;

    INSERT INTO terminal_registers (organization_id, branch_id, installation_id, register_number)
    SELECT p_organization_id, p_branch_id, p_installation_id, COALESCE(MAX(register_number), 0) + 1
      FROM terminal_registers
     WHERE organization_id = p_organization_id
       AND branch_id = p_branch_id
    RETURNING register_number INTO assigned_number;
    newly_allocated := true;
END
$$ LANGUAGE plpgsql;

REVOKE ALL ON FUNCTION terminal_registers_assign(uuid, uuid, uuid, boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION terminal_registers_assign(uuid, uuid, uuid, boolean) TO app_runtime;

COMMIT;

-- persist-web-orders: 0025_orders.sql, appended verbatim per the hand-kept
-- mirror convention (web orders stored in Postgres, human order numbers).

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- persist-web-orders: web orders (registered customer and guest) used to live in an
-- in-memory dictionary of the API process and vanished on every restart. `orders` and
-- `order_lines` store them, and give every order the human number
-- `P{branch}-W-{sequence}` (`P01-W-37`): the branch code (frozen from `branches.code`,
-- which never changes) plus a sequence that counts the orders of that branch from 1.
-- APPLIED AFTER: 0021_branch_codes.sql (needs branches.code and branches_org_scoped_uk).
--
--   orders       one row per order: origin (Guest | RegisteredCustomer, text), the customer
--                id for a registered order, the guest contact for a guest order, the delivery
--                state (status, pending_reason, text like the domain enums), the number
--                (branch_code, sequence) and the submission time. Primary key
--                (organization_id, order_id): the business order id is idempotent PER
--                organization. UNIQUE (organization_id, destination_branch_id, sequence).
--   order_lines  the full OrderLineSnapshot of each line (names, quantity behavior, unit,
--                list price, discount, net price, total), frozen at submission.
--
-- Invariants the domain constructor enforces are repeated here as CHECK constraints so no
-- writer can bypass them: a registered order has a customer and no guest data, a guest order
-- has the guest contact (document, channel, address, name) and no customer.
--
-- The sequence is assigned by the application inside the INSERT transaction under a
-- transaction-scoped advisory lock keyed by the destination branch (seed 3; 1 is the
-- per-branch register lock and 2 the per-installation lock of 0022) as MAX(sequence)+1;
-- the UNIQUE constraint is the backstop. Orders are never deleted, so a committed number is
-- never reissued, and an idempotent resubmit returns the stored row without touching the counter.
--
-- Row level security: FORCE ROW LEVEL SECURITY with the symmetric tenant-isolation policy
-- (NULLIF pooler-safety hardening from 0001 - never regress it). app_runtime gets SELECT and
-- INSERT on both tables and UPDATE only on the delivery state of an order (status,
-- pending_reason); the identity, the lines and the number never change. No DELETE. The
-- composite foreign key to branches follows 0014/0016/0023.
--
-- Deploy order: apply this BEFORE the API version that stores orders. That API fails
-- /health/ready against a database without 0025 and never serves traffic half-migrated.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS order_lines;
--   DROP TABLE IF EXISTS orders;
--   COMMIT;

BEGIN;

-- Same guard 0016/0022/0023 install (the composite foreign key needs a unique
-- constraint over exactly its target columns); a no-op where they already ran.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS orders (
    organization_id       uuid        NOT NULL,
    order_id              uuid        NOT NULL,
    destination_branch_id uuid        NOT NULL,
    origin                text        NOT NULL,
    customer_id           uuid        NULL,
    guest_document_id     text        NULL,
    guest_channel         text        NULL,
    guest_contact_address text        NULL,
    guest_display_name    text        NULL,
    guest_delivery_notes  text        NULL,
    status                text        NOT NULL,
    pending_reason        text        NOT NULL,
    branch_code           smallint    NOT NULL,
    sequence              integer     NOT NULL,
    submitted_at_utc      timestamptz NOT NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT orders_pk PRIMARY KEY (organization_id, order_id),
    CONSTRAINT orders_number_uk UNIQUE (organization_id, destination_branch_id, sequence),
    CONSTRAINT orders_branch_fk
        FOREIGN KEY (organization_id, destination_branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT orders_number_ck CHECK (branch_code BETWEEN 1 AND 999 AND sequence >= 1),
    CONSTRAINT orders_origin_ck CHECK (origin IN ('Guest', 'RegisteredCustomer')),
    -- Order Construction Invariant: registered => customer, no guest data; guest => guest
    -- contact (all four mandatory parts), no customer.
    CONSTRAINT orders_origin_identity_ck CHECK (
        (origin = 'RegisteredCustomer'
            AND customer_id IS NOT NULL
            AND guest_document_id IS NULL AND guest_channel IS NULL AND guest_contact_address IS NULL
            AND guest_display_name IS NULL AND guest_delivery_notes IS NULL)
        OR
        (origin = 'Guest'
            AND customer_id IS NULL
            AND btrim(guest_document_id) <> '' AND guest_channel IS NOT NULL
            AND btrim(guest_contact_address) <> '' AND btrim(guest_display_name) <> '')),
    CONSTRAINT orders_status_ck CHECK (status IN ('PendingDestination', 'DestinationConfirmed')),
    CONSTRAINT orders_pending_reason_ck CHECK (pending_reason IN ('None', 'DestinationOffline', 'StockUnconfirmed')),
    -- A confirmed order is no longer pending for any reason.
    CONSTRAINT orders_confirmed_ck CHECK (status <> 'DestinationConfirmed' OR pending_reason = 'None')
);

CREATE INDEX IF NOT EXISTS orders_org_submitted_idx ON orders (organization_id, submitted_at_utc);

CREATE TABLE IF NOT EXISTS order_lines (
    organization_id             uuid    NOT NULL,
    order_id                    uuid    NOT NULL,
    line_no                     integer NOT NULL,
    product_id                  uuid    NOT NULL,
    product_name                text    NOT NULL,
    presentation_id             uuid    NOT NULL,
    presentation_name           text    NOT NULL,
    quantity_behavior           text    NOT NULL,
    unit_id                     uuid    NOT NULL,
    quantity                    numeric NOT NULL,
    unit_list_price             numeric NOT NULL,
    applied_discount_percentage numeric NOT NULL,
    unit_net_price              numeric NOT NULL,
    line_total                  numeric NOT NULL,
    CONSTRAINT order_lines_pk PRIMARY KEY (organization_id, order_id, line_no),
    CONSTRAINT order_lines_order_fk
        FOREIGN KEY (organization_id, order_id) REFERENCES orders (organization_id, order_id) ON DELETE CASCADE,
    CONSTRAINT order_lines_line_no_ck CHECK (line_no >= 1),
    CONSTRAINT order_lines_quantity_behavior_ck CHECK (quantity_behavior IN ('FixedQuantity', 'Weighted', 'Bulk'))
);

ALTER TABLE orders ENABLE ROW LEVEL SECURITY;
ALTER TABLE orders FORCE ROW LEVEL SECURITY;
REVOKE ALL ON orders FROM PUBLIC;
GRANT SELECT, INSERT ON orders TO app_runtime;
-- Only the delivery state evolves (MarkPending / MarkDestinationConfirmed).
GRANT UPDATE (status, pending_reason) ON orders TO app_runtime;

ALTER TABLE order_lines ENABLE ROW LEVEL SECURITY;
ALTER TABLE order_lines FORCE ROW LEVEL SECURITY;
REVOKE ALL ON order_lines FROM PUBLIC;
GRANT SELECT, INSERT ON order_lines TO app_runtime;   -- no UPDATE, no DELETE: lines are frozen

DROP POLICY IF EXISTS orders_tenant_isolation ON orders;
CREATE POLICY orders_tenant_isolation ON orders
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

DROP POLICY IF EXISTS order_lines_tenant_isolation ON order_lines;
CREATE POLICY order_lines_tenant_isolation ON order_lines
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;

-- persist-web-orders: 0026_orders_guest_check.sql, appended verbatim per the hand-kept
-- mirror convention (guest origin CHECK rejects NULL parts).

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- persist-web-orders (review follow-up): the guest branch of `orders_origin_identity_ck`
-- (0025) wrote `btrim(guest_document_id) <> ''` without `IS NOT NULL`. In SQL a CHECK passes
-- when it evaluates to NULL, so a guest order with a NULL document id, contact address or
-- display name was accepted by the database even though the domain constructor rejects it.
-- This migration drops and re-adds the constraint with each mandatory guest part written as
-- `IS NOT NULL AND btrim(...) <> ''`. The registered-customer branch is unchanged.
-- APPLIED AFTER: 0025_orders.sql. 0025 stays as it was applied (a shipped migration is never
-- edited); a fresh database gets the weak constraint from 0025 and the strict one from 0026.
--
-- Existing rows are validated by the re-added constraint: every order stored so far came
-- through the domain constructor, so none can violate it.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   ALTER TABLE orders DROP CONSTRAINT IF EXISTS orders_origin_identity_ck;
--   ALTER TABLE orders ADD CONSTRAINT orders_origin_identity_ck CHECK (
--       (origin = 'RegisteredCustomer' AND customer_id IS NOT NULL
--           AND guest_document_id IS NULL AND guest_channel IS NULL AND guest_contact_address IS NULL
--           AND guest_display_name IS NULL AND guest_delivery_notes IS NULL)
--       OR
--       (origin = 'Guest' AND customer_id IS NULL
--           AND btrim(guest_document_id) <> '' AND guest_channel IS NOT NULL
--           AND btrim(guest_contact_address) <> '' AND btrim(guest_display_name) <> ''));
--   COMMIT;

BEGIN;

ALTER TABLE orders DROP CONSTRAINT IF EXISTS orders_origin_identity_ck;
ALTER TABLE orders ADD CONSTRAINT orders_origin_identity_ck CHECK (
    (origin = 'RegisteredCustomer'
        AND customer_id IS NOT NULL
        AND guest_document_id IS NULL AND guest_channel IS NULL AND guest_contact_address IS NULL
        AND guest_display_name IS NULL AND guest_delivery_notes IS NULL)
    OR
    (origin = 'Guest'
        AND customer_id IS NULL
        AND guest_document_id IS NOT NULL AND btrim(guest_document_id) <> ''
        AND guest_channel IS NOT NULL
        AND guest_contact_address IS NOT NULL AND btrim(guest_contact_address) <> ''
        AND guest_display_name IS NOT NULL AND btrim(guest_display_name) <> ''));

COMMIT;
