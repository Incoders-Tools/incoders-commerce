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

-- customer-master-data: 0027_customer_master_data.sql, appended verbatim per the hand-kept
-- mirror convention (cities, business_types, customer references, Dni).

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- customer-master-data: the customer registry gains two ORGANIZATION-owned
-- catalogs, `cities` and `business_types` (modelled on `categories`, 0018),
-- optional references from `customers` to them, a free-text `contact_name`
-- (the person to talk to at the customer) and the `Dni` tax id type.
--
-- APPLIED AFTER: 0026_orders_guest_check.sql. Touches `customers` (0008).
--
-- Both catalogs are shared by every branch of the organization, so their
-- policy compares only `organization_id` (fail-closed when no organization is
-- scoped) - the same shape as `customers` (0008) and `categories` (0018).
-- DELETE is deliberately NOT granted: an entry that stops being used is
-- disabled (`is_active = false`) so existing customers keep their reference.
--
-- `customers.locality` (free text) stays for compatibility; `city_id` is the
-- source of truth going forward. Existing customers keep NULL references.
--
-- Re-run safety after later migrations: 0028 retires the organization-scoped
-- `cities` (and its foreign key) and 0029 retires `customers.contact_name`.
-- Their sections below are skipped once the replacing objects exist
-- (`countries`, `customer_contacts`), so re-applying the whole chain never
-- resurrects a retired object.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   ALTER TABLE customers DROP CONSTRAINT customers_city_org_fk;
--   ALTER TABLE customers DROP CONSTRAINT customers_business_type_org_fk;
--   ALTER TABLE customers DROP COLUMN city_id, DROP COLUMN business_type_id, DROP COLUMN contact_name;
--   (restore the tax_id_type CHECK to IN ('None','Cuit','Cuil') once no row uses 'Dni')
--   DROP TABLE cities; DROP TABLE business_types;

BEGIN;

-- ===========================================================================
-- 1. cities
-- ===========================================================================

DO $mig$
BEGIN
    IF to_regclass('public.countries') IS NOT NULL THEN
        RETURN;  -- cities became global reference data in 0028
    END IF;

    CREATE TABLE IF NOT EXISTS cities (
        id              uuid PRIMARY KEY,
        organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
        name            text NOT NULL CHECK (btrim(name) <> ''),
        key             text NOT NULL CHECK (btrim(key) <> ''),
        sort_order      integer NOT NULL DEFAULT 0,
        is_active       boolean NOT NULL DEFAULT true,
        created_at_utc  timestamptz NOT NULL DEFAULT now(),
        updated_at_utc  timestamptz NOT NULL DEFAULT now(),
        -- Target of the composite foreign key from `customers` (same shape as 0018).
        CONSTRAINT cities_org_scoped_uk UNIQUE (organization_id, id)
    );

    CREATE UNIQUE INDEX IF NOT EXISTS cities_org_name_uk ON cities (organization_id, lower(btrim(name)));
    CREATE UNIQUE INDEX IF NOT EXISTS cities_org_key_uk ON cities (organization_id, key);

    ALTER TABLE cities ENABLE ROW LEVEL SECURITY;
    ALTER TABLE cities FORCE  ROW LEVEL SECURITY;
    REVOKE ALL ON cities FROM PUBLIC;
    GRANT SELECT, INSERT, UPDATE ON cities TO app_runtime;

    DROP POLICY IF EXISTS cities_tenant_isolation ON cities;
    CREATE POLICY cities_tenant_isolation ON cities
        USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
        WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
END $mig$;

-- ===========================================================================
-- 2. business_types
-- ===========================================================================

CREATE TABLE IF NOT EXISTS business_types (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    key             text NOT NULL CHECK (btrim(key) <> ''),
    sort_order      integer NOT NULL DEFAULT 0,
    is_active       boolean NOT NULL DEFAULT true,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT business_types_org_scoped_uk UNIQUE (organization_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS business_types_org_name_uk ON business_types (organization_id, lower(btrim(name)));
CREATE UNIQUE INDEX IF NOT EXISTS business_types_org_key_uk ON business_types (organization_id, key);

ALTER TABLE business_types ENABLE ROW LEVEL SECURITY;
ALTER TABLE business_types FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON business_types FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON business_types TO app_runtime;

DROP POLICY IF EXISTS business_types_tenant_isolation ON business_types;
CREATE POLICY business_types_tenant_isolation ON business_types
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 3. customers: optional references, contact name, Dni
-- ===========================================================================

ALTER TABLE customers ADD COLUMN IF NOT EXISTS city_id          uuid;
ALTER TABLE customers ADD COLUMN IF NOT EXISTS business_type_id uuid;
-- `contact_name` is retired by 0029 (contacts sub-table): never re-add it then.
DO $mig$
BEGIN
    IF to_regclass('public.customer_contacts') IS NULL THEN
        ALTER TABLE customers ADD COLUMN IF NOT EXISTS contact_name text;
    END IF;
END $mig$;

-- RLS filters reads, not foreign-key checks, so the organization is part of
-- the key: a customer can never reference another organization's city or
-- business type (same reasoning as 0014/0016/0018). MATCH SIMPLE: a NULL
-- reference is not checked. RESTRICT: no hard delete of a referenced entry.
DO $$
BEGIN
    IF to_regclass('public.countries') IS NULL
       AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_city_org_fk') THEN
        ALTER TABLE customers
            ADD CONSTRAINT customers_city_org_fk
            FOREIGN KEY (organization_id, city_id)
            REFERENCES cities (organization_id, id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_business_type_org_fk') THEN
        ALTER TABLE customers
            ADD CONSTRAINT customers_business_type_org_fk
            FOREIGN KEY (organization_id, business_type_id)
            REFERENCES business_types (organization_id, id) ON DELETE RESTRICT;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS customers_org_city_idx ON customers (organization_id, city_id);
CREATE INDEX IF NOT EXISTS customers_org_business_type_idx ON customers (organization_id, business_type_id);

-- `tax_id_type` is stored as text (the enum member name) behind the inline
-- CHECK 0008 declared (auto-named customers_tax_id_type_check). Replace it
-- with one that also allows 'Dni'; skipped when it already does.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'customers'::regclass
          AND conname = 'customers_tax_id_type_check'
          AND pg_get_constraintdef(oid) LIKE '%Dni%'
    ) THEN
        ALTER TABLE customers DROP CONSTRAINT IF EXISTS customers_tax_id_type_check;
        ALTER TABLE customers
            ADD CONSTRAINT customers_tax_id_type_check
            CHECK (tax_id_type IN ('None', 'Cuit', 'Cuil', 'Dni'));
    END IF;
END $$;

COMMIT;

-- core-geography: 0028_core_geography.sql, appended verbatim per the hand-kept
-- mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- core-geography: cities stop being an ORGANIZATION catalog (0027) and become
-- core reference data shared by every organization, anchored to the official
-- Argentine geography: `countries` -> `provinces` -> `cities`, loaded from the
-- Georef API of datos.gob.ar (see deploy/db/reference/georef/README.md for the
-- source, licence and refresh procedure). Customers point at a global city.
--
-- APPLIED AFTER: 0027_customer_master_data.sql. Touches `customers` (0008) and
-- retires the organization-scoped `cities` table 0027 created.
--
-- Shape
--   countries (code PK 'AR', iso3, name)
--   provinces (id PK = INDEC province code '06', country_code, iso_code
--              'AR-B', name)
--   cities    (id uuid PK, indec_id UNIQUE = Georef localidad id e.g.
--              '06140010' (NULL for a city a system administrator added that
--              Georef does not list), name, province_id, department_name,
--              is_active, search_key = accent/case-folded name, audit dates)
--   Buenos Aires city is modelled the way Georef does: province '02' (AR-C)
--   with its barrios plus the representative locality 'Ciudad de Buenos
--   Aires' (02014010).
--
-- RLS: these are NOT tenant tables, so no policy compares an organization.
-- They keep ENABLE + FORCE ROW LEVEL SECURITY (so a future table-level change
-- cannot silently expose them) with permissive, role-agnostic policies: every
-- role that holds the privilege can read every row (the owner role included,
-- which matters when it is subject to RLS while loading data). Writes stay
-- narrow by GRANT: `app_runtime` may only INSERT/UPDATE
-- `cities` (system-administrator endpoints), never touch `countries` or
-- `provinces`, and nobody gets DELETE (a city that stops being used is
-- deactivated so customers keep their reference).
--
-- Why a new table named `cities` instead of a new name: the organization
-- table is retired in this same migration, so the final schema reads
-- naturally (`customers.city_id -> cities`). The old table is renamed to
-- `org_cities_retired` first, its rows are mapped onto Georef localities, and
-- it is dropped at the end of the migration.
--
-- Existing organization cities are mapped, per row, by accent/case-folded
-- name: (1) owner-decided names (Capital Federal -> 02014010, Capitán
-- Sarmiento -> 06140010, Río Tala -> 06770040); (2) an exact name inside
-- Buenos Aires province; (3) a Buenos Aires locality whose name starts with
-- the folded name followed by a space (San Nicolás -> San Nicolás de los
-- Arroyos); (4) an exact name anywhere in the country. A stage with several
-- candidates prefers the single census locality (8-digit id) over its 10-digit
-- sub-localities and otherwise is AMBIGUOUS: the city is left unmapped and
-- the customers that used it lose the reference (their free-text `locality`
-- is untouched). Every decision is printed as a NOTICE. The original
-- created/updated dates of a mapped city are preserved on the global row.
--
-- The whole file runs in ONE transaction and can be re-run safely (once the
-- organization table is retired every retirement step is skipped).
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   ALTER TABLE customers DROP CONSTRAINT customers_city_fk;
--   DROP TABLE cities; DROP TABLE provinces; DROP TABLE countries;
--   (the organization `cities` table and its customer links are NOT
--   recreated: restore them from a backup if ever needed)

BEGIN;

-- ===========================================================================
-- 1. countries and provinces
-- ===========================================================================

CREATE TABLE IF NOT EXISTS countries (
    code           text PRIMARY KEY CHECK (code ~ '^[A-Z]{2}$'),
    iso3           text NOT NULL UNIQUE CHECK (iso3 ~ '^[A-Z]{3}$'),
    name           text NOT NULL CHECK (btrim(name) <> ''),
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS provinces (
    id             text PRIMARY KEY CHECK (id ~ '^[0-9]{2}$'),
    country_code   text NOT NULL REFERENCES countries (code),
    iso_code       text NOT NULL UNIQUE CHECK (iso_code ~ '^[A-Z]{2}-[A-Z0-9]{1,3}$'),
    name           text NOT NULL CHECK (btrim(name) <> ''),
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now()
);

-- ===========================================================================
-- 2. Retire the organization-scoped `cities` of 0027 (rename now, drop below)
-- ===========================================================================

DO $mig$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'cities' AND column_name = 'organization_id'
    ) THEN
        ALTER TABLE cities RENAME TO org_cities_retired;
        ALTER INDEX cities_pkey RENAME TO org_cities_retired_pkey;
    END IF;
END $mig$;

-- ===========================================================================
-- 3. cities (global)
-- ===========================================================================

CREATE TABLE IF NOT EXISTS cities (
    id              uuid PRIMARY KEY,
    indec_id        text UNIQUE CHECK (indec_id ~ '^[0-9]{8}([0-9]{2})?$'),
    name            text NOT NULL CHECK (btrim(name) <> ''),
    province_id     text NOT NULL REFERENCES provinces (id),
    department_name text,
    is_active       boolean NOT NULL DEFAULT true,
    -- Accent- and case-folded name for search (same fold the customer search
    -- uses); generated so it can never drift from `name`.
    search_key      text GENERATED ALWAYS AS (
        translate(lower(name), 'áéíóúüñàèìòùâêîôûäëïöç', 'aeiouunaeiouaeiouaeioc')) STORED,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS cities_province_search_idx ON cities (province_id, search_key);
CREATE INDEX IF NOT EXISTS cities_search_prefix_idx ON cities (search_key text_pattern_ops);
-- A city added by a system administrator (no INDEC id) cannot duplicate an
-- existing one of the same province and department. Georef rows are exempt:
-- the source itself repeats a name inside a department (a locality and its
-- sub-locality).
CREATE UNIQUE INDEX IF NOT EXISTS cities_manual_name_uk
    ON cities (province_id, search_key, (coalesce(lower(btrim(department_name)), '')))
    WHERE indec_id IS NULL;

ALTER TABLE countries ENABLE ROW LEVEL SECURITY;
ALTER TABLE countries FORCE  ROW LEVEL SECURITY;
ALTER TABLE provinces ENABLE ROW LEVEL SECURITY;
ALTER TABLE provinces FORCE  ROW LEVEL SECURITY;
ALTER TABLE cities    ENABLE ROW LEVEL SECURITY;
ALTER TABLE cities    FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON countries, provinces, cities FROM PUBLIC;
GRANT SELECT ON countries, provinces TO app_runtime;
GRANT SELECT, INSERT, UPDATE ON cities TO app_runtime;

DROP POLICY IF EXISTS countries_read ON countries;
CREATE POLICY countries_read ON countries FOR SELECT USING (true);
DROP POLICY IF EXISTS provinces_read ON provinces;
CREATE POLICY provinces_read ON provinces FOR SELECT USING (true);
DROP POLICY IF EXISTS cities_read ON cities;
CREATE POLICY cities_read ON cities FOR SELECT USING (true);
DROP POLICY IF EXISTS cities_insert ON cities;
CREATE POLICY cities_insert ON cities FOR INSERT WITH CHECK (true);
DROP POLICY IF EXISTS cities_update ON cities;
CREATE POLICY cities_update ON cities FOR UPDATE USING (true) WITH CHECK (true);

-- ===========================================================================
-- 4. Georef data (generated)
-- ===========================================================================

-- >>> BEGIN GENERATED GEOREF DATA (deploy/db/reference/georef/generate.py) - do not edit by hand >>>
-- Source: https://apis.datos.gob.ar/georef/api/provincias and /localidades (datos.gob.ar, Georef; CC BY 4.0, see README.md).
-- Fetched 2026-10-02: 24 provinces, 4037 localities.

INSERT INTO countries (code, iso3, name) VALUES ('AR', 'ARG', 'Argentina')
ON CONFLICT (code) DO NOTHING;

INSERT INTO provinces (id, country_code, iso_code, name) VALUES
    ('02', 'AR', 'AR-C', 'Ciudad Autónoma de Buenos Aires'),
    ('06', 'AR', 'AR-B', 'Buenos Aires'),
    ('10', 'AR', 'AR-K', 'Catamarca'),
    ('14', 'AR', 'AR-X', 'Córdoba'),
    ('18', 'AR', 'AR-W', 'Corrientes'),
    ('22', 'AR', 'AR-H', 'Chaco'),
    ('26', 'AR', 'AR-U', 'Chubut'),
    ('30', 'AR', 'AR-E', 'Entre Ríos'),
    ('34', 'AR', 'AR-P', 'Formosa'),
    ('38', 'AR', 'AR-Y', 'Jujuy'),
    ('42', 'AR', 'AR-L', 'La Pampa'),
    ('46', 'AR', 'AR-F', 'La Rioja'),
    ('50', 'AR', 'AR-M', 'Mendoza'),
    ('54', 'AR', 'AR-N', 'Misiones'),
    ('58', 'AR', 'AR-Q', 'Neuquén'),
    ('62', 'AR', 'AR-R', 'Río Negro'),
    ('66', 'AR', 'AR-A', 'Salta'),
    ('70', 'AR', 'AR-J', 'San Juan'),
    ('74', 'AR', 'AR-D', 'San Luis'),
    ('78', 'AR', 'AR-Z', 'Santa Cruz'),
    ('82', 'AR', 'AR-S', 'Santa Fe'),
    ('86', 'AR', 'AR-G', 'Santiago del Estero'),
    ('90', 'AR', 'AR-T', 'Tucumán'),
    ('94', 'AR', 'AR-V', 'Tierra del Fuego, Antártida e Islas del Atlántico Sur')
ON CONFLICT (id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('1db17da5-f5fe-5d0f-bcd1-a086523d1a77', '0200701001', 'Constitución', '02', 'Comuna 1'),
    ('b251ebf3-ca9f-5b45-b173-1e736cf3a287', '0200701002', 'Monserrat', '02', 'Comuna 1'),
    ('69d47083-4d54-5a7f-8352-50c788624062', '0200701003', 'Puerto Madero', '02', 'Comuna 1'),
    ('e3ddc902-71ed-5761-90be-8c36ca71072b', '0200701004', 'Retiro', '02', 'Comuna 1'),
    ('a6cc7430-af1d-5e21-b473-c05927d3619e', '0200701005', 'San Nicolás', '02', 'Comuna 1'),
    ('fa71fb41-b0e7-54e5-b9cf-8fa75ebea968', '0200701006', 'San Telmo', '02', 'Comuna 1'),
    ('6d4f4e9a-6c7c-58b7-ba6b-bf3f8693fb11', '02014010', 'Ciudad de Buenos Aires', '02', 'Comuna 2'),
    ('e296db27-d24e-52a5-8648-b8775c3f886c', '0201401001', 'Recoleta', '02', 'Comuna 2'),
    ('17463ae0-c0c5-572a-b333-4953be5ae206', '0202101001', 'Balvanera', '02', 'Comuna 3'),
    ('4efdd835-6f1f-5d89-8bc0-590b2be02363', '0202101002', 'San Cristóbal', '02', 'Comuna 3'),
    ('8cddb85e-33be-5a90-9d7c-8c1876ee8879', '0202801001', 'Barracas', '02', 'Comuna 4'),
    ('259f5664-43e8-55ad-ba80-9cf4ba9bc687', '0202801002', 'Boca', '02', 'Comuna 4'),
    ('6d68527f-49c6-575d-9849-1123e66adbcd', '0202801003', 'Nueva Pompeya', '02', 'Comuna 4'),
    ('7b9a4d32-24a3-529b-a225-cc3ac1d44ebe', '0202801004', 'Parque Patricios', '02', 'Comuna 4'),
    ('38dd8949-8ac6-55ab-8b43-5c7dfe0905b1', '0203501001', 'Almagro', '02', 'Comuna 5'),
    ('92f97302-5c1a-5d1a-b740-1e8acfa55b56', '0203501002', 'Boedo', '02', 'Comuna 5'),
    ('25bc79cc-7b41-51ed-81a3-5911d8603315', '0204201001', 'Caballito', '02', 'Comuna 6'),
    ('ce5b197e-5d32-5406-b4fd-8bd80a7e1a98', '0204901001', 'Flores', '02', 'Comuna 7'),
    ('a4b25bf8-159e-546c-b05e-cb18d028818b', '0204901002', 'Parque Chacabuco', '02', 'Comuna 7'),
    ('21654223-608c-57ed-b891-732b5af5bfbf', '0205601001', 'Villa Lugano', '02', 'Comuna 8'),
    ('d7753baa-1813-5bf0-9874-3a42aac218be', '0205601002', 'Villa Riachuelo', '02', 'Comuna 8'),
    ('c4748801-8715-5b20-9659-3ce86ecb198e', '0205601003', 'Villa Soldati', '02', 'Comuna 8'),
    ('f62a6ec7-86e8-54c0-a6ec-c598cd2ae537', '0206301001', 'Liniers', '02', 'Comuna 9'),
    ('1225cf24-eb26-53df-af06-21228c25cc9f', '0206301002', 'Mataderos', '02', 'Comuna 9'),
    ('de2034ef-facd-55c5-b053-517c6ac9a4ae', '0206301003', 'Parque Avellaneda', '02', 'Comuna 9'),
    ('27e0bc87-6d7a-5242-9042-7a3db7608e5c', '0207001001', 'Floresta', '02', 'Comuna 10'),
    ('9f0470e3-765b-52dd-baf2-e7ea52968e77', '0207001002', 'Monte Castro', '02', 'Comuna 10'),
    ('97de8600-b5d1-5655-a313-e6d0c88f30df', '0207001003', 'Vélez Sarsfield', '02', 'Comuna 10'),
    ('86b53c12-1e4e-5b02-a6af-c0770d1dcd1f', '0207001004', 'Versalles', '02', 'Comuna 10'),
    ('c62459a0-1fa7-55a9-b428-d4a1ffb75ada', '0207001005', 'Villa Luro', '02', 'Comuna 10'),
    ('9f3ba987-31bb-57ba-9949-7facd97c8cb3', '0207001006', 'Villa Real', '02', 'Comuna 10'),
    ('730c00ad-621d-5733-862b-ea9cb034f742', '0207701001', 'Villa del Parque', '02', 'Comuna 11'),
    ('3d35f0bf-5176-5f31-89b2-2cdb6667d9bd', '0207701002', 'Villa Devoto', '02', 'Comuna 11'),
    ('ca4eeb54-2c18-5a97-9f0e-35b235034962', '0207701003', 'Villa General Mitre', '02', 'Comuna 11'),
    ('e752eab7-2b5c-597d-8374-000ed485cdab', '0207701004', 'Villa Santa Rita', '02', 'Comuna 11'),
    ('b0d47f6a-c1d0-56c3-bcee-5b5f2024256b', '0208401001', 'Coghlan', '02', 'Comuna 12'),
    ('455fd47b-840f-5665-9d7f-d0000fb88bcb', '0208401002', 'Saavedra', '02', 'Comuna 12'),
    ('3b12631b-e412-5cbf-94d3-482030fe8b5a', '0208401003', 'Villa Pueyrredón', '02', 'Comuna 12'),
    ('46c999ae-6ff6-5bf5-ba30-44f5c738c4c4', '0208401004', 'Villa Urquiza', '02', 'Comuna 12'),
    ('0c2d72e4-b0b5-5383-b81b-adc1a7d9b68c', '0209101001', 'Belgrano', '02', 'Comuna 13'),
    ('0f1df083-5eb1-57d2-be3f-48d08afaa5d5', '0209101002', 'Colegiales', '02', 'Comuna 13'),
    ('4954a7d9-8ed0-5bd4-8e67-a34a7eed75a8', '0209101003', 'Nuñez', '02', 'Comuna 13'),
    ('c8870b7d-37b6-521d-a908-a01d1156b8b3', '0209801001', 'Palermo', '02', 'Comuna 14'),
    ('3b3fa5d2-2df0-5ecc-b692-e87acb428a09', '0210501001', 'Agronomía', '02', 'Comuna 15'),
    ('acdfa864-228b-55d8-9fef-e45a497a7c72', '0210501002', 'Chacarita', '02', 'Comuna 15'),
    ('15ae4e5f-c319-5207-9d84-363f175430bf', '0210501003', 'Parque Chas', '02', 'Comuna 15'),
    ('267e3f5e-dac0-5cf3-8908-eb77b9743957', '0210501004', 'Paternal', '02', 'Comuna 15'),
    ('c4ccf768-4f61-58fb-be38-54638ff6fd65', '0210501005', 'Villa Crespo', '02', 'Comuna 15'),
    ('49fd0fdc-2afc-5623-9383-4ad31fe2f710', '0210501006', 'Villa Ortúzar', '02', 'Comuna 15'),
    ('7b2790b4-994a-5bed-93c4-cc07ee8584e8', '06007010', 'Carhué', '06', 'Adolfo Alsina'),
    ('131c0881-a099-5cf1-87bd-c0135346cb31', '06007020', 'Colonia San Miguel Arcángel', '06', 'Adolfo Alsina'),
    ('cdef9faa-037a-59e3-89c0-87ed4c23efe5', '06007030', 'Delfín Huergo', '06', 'Adolfo Alsina'),
    ('fbc96b0a-b56f-5f3c-bbb4-aa16312c1534', '06007040', 'Espartillar', '06', 'Adolfo Alsina'),
    ('25ad8c13-2cd7-5b38-960d-c771bdae74a3', '06007050', 'Esteban Agustín Gascón', '06', 'Adolfo Alsina'),
    ('8d9e15f0-5732-5b90-b8da-8d07fd2b7b86', '06007060', 'La Pala', '06', 'Adolfo Alsina'),
    ('13a92196-f290-5703-8cfe-13a00bccb14d', '06007070', 'Maza', '06', 'Adolfo Alsina'),
    ('de394c23-babd-5e6a-bf4c-74e2cd590653', '06007080', 'Rivera', '06', 'Adolfo Alsina'),
    ('17be24d8-933c-5267-ba6a-e37077712df4', '06007100', 'Villa Margarita', '06', 'Adolfo Alsina'),
    ('47534307-8728-58f3-a349-4b042de05674', '06014010', 'Adolfo Gonzales Chaves', '06', 'Adolfo Gonzales Chaves'),
    ('698c5159-19b2-5306-aab8-bfd209f7073b', '06014020', 'De La Garma', '06', 'Adolfo Gonzales Chaves'),
    ('5370cb05-3445-5e07-8587-c4d57d5171b0', '06014030', 'Juan E. Barra', '06', 'Adolfo Gonzales Chaves'),
    ('d2c995d1-b046-58e1-b4bc-553d777bc17b', '06014040', 'Vásquez', '06', 'Adolfo Gonzales Chaves'),
    ('627ce12c-52c4-5f0b-a642-31f9379f3488', '06021010', 'Alberti', '06', 'Alberti'),
    ('2a49aea0-3578-53df-b143-a970075b0188', '06021020', 'Coronel Seguí', '06', 'Alberti'),
    ('bc7ba355-a5b0-5c38-9d70-6dd79396434a', '06021030', 'Mechita', '06', 'Alberti'),
    ('ad2fc451-c63c-5c80-b088-28705bb1fb6a', '06021040', 'Pla', '06', 'Alberti'),
    ('c152c28f-5112-500b-b192-9d92000ec9de', '06021050', 'Villa Grisolía', '06', 'Alberti'),
    ('380e84d3-3fd1-5c5b-9e35-a33c3f482329', '06021060', 'Villa María', '06', 'Alberti'),
    ('f5735172-6768-58f2-800a-2e8df00b597c', '06021070', 'Villa Ortiz', '06', 'Alberti'),
    ('5bc43a28-7461-558e-9e6b-3ae48c111424', '06028010', 'Almirante Brown', '06', 'Almirante Brown'),
    ('4db7d53e-bd3a-5f1f-b06d-34d18926a744', '0602801001', 'Adrogué', '06', 'Almirante Brown'),
    ('67020f9c-0664-5c2e-b121-6bc6b0fa32c7', '0602801002', 'Burzaco', '06', 'Almirante Brown'),
    ('09f73772-4569-5c17-a3dc-5698565da7e1', '0602801003', 'Claypole', '06', 'Almirante Brown'),
    ('df50705d-f775-528e-8140-ed83a7fb2c47', '0602801004', 'Don Orione', '06', 'Almirante Brown'),
    ('9969b47c-76e9-5669-86ff-b0695c730dff', '0602801005', 'Glew', '06', 'Almirante Brown'),
    ('82171b76-4a32-53cf-a6d2-0d3a7a3c09d0', '0602801006', 'José Mármol', '06', 'Almirante Brown'),
    ('3118f9eb-ba6d-58bf-8e16-f9f32ce4bb33', '0602801007', 'Longchamps', '06', 'Almirante Brown'),
    ('29562d7b-b098-56a5-8506-9e4f4f3a4fce', '0602801008', 'Malvinas Argentinas', '06', 'Almirante Brown'),
    ('04c9a9a6-5b9c-5b03-91db-fe173e62e803', '0602801009', 'Ministro Rivadavia', '06', 'Almirante Brown'),
    ('496278bd-afb1-57d3-aae7-af3fc78b3591', '0602801010', 'Rafael Calzada', '06', 'Almirante Brown'),
    ('ada53054-9fb5-5ddd-ac1f-b3d20f88c06a', '0602801011', 'San Francisco Solano', '06', 'Almirante Brown'),
    ('b6f51945-0508-54b8-ba88-3228f916be42', '0602801012', 'San José', '06', 'Almirante Brown'),
    ('2f7a88f4-6564-5149-a653-03aecf314595', '06035010', 'Avellaneda', '06', 'Avellaneda'),
    ('248fe9f8-83d2-5549-b316-f9c1f4149221', '0603501001', 'Área Reserva Cinturón Ecológico', '06', 'Avellaneda'),
    ('b4c8acf2-67d7-54e3-a457-7c192260d5af', '0603501002', 'Avellaneda', '06', 'Avellaneda'),
    ('a76c4a3c-0eaa-502d-b28f-74c323c3c0cb', '0603501003', 'Crucesita', '06', 'Avellaneda'),
    ('53fa9586-499a-5f0f-9a72-5af06cf121d0', '0603501004', 'Dock Sud', '06', 'Avellaneda'),
    ('005f9a59-4b45-5e9d-b253-dd1e6c2e0bf8', '0603501005', 'Gerli', '06', 'Avellaneda'),
    ('5aab3ac2-4037-5fa8-9584-24475473ec23', '0603501006', 'Piñeyro', '06', 'Avellaneda'),
    ('40656b7b-699e-5ea9-ab28-17703aba7cf5', '0603501007', 'Sarandí', '06', 'Avellaneda'),
    ('147a6b86-02d4-580c-9a6f-985262f9fb28', '0603501008', 'Villa Domínico', '06', 'Avellaneda'),
    ('0b9147b5-4cf5-57dd-a8d7-ec61d923d1c2', '0603501009', 'Wilde', '06', 'Avellaneda'),
    ('42b0783a-cb3f-55aa-a9ff-d8f630b0f112', '06042010', 'Ayacucho', '06', 'Ayacucho'),
    ('bb747985-1405-5e61-b015-fcbb5ef012ea', '06042020', 'La Constancia', '06', 'Ayacucho'),
    ('ac91374e-dae1-5b90-8185-9a2fb8002698', '06042030', 'Solanet', '06', 'Ayacucho'),
    ('35f50c9f-ee9c-55e5-a1ce-f4d7a2b9a00b', '06042040', 'Udaquiola', '06', 'Ayacucho'),
    ('4bf0e507-e3c6-5252-a390-8f1d3e276667', '06049010', 'Ariel', '06', 'Azul'),
    ('d8bf4024-55bc-5324-9010-23d4f7ac1aaf', '06049020', 'Azul', '06', 'Azul'),
    ('f2c54aed-457a-51a0-b0b1-211913e94f14', '06049030', 'Cacharí', '06', 'Azul'),
    ('78a50e06-14c1-5bac-a5c7-67ed856391a5', '06049040', 'Chillar', '06', 'Azul'),
    ('89d1edf4-3c29-5488-ac37-f7ed9972949c', '06049050', '16 de Julio', '06', 'Azul'),
    ('40a998cb-17c2-5e5f-a3b0-62b36fb0ffe4', '06056010', 'Bahía Blanca', '06', 'Bahía Blanca'),
    ('0eed851a-a111-56b7-86c8-d19802652da1', '0605601001', 'Bahía Blanca', '06', 'Bahía Blanca'),
    ('eb22a951-80ed-5aac-a396-c1155c3964fe', '0605601002', 'Grünbein', '06', 'Bahía Blanca'),
    ('8152a9d2-08a0-565b-b4b4-6d47058952e0', '0605601003', 'Ingeniero White', '06', 'Bahía Blanca'),
    ('27596d4d-a6e9-5bb8-93d6-081bd57300eb', '0605601004', 'Villa Bordeau', '06', 'Bahía Blanca'),
    ('5cea2408-47a0-5eec-b161-54436c97d220', '0605601005', 'Villa Espora', '06', 'Bahía Blanca'),
    ('ea399a2b-e777-5f61-b1d0-c12c0fed6939', '06056020', 'Cabildo', '06', 'Bahía Blanca'),
    ('63ce6364-6cd4-5234-bb93-166008084a1e', '06056030', 'General Daniel Cerri', '06', 'Bahía Blanca'),
    ('43b8ef5a-e585-5d93-840a-c2cc1b385d5a', '06063010', 'Balcarce', '06', 'Balcarce'),
    ('689f669a-6632-5af1-86c2-689212040f49', '06063020', 'Los Pinos', '06', 'Balcarce'),
    ('c9bb6e19-bbcd-5ff0-8f64-f4ead16db071', '06063030', 'Napaleofú', '06', 'Balcarce'),
    ('8b60312c-6045-59b9-b94f-2cc0b2af573a', '06063040', 'Ramos Otero', '06', 'Balcarce'),
    ('d4a68444-dc7f-564b-95bc-c109a7fe7973', '06063050', 'San Agustín', '06', 'Balcarce'),
    ('e976599e-e623-5eaf-9ade-911055e0ef40', '06063060', 'Villa Laguna La Brava', '06', 'Balcarce'),
    ('badec458-4805-59c6-a30c-9cd7d4f9438b', '06070010', 'Baradero', '06', 'Baradero'),
    ('f22007a6-7843-5dbf-8918-0e2630b6f034', '06070020', 'Irineo Portela', '06', 'Baradero'),
    ('c0df9389-183f-5108-90d3-1f86e1b372bd', '06070030', 'Santa Coloma', '06', 'Baradero'),
    ('2c281e66-bcec-57de-b948-28dd49a7cefd', '06070040', 'Villa Alsina', '06', 'Baradero'),
    ('cec717c5-608a-5363-82f3-04fb3becb6c7', '06077010', 'Arrecifes', '06', 'Arrecifes'),
    ('bfddf18f-2f6c-57b1-8252-666ac0b6f2bb', '06077020', 'Todd', '06', 'Arrecifes'),
    ('696921fe-6491-53c0-a662-21389eb30e6e', '06077030', 'Viña', '06', 'Arrecifes'),
    ('4ae75bf2-38df-53ae-a5eb-cd750b525408', '06084010', 'Barker', '06', 'Benito Juárez'),
    ('dbb1ab38-be20-5690-9957-7b2e98ab27b6', '06084020', 'Benito Juárez', '06', 'Benito Juárez'),
    ('ef827b4a-0e1b-5ce1-a919-7ac5a6a8e882', '06084030', 'López', '06', 'Benito Juárez'),
    ('1583f200-7f5d-5c9b-9193-f869efaeafdd', '06084040', 'Tedín Uriburu', '06', 'Benito Juárez'),
    ('5bca7a50-bf4f-5ad3-9679-1b9dd73c0f1f', '06084050', 'Villa Cacique', '06', 'Benito Juárez'),
    ('6f5712bc-e3a4-509e-a3e6-1543b6fb0e22', '06091010', 'Berazategui', '06', 'Berazategui'),
    ('e4eb6111-908d-5a82-b678-56c84eba01ce', '0609101001', 'Berazategui', '06', 'Berazategui'),
    ('cca5e741-3758-57a6-a6e0-d8e12414c79e', '0609101002', 'Berazategui Oeste', '06', 'Berazategui'),
    ('4af88ef1-4d92-5a32-85bc-26232b85309e', '0609101003', 'Carlos Tomás Sourigues', '06', 'Berazategui'),
    ('097bf9a9-4c31-5939-9850-885d920060a5', '0609101004', 'El Pato', '06', 'Berazategui'),
    ('52728555-e508-522e-bb79-fb253a71db09', '0609101005', 'Guillermo Enrique Hudson', '06', 'Berazategui'),
    ('86db1699-1b12-50c5-9ad1-8c8b91ee7db5', '0609101006', 'Juan María Gutiérrez', '06', 'Berazategui'),
    ('5cf7bb40-0679-5e56-8fbd-036200d5d297', '0609101007', 'Pereyra', '06', 'Berazategui'),
    ('50e84a15-d58d-54d0-be5c-feec52c2dfbb', '0609101008', 'Plátanos', '06', 'Berazategui'),
    ('ce45b667-5c8f-54cf-b155-83f01dc37f13', '0609101009', 'Ranelagh', '06', 'Berazategui'),
    ('f6923134-217c-5581-9577-4bffdb89c543', '0609101010', 'Villa España', '06', 'Berazategui'),
    ('9a569f58-c608-54d3-8da2-c4133c1233a8', '06098010', 'Berisso', '06', 'Berisso'),
    ('6aaecb7e-0194-5847-8262-5f77d051b26d', '0609801001', 'Barrio Banco Provincia', '06', 'Berisso'),
    ('1d9bc3c0-fa4e-5f04-8ad8-fb5c5e435670', '0609801002', 'Barrio El Carmen Este', '06', 'Berisso'),
    ('ddab8643-aff2-5c1c-ae75-e70089ebd11f', '0609801003', 'Barrio Universitario', '06', 'Berisso'),
    ('d1f62ea7-3460-59c4-a3f5-f77d9d958aec', '0609801004', 'Berisso', '06', 'Berisso'),
    ('2641db97-44e0-5b99-9222-df4df2b48dc1', '0609801005', 'Los Talas', '06', 'Berisso'),
    ('90d09f4f-521a-5870-9124-824c512f7d2d', '0609801006', 'Villa Argüello', '06', 'Berisso'),
    ('a65a1354-3e6e-5259-beb9-f73820c4e3b4', '0609801007', 'Villa Dolores', '06', 'Berisso'),
    ('53d85061-82ea-54d3-a23c-b8be892340a3', '0609801008', 'Villa Independencia', '06', 'Berisso'),
    ('609e88e3-4f6a-5246-af01-ea2727a754a0', '0609801009', 'Villa Nueva', '06', 'Berisso'),
    ('11eca2e7-52b8-59e9-bb96-69e561475b9c', '0609801010', 'Villa Porteña', '06', 'Berisso'),
    ('51c2a066-90fb-5bdd-a833-e126c107d550', '0609801011', 'Villa Progreso', '06', 'Berisso'),
    ('a894ac20-5e24-5404-8d6c-ed577445ffb6', '0609801012', 'Villa San Carlos', '06', 'Berisso'),
    ('b1c285fd-3b63-570f-b1b6-7d2daa300810', '0609801013', 'Villa Zula', '06', 'Berisso'),
    ('ac6da781-6120-55bd-bda0-e0e951c240e6', '06105010', 'Hale', '06', 'Bolívar'),
    ('9dc68b3d-d2c8-5dcb-866d-6b69ec8deb26', '06105020', 'Juan F. Ibarra', '06', 'Bolívar'),
    ('a8e23707-360e-5401-bea0-cbbd82e21cdd', '06105040', 'Paula', '06', 'Bolívar'),
    ('08e4eeaf-2ec1-5ae6-86e4-6c7dd3725313', '06105050', 'Pirovano', '06', 'Bolívar'),
    ('8d2845c8-c56a-5ab9-aecc-6b53f347aa87', '06105060', 'San Carlos de Bolívar', '06', 'Bolívar'),
    ('b1edc8d3-d48b-560e-8c02-a31be612803e', '06105070', 'Urdampilleta', '06', 'Bolívar'),
    ('6be5b2ad-79a8-57b7-96dc-5504fe9bce7c', '06105080', 'Villa Lynch Pueyrredón', '06', 'Bolívar'),
    ('1d8a77b7-0be5-5c57-b773-63de85493146', '06112010', 'Bragado', '06', 'Bragado'),
    ('bb40d40b-2b95-5d81-98c9-240af6307ae3', '06112020', 'Comodoro Py', '06', 'Bragado'),
    ('de6cc66e-25e8-55dd-8353-bcb02636ee05', '06112030', 'General O''Brien', '06', 'Bragado'),
    ('537133b8-3117-5879-986c-14b8de5e05ef', '06112040', 'Irala', '06', 'Bragado'),
    ('4fe5fd8a-06f3-5434-972a-9643ae8d52ff', '06112050', 'La Limpia', '06', 'Bragado'),
    ('561979b6-66c0-5d20-8a63-28ba6d210b80', '06112060', 'Juan F. Salaberry', '06', 'Bragado'),
    ('690cd966-73ed-5054-bb29-c1d9fcb832cf', '06112070', 'Mechita', '06', 'Bragado'),
    ('480dcd6a-5fe5-5f88-80e4-baf88789a254', '06112080', 'Olascoaga', '06', 'Bragado'),
    ('ad217186-5cee-58fb-8347-1fc03439c60a', '06112090', 'Warnes', '06', 'Bragado'),
    ('2da571d6-4b99-5100-aa3a-7623e4bc92a0', '06119010', 'Altamirano', '06', 'Brandsen'),
    ('f91d0cc5-dc6f-52a5-8e91-72db243d0b60', '06119020', 'Barrio Las Golondrinas', '06', 'Brandsen'),
    ('c2d20923-d57b-5de8-ba5a-e1c5672e4a55', '06119030', 'Barrio Los Bosquecitos', '06', 'Brandsen'),
    ('991d3cf2-e16e-5a0b-abf4-4a4a45d4d84d', '06119040', 'Barrio Parque Las Acacias', '06', 'Brandsen'),
    ('6b192707-8f8f-55d5-9d05-1eccc0fac91f', '06119050', 'Coronel Brandsen', '06', 'Brandsen'),
    ('09e21d0d-c75e-5793-8e60-4c648ffbb65e', '06119060', 'Gómez', '06', 'Brandsen'),
    ('fe85967a-e527-50b1-874c-d7d517dc6812', '06119070', 'Jeppener', '06', 'Brandsen'),
    ('55c3931a-7435-5cc1-98cc-311273a5e5ba', '06119080', 'Oliden', '06', 'Brandsen'),
    ('f10f5294-a660-5f33-882a-4557542169d0', '06119090', 'Samborombón', '06', 'Brandsen'),
    ('87232ee8-3a0a-548f-b84c-b2ac25c181cc', '06126010', 'Los Cardales', '06', 'Campana'),
    ('fa43f567-7f1d-5163-a7d3-921e40e5ffac', '06126020', 'Barrio Los Pioneros (Barrio Tavella)', '06', 'Campana'),
    ('f6c1b06a-1cc0-57a3-8490-68d79560c4c3', '06134010', 'Alejandro Petión', '06', 'Cañuelas'),
    ('f42d5e84-89f6-5ce7-b26d-c7fa7c653b0f', '06134020', 'Barrio El Taladro', '06', 'Cañuelas'),
    ('620fba85-a182-5207-9d42-b730d3a78852', '06134030', 'Cañuelas', '06', 'Cañuelas'),
    ('505699d1-5c49-5ef8-ab09-1c0b8dfc70aa', '06134040', 'Gobernador Udaondo', '06', 'Cañuelas'),
    ('681dff6d-c8f9-5a9b-8121-943fcd473639', '06134060', 'Santa Rosa', '06', 'Cañuelas'),
    ('dbefe765-b70f-5ec5-afd8-6fef73213b94', '06134070', 'Uribelarrea', '06', 'Cañuelas'),
    ('42b451dd-eb07-5d2c-8b9e-145e3171ca6d', '06134080', 'Vicente Casares', '06', 'Cañuelas'),
    ('37fbe808-8172-5370-8fba-36606b35936a', '06140010', 'Capitán Sarmiento', '06', 'Capitán Sarmiento'),
    ('abd433c5-8b8f-51d3-95fc-66f9b4b099f7', '06140020', 'La Luisa', '06', 'Capitán Sarmiento'),
    ('f0f02efb-21e7-56d8-a78f-f00eb38a9b8d', '06147010', 'Bellocq', '06', 'Carlos Casares'),
    ('5547bbb5-131f-5d9d-9a34-eabfa4aa23fe', '06147020', 'Cadret', '06', 'Carlos Casares'),
    ('c7ce836c-b560-53f7-b4f1-ff3c7c530e81', '06147030', 'Carlos Casares', '06', 'Carlos Casares'),
    ('7c2e094f-f30d-5738-9b32-6de6f5d44e12', '06147040', 'Colonia Mauricio', '06', 'Carlos Casares'),
    ('15d62dba-96da-58ea-a138-7ccfcf04b57a', '06147050', 'Hortensia', '06', 'Carlos Casares'),
    ('c420ed24-8f53-552d-bbdd-a8769d51461d', '06147060', 'La Sofía', '06', 'Carlos Casares'),
    ('506a756c-5905-55e9-ad75-a7e0342ac21c', '06147070', 'Mauricio Hirsch', '06', 'Carlos Casares'),
    ('780af34f-972a-586d-8b4b-6713256e164a', '06147080', 'Moctezuma', '06', 'Carlos Casares'),
    ('a3e676e9-2eb7-57af-a6bd-0764082e8e25', '06147090', 'Ordoqui', '06', 'Carlos Casares'),
    ('aa726a34-25cc-5e5a-8f6d-91fd331e410e', '06147100', 'Smith', '06', 'Carlos Casares'),
    ('3d4c6c87-992b-530c-aab1-85eff9803776', '06154010', 'Carlos Tejedor', '06', 'Carlos Tejedor'),
    ('df634c12-3700-52f1-b944-b1be79c99bcd', '06154020', 'Colonia Seré', '06', 'Carlos Tejedor'),
    ('55ecb5ad-8577-5640-96fb-524d18648091', '06154030', 'Curarú', '06', 'Carlos Tejedor'),
    ('c5265ec3-089d-525c-86d2-1de93508fd09', '06154040', 'Timote', '06', 'Carlos Tejedor'),
    ('017dd587-e0cb-5753-aed1-93f537355a63', '06154050', 'Tres Algarrobos', '06', 'Carlos Tejedor'),
    ('3c0da3b8-e7ab-5602-acda-b2c54dcf97bf', '06161010', 'Carmen de Areco', '06', 'Carmen de Areco'),
    ('df254b35-e7bf-59b8-b8c9-5148463a9b81', '06161020', 'Pueblo Gouin', '06', 'Carmen de Areco'),
    ('807fa604-0989-5e84-aada-0e5f74421268', '06161030', 'Tres Sargentos', '06', 'Carmen de Areco'),
    ('75ac17c1-1ec8-562d-ad0c-d4313352544e', '06168010', 'Castelli', '06', 'Castelli'),
    ('c14d9e9a-018f-58b9-a004-08fce3fafdbd', '06168020', 'Centro Guerrero', '06', 'Castelli'),
    ('5b95b0e3-7cfc-5f0c-9149-9635a5ebad30', '06168030', 'Cerro de la Gloria', '06', 'Castelli'),
    ('dba0c71e-8afd-5caf-8dca-e7708b69bf5a', '06175010', 'Colón', '06', 'Colón'),
    ('f472db07-7392-5056-b2d1-9d3ad62da40f', '06175020', 'Villa Manuel Pomar', '06', 'Colón'),
    ('51ff6c09-0ce8-55de-85e8-03c7c6e5603e', '06175030', 'Pearson', '06', 'Colón'),
    ('0b2ca9d7-0884-5d8a-a653-06c59215bcd7', '06175040', 'Sarasa', '06', 'Colón'),
    ('2d8d989e-47b6-5f3a-ab9b-51c546a5826a', '06182010', 'Bajo Hondo', '06', 'Coronel de Marina Leonardo Rosales'),
    ('6eafb510-9685-54d5-a56f-5668dbfb6bda', '06182020', 'Balneario Pehuen Co', '06', 'Coronel de Marina Leonardo Rosales'),
    ('63f1282f-ac64-5661-8b31-ca96f5bce825', '06182030', 'Punta Alta', '06', 'Coronel de Marina Leonardo Rosales'),
    ('adfba6ba-155f-59b6-9352-b3ebf6599eaa', '0618203001', 'Punta Alta', '06', 'Coronel de Marina Leonardo Rosales'),
    ('b47bd2bc-3c91-5853-b067-d060cf584ae1', '0618203002', 'Villa del Mar', '06', 'Coronel de Marina Leonardo Rosales'),
    ('cb9f1dd6-46c7-5413-9855-09f1f790be7b', '06182050', 'Villa General Arias', '06', 'Coronel de Marina Leonardo Rosales'),
    ('010a2c78-5d9a-59a3-bd79-e4c8af840f48', '06189010', 'Aparicio', '06', 'Coronel Dorrego'),
    ('37419d45-56cf-504e-aa41-0907b7768245', '06189020', 'Marisol', '06', 'Coronel Dorrego'),
    ('7c7862a0-6514-5eb3-aa7b-27bf0db9d406', '06189030', 'Coronel Dorrego', '06', 'Coronel Dorrego'),
    ('9cbc7535-86ac-5068-8c52-ae1375ad7be3', '06189040', 'El Perdido', '06', 'Coronel Dorrego'),
    ('8814d8f8-8f4d-5263-b48b-d5a33048e6f0', '06189050', 'Faro', '06', 'Coronel Dorrego'),
    ('7a019553-31de-55a8-8cf5-da822d30792b', '06189060', 'Irene', '06', 'Coronel Dorrego'),
    ('a7e84ce2-bb95-57b9-8132-b7c989f98948', '06189070', 'Oriente', '06', 'Coronel Dorrego'),
    ('fe39195b-0df6-596d-a238-10dee73a9ef1', '06189080', 'San Román', '06', 'Coronel Dorrego'),
    ('74947e68-c94b-5662-89d4-fb3cc1108ff7', '06196010', 'Coronel Pringles', '06', 'Coronel Pringles'),
    ('cf3abbb6-e959-5144-8888-1a669fc637b0', '06196020', 'El Divisorio', '06', 'Coronel Pringles'),
    ('231c4135-7668-5a07-97bf-ded76fa92817', '06196030', 'El Pensamiento', '06', 'Coronel Pringles'),
    ('c4d1e58d-6258-553c-8c29-8e95e57f0b01', '06196040', 'Indio Rico', '06', 'Coronel Pringles'),
    ('d08a7c04-fcd9-592f-9e5f-d207136d2f29', '06196050', 'Lartigau', '06', 'Coronel Pringles'),
    ('8fb0e5d5-f4af-5117-95cf-26b7d536f1a3', '06203010', 'Cascada', '06', 'Coronel Suárez'),
    ('75308670-10d3-5e09-96ae-75aab1ebd058', '06203020', 'Coronel Suárez', '06', 'Coronel Suárez'),
    ('5c1bf26c-8585-5d18-a764-e23503ecc04f', '06203030', 'Curamalal', '06', 'Coronel Suárez'),
    ('3fba1c4c-e5ff-5cdb-aa6b-3db2ef87373a', '06203040', 'D''Orbigny', '06', 'Coronel Suárez'),
    ('1d11a7b3-ae4e-58fb-bda5-f00904f2c07c', '06203050', 'Huanguelén', '06', 'Coronel Suárez'),
    ('59eebe86-0932-5d71-b8c6-e25fc3b9df4a', '06203060', 'Pasman', '06', 'Coronel Suárez'),
    ('c1f83bb6-bdf4-5141-b59c-d3286014bb2d', '06203070', 'San José', '06', 'Coronel Suárez'),
    ('9dc1ef37-d171-5e70-ae28-07357fa34a5d', '06203080', 'Santa María', '06', 'Coronel Suárez'),
    ('f43be958-821a-57af-947e-0f85d3f88e62', '06203090', 'Santa Trinidad', '06', 'Coronel Suárez'),
    ('81c9b4cd-8750-55bd-a4d9-3585081854f4', '06203100', 'Villa La Arcadia', '06', 'Coronel Suárez'),
    ('020e86b1-d5ba-5c90-9c49-301dfd66bd90', '06210010', 'Castilla', '06', 'Chacabuco'),
    ('292d40b4-631b-50eb-87fa-c387696f9125', '06210020', 'Chacabuco', '06', 'Chacabuco'),
    ('31beca0b-bb62-59df-9270-5fc3611fdf01', '06210030', 'Los Angeles', '06', 'Chacabuco'),
    ('5d05cc6c-412f-5d88-b4ed-5dce4ff86e6a', '06210040', 'O''Higgins', '06', 'Chacabuco'),
    ('cb8fe266-ddc3-50a9-9856-b6ba8ba773f6', '06210050', 'Rawson', '06', 'Chacabuco'),
    ('9c4fb907-595e-5842-a859-465d473281c8', '06218010', 'Chascomús', '06', 'Chascomús'),
    ('86a80adc-c3be-562b-8b07-2dd4ec3fca04', '0621801001', 'Chascomús', '06', 'Chascomús'),
    ('95e17ca0-5884-561c-bb52-bf530c134894', '0621801003', 'Barrio San Cayetano', '06', 'Chascomús'),
    ('debaaab0-f9fb-5e2e-8ba1-a06ea145101c', '06218030', 'Villa Parque Girado', '06', 'Chascomús'),
    ('dcc95255-ea62-5a55-a80a-ab80205a65c4', '06224010', 'Chivilcoy', '06', 'Chivilcoy'),
    ('4a8610cd-e5b2-5d30-a022-3d7ecd5b1758', '06224020', 'Emilio Ayarza', '06', 'Chivilcoy'),
    ('0008acda-7318-503a-946b-1487dcb16d40', '06224030', 'Gorostiaga', '06', 'Chivilcoy'),
    ('9797b7aa-8a6e-5abf-b17a-0e9509598c6b', '06224040', 'La Rica', '06', 'Chivilcoy'),
    ('93d6b444-1efc-5322-8e59-a6dca368adda', '06224050', 'Moquehuá', '06', 'Chivilcoy'),
    ('2b6187e0-da18-5392-8217-56836ca829db', '06224060', 'Ramón Biaus', '06', 'Chivilcoy'),
    ('d708add6-ce34-561c-9f23-73cd3c06216f', '06224070', 'San Sebastián', '06', 'Chivilcoy'),
    ('eb8c778f-ceaf-5e47-9678-4855fa910305', '06231010', 'Andant', '06', 'Daireaux'),
    ('6d232c0b-dd1a-57b1-9bc8-62136ae4e683', '06231020', 'Arboledas', '06', 'Daireaux'),
    ('70c6dc9f-9e2b-5767-acca-471edec7010b', '06231030', 'Daireaux', '06', 'Daireaux'),
    ('3d37922a-76a5-566a-8291-33008247bb93', '06231040', 'La Larga', '06', 'Daireaux'),
    ('1cf0e5b8-b915-5ae9-a30c-0348e1842eb2', '06231060', 'Salazar', '06', 'Daireaux'),
    ('6a6d9745-ad4e-53b5-a155-c21b7f26e052', '06238010', 'Dolores', '06', 'Dolores'),
    ('5d615c48-bd80-52e4-9202-087cd5f28de6', '06238020', 'Sevigne', '06', 'Dolores'),
    ('bb8943f6-098a-56d9-8ec7-1ced8a84a5a1', '06245010', 'Ensenada', '06', 'Ensenada'),
    ('1adf0e7b-6bf2-51c3-8b00-1eed2cd1711b', '0624501001', 'Dique N° 1', '06', 'Ensenada'),
    ('5d205626-9f56-5e9f-a0d2-c95a27806c56', '0624501002', 'Ensenada', '06', 'Ensenada'),
    ('41d68055-92c8-59b9-ba85-c4a166856362', '0624501003', 'Isla Santiago', '06', 'Ensenada'),
    ('81bac078-a047-59c8-815f-9219978fb5f1', '0624501004', 'Punta Lara', '06', 'Ensenada'),
    ('6d3aa4a1-c1cf-5687-a1a4-80aeb7c167e9', '0624501005', 'Villa Catela', '06', 'Ensenada'),
    ('ca65e13c-9560-51b1-97a3-c9e7cb270430', '06252010', 'Escobar', '06', 'Escobar'),
    ('d1f29313-2deb-5278-8b1d-0f1c6dea7cd3', '0625201001', 'Belén de Escobar', '06', 'Escobar'),
    ('368fbc4c-be98-588a-9e62-1538b6496878', '0625201002', 'El Cazador', '06', 'Escobar'),
    ('7aaf01eb-7155-5ee9-bca2-63b7fd9ca0f7', '0625201003', 'Garín', '06', 'Escobar'),
    ('d47b4a4f-aed1-5e89-a6fb-37e1d9c8f887', '0625201004', 'Ingeniero Maschwitz', '06', 'Escobar'),
    ('47c204b9-e415-5fc8-926f-d385368f47cc', '0625201005', 'Loma Verde', '06', 'Escobar'),
    ('24d87e7b-7fec-5c2b-8795-5864f869b420', '0625201006', 'Matheu', '06', 'Escobar'),
    ('9fbac78e-9ef2-5e06-9cf6-bba7b1abd516', '0625201007', 'Maquinista F. Savio Este', '06', 'Escobar'),
    ('a0207777-ede6-52ec-8823-64960ae08e5f', '06260010', 'Esteban Echeverría', '06', 'Esteban Echeverría'),
    ('d8eadbf3-ea94-53c1-a750-4a4754faff26', '0626001001', 'Canning', '06', 'Esteban Echeverría'),
    ('fde6ff2e-fd0e-5475-abb1-b9a39e16e21b', '0626001002', 'El Jagüel', '06', 'Esteban Echeverría'),
    ('4ea80a9b-6bf8-58c8-9676-194035a44014', '0626001003', 'Luis Guillón', '06', 'Esteban Echeverría'),
    ('62c945be-d0ba-5783-8552-a236af8a06db', '0626001004', 'Monte Grande', '06', 'Esteban Echeverría'),
    ('c9b0ae51-bbe1-5d06-bf8f-a70ccd2ca4e8', '0626001005', '9 de Abril', '06', 'Esteban Echeverría'),
    ('ff08059f-63b6-5e9d-b59e-c7a92b2a4b76', '06266010', 'Arroyo de la Cruz', '06', 'Exaltación de la Cruz'),
    ('5f18b4ed-7fd9-5db8-badd-c581b7f3fb55', '06266050', 'Parada Orlando', '06', 'Exaltación de la Cruz'),
    ('18d8a762-8cbd-53aa-a875-2f5035ff2722', '06266060', 'Parada Robles - Pavón', '06', 'Exaltación de la Cruz'),
    ('59fa81ec-5544-5d0e-bbbb-5a948d8c64e1', '0626606001', 'El Remanso', '06', 'Exaltación de la Cruz'),
    ('c27b6384-602f-5477-9fbb-783d2293cbda', '0626606002', 'Parada Robles', '06', 'Exaltación de la Cruz'),
    ('38383dde-74c0-5973-87c5-51cb4c5a618b', '0626606003', 'Pavón', '06', 'Exaltación de la Cruz'),
    ('bd1f12fe-fab0-51b7-bb82-c644d4d3c4da', '06270010', 'Ezeiza', '06', 'Ezeiza'),
    ('bfdc0e41-adc1-5627-b2b9-907466d4c38e', '0627001001', 'Aeropuerto Internacional Ezeiza', '06', 'Ezeiza'),
    ('4f089f90-614a-5fb4-84b7-63d58ec39136', '0627001002', 'Canning', '06', 'Ezeiza'),
    ('c74c05b0-cb4e-51ed-8519-d16b6ecb07b9', '0627001003', 'Carlos Spegazzini', '06', 'Ezeiza'),
    ('7b590dca-29b1-547e-9b37-7498a928a080', '0627001004', 'José María Ezeiza', '06', 'Ezeiza'),
    ('cfec8f64-90f9-5c03-9738-6cf30cd9886a', '0627001005', 'La Unión', '06', 'Ezeiza'),
    ('86ed97a4-6ece-54cd-a10e-5048e1eee961', '0627001006', 'Tristán Suárez', '06', 'Ezeiza'),
    ('93fdcb2d-bbb3-5d85-a256-4c6c8b7dea1c', '06274010', 'Florencio Varela', '06', 'Florencio Varela'),
    ('aa7a2765-4700-5739-a8c0-aca346681c75', '0627401001', 'Bosques', '06', 'Florencio Varela'),
    ('a9c6246b-d8d6-5e06-ae52-5550eebbb9c9', '0627401002', 'Estanislao Severo Zeballos', '06', 'Florencio Varela'),
    ('9111c1a5-aa3c-57b8-9614-782a0fa66802', '0627401003', 'San Juan Bautista', '06', 'Florencio Varela'),
    ('9c8a7cd2-0939-5a60-998d-ac60172e79f4', '0627401004', 'Gobernador Julio A. Costa', '06', 'Florencio Varela'),
    ('fd643aaa-030a-51dc-bf9e-6090d4e52da3', '0627401005', 'Ingeniero Juan Allan', '06', 'Florencio Varela'),
    ('5ea24863-0606-5fed-97f6-45315e8e7c9a', '0627401006', 'Villa Brown', '06', 'Florencio Varela'),
    ('708b7ec2-15da-521e-b3e8-c0858a9da297', '0627401007', 'Villa San Luis', '06', 'Florencio Varela'),
    ('6b36b12f-da25-5949-973b-26086703e0cb', '0627401008', 'Villa Santa Rosa', '06', 'Florencio Varela'),
    ('ce1905fd-9278-5884-9c9b-3b846d48b946', '0627401009', 'Villa Vatteone', '06', 'Florencio Varela'),
    ('e887b93a-bd51-5d8c-9155-aab12ee3eb6f', '0627401010', 'El Tropezón', '06', 'Florencio Varela'),
    ('a36ad70f-1fc8-52a4-90ee-33f2d36ad2f3', '0627401011', 'La Capilla', '06', 'Florencio Varela'),
    ('3bf6a281-24e8-5f71-9065-c092b8e0af54', '06277010', 'Blaquier', '06', 'Florentino Ameghino'),
    ('c7f8fa2b-6ff0-5168-8537-458eeb8af052', '06277020', 'Florentino Ameghino', '06', 'Florentino Ameghino'),
    ('ef87c048-d3c6-5990-bafa-4dacec2fb6db', '06277030', 'Porvenir', '06', 'Florentino Ameghino'),
    ('25a1755a-542a-506d-8fa5-75a99fdf9c6d', '06280010', 'Comandante Nicanor Otamendi', '06', 'General Alvarado'),
    ('57506978-24db-5825-97f9-5d0778874ae6', '06280020', 'Mar del Sur', '06', 'General Alvarado'),
    ('33907857-3b41-5016-936f-a58e7c647ee0', '06280030', 'Mechongué', '06', 'General Alvarado'),
    ('f7638bf9-3119-54b0-b018-d943eca10996', '06280040', 'Miramar', '06', 'General Alvarado'),
    ('23d4e516-bcbb-5ab9-a5db-df0e958a7425', '06287010', 'General Alvear', '06', 'General Alvear'),
    ('b24d2a88-4645-54fb-b474-6732db19126f', '06294010', 'Arribeños', '06', 'General Arenales'),
    ('e76d8aa8-977a-5adb-b4a2-a5183bca4afe', '06294020', 'Ascensión', '06', 'General Arenales'),
    ('79d75b1a-4d47-5144-beca-32967033f2be', '06294030', 'Estación Arenales', '06', 'General Arenales'),
    ('2cde145d-996d-5b09-a870-08c185068483', '06294040', 'Ferré', '06', 'General Arenales'),
    ('2c6bb518-70fa-5ce4-a0bb-63f25e6ea2cc', '06294050', 'General Arenales', '06', 'General Arenales'),
    ('730dce44-3bc3-5b0e-a487-5a4b403fa7b0', '06294060', 'La Angelita', '06', 'General Arenales'),
    ('6082bf59-47e0-57b5-b659-30fceea7769e', '06294070', 'La Trinidad', '06', 'General Arenales'),
    ('22ca8001-5eaf-5df5-99a6-59dfee872771', '06301010', 'General Belgrano', '06', 'General Belgrano'),
    ('69d35340-b98b-5af7-b5d9-63aa14599abb', '06301020', 'Gorchs', '06', 'General Belgrano'),
    ('9e81b984-3aa4-5efd-80e1-5b389c1348b1', '06308010', 'General Guido', '06', 'General Guido'),
    ('1d6eb434-b92d-5dd5-9a86-a96e85c80eb5', '06308020', 'Labardén', '06', 'General Guido'),
    ('1ac0eae1-4482-5d6d-b720-02a65d1c711c', '06315010', 'General Juan Madariaga', '06', 'General Juan Madariaga'),
    ('7f3ae11c-43a8-57d1-a166-5170d17f3f0b', '0631501001', 'Barrio Kennedy', '06', 'General Juan Madariaga'),
    ('2fa411a5-1e07-5ca9-b72e-f187b39528cf', '0631501002', 'General Juan Madariaga', '06', 'General Juan Madariaga'),
    ('5813634e-b379-5178-bbc2-08efedc07f7f', '06322010', 'General La Madrid', '06', 'General La Madrid'),
    ('2469ffdc-e1a2-547c-855b-b2aceefabbf6', '06322020', 'La Colina', '06', 'General La Madrid'),
    ('f095454d-4681-589f-a7e9-8064356cf181', '06322030', 'Las Martinetas', '06', 'General La Madrid'),
    ('d50e3b6d-32c5-5293-95f6-736edfdcf1cd', '06322040', 'Líbano', '06', 'General La Madrid'),
    ('93cfb43d-6a31-52f8-be17-9d8c019d7008', '06322050', 'Pontaut', '06', 'General La Madrid'),
    ('b6faf6b4-5e98-5e13-9e04-5b93b7536d59', '06329010', 'General Hornos', '06', 'General Las Heras'),
    ('309f617b-88e1-5928-89c5-cea74e141bd6', '06329020', 'General Las Heras', '06', 'General Las Heras'),
    ('e07fe3af-628d-53f6-b0e3-7c72f4f60c55', '06329030', 'La Choza', '06', 'General Las Heras'),
    ('6d56bf3c-f5da-557b-bb7c-fb81dec215f0', '06329050', 'Plomer', '06', 'General Las Heras'),
    ('338cc217-a439-55dd-8b5d-18668c7f4282', '06336020', 'General Lavalle', '06', 'General Lavalle'),
    ('e5edfdfa-3f29-5bc0-9248-cf6df778ba0a', '06343010', 'Barrio Río Salado', '06', 'General Paz'),
    ('37b8da88-2f23-5e06-9f56-b93879b9bf8f', '06343020', 'Loma Verde', '06', 'General Paz'),
    ('56170b12-e2a3-5607-b798-745132c7ce7d', '06343030', 'Ranchos', '06', 'General Paz'),
    ('0d1b0ad0-9709-5b88-86a3-9382e96b4daf', '06343040', 'Villanueva', '06', 'General Paz'),
    ('f6b2e719-acc8-5e7f-9f9a-bd6946a21cfb', '06351010', 'Colonia San Ricardo', '06', 'General Pinto'),
    ('ff2de4b9-2f40-5cf9-9ecc-ff7c35c8b5c1', '06351020', 'General Pinto', '06', 'General Pinto'),
    ('be253ed7-8548-5b92-b77d-1b556846bed8', '06351030', 'Germania', '06', 'General Pinto'),
    ('ebf9a850-c9ce-538e-a453-726c5f4cc8c8', '06351040', 'Villa Francia', '06', 'General Pinto'),
    ('92435c70-5c5a-555f-90c0-e9ddd30431f7', '06351050', 'Villa Roth', '06', 'General Pinto'),
    ('681146f8-853b-55a8-baf7-7573ac36cd60', '06357060', 'Barrio Santa Paula', '06', 'General Pueyrredón'),
    ('61a8ed16-444a-5d15-8384-03112b1928f6', '06357070', 'Batán', '06', 'General Pueyrredón'),
    ('d20db0d4-c034-52d0-b903-ac6bea81ad34', '06357080', 'Chapadmalal', '06', 'General Pueyrredón'),
    ('6dda112b-f6e6-58cd-aa12-4bbf5597318b', '06357090', 'El Marquesado', '06', 'General Pueyrredón'),
    ('b962a4dd-5479-5a7d-935c-60e533f88267', '06357100', 'Estación Chapadmalal', '06', 'General Pueyrredón'),
    ('edbe4f95-9639-5898-b22a-e3d590efff5c', '06357110', 'Mar del Plata', '06', 'General Pueyrredón'),
    ('30d806d4-a262-5443-87c3-a8634a8c75c2', '0635711001', 'Camet', '06', 'General Pueyrredón'),
    ('a14a1c21-3ca7-5882-8352-1048a252634b', '0635711002', 'Estación Camet', '06', 'General Pueyrredón'),
    ('9fb35624-516a-551d-a552-51e6ebe178b8', '0635711003', 'Mar del Plata', '06', 'General Pueyrredón'),
    ('02fc507f-b446-58a7-a21d-bae30dd96dd8', '0635711004', 'Punta Mogotes', '06', 'General Pueyrredón'),
    ('e10ffc41-a32e-58fc-b36e-2952b59c983e', '0635711005', 'Barrio El Casal', '06', 'General Pueyrredón'),
    ('ee13552c-3622-5dfb-96ad-7bb2cdd3c088', '06357120', 'Sierra de los Padres', '06', 'General Pueyrredón'),
    ('1a3ccc66-29fb-56ab-bbfc-329daecbfc4b', '0635712001', 'Barrio Colinas Verdes', '06', 'General Pueyrredón'),
    ('e5496881-967b-5acc-86cc-9f7b18432571', '0635712002', 'Barrio El Coyunco', '06', 'General Pueyrredón'),
    ('ee43070f-4dac-5141-897e-9786c1e5cb51', '0635712003', 'Barrio La Gloria', '06', 'General Pueyrredón'),
    ('3afa1df3-e517-57b4-b36b-2d2737979dde', '0635712004', 'Sierra de los Padres', '06', 'General Pueyrredón'),
    ('a6e29770-af1f-504f-877a-9add6209ff7d', '06364030', 'General Rodríguez', '06', 'General Rodríguez'),
    ('0e04834e-d0e2-5955-a6e3-6b8a6fa28631', '0636403001', 'Barrio Morabo', '06', 'General Rodríguez'),
    ('1ae1857b-c3be-572a-a016-fde336cb4d59', '0636403002', 'Barrio Ruta 24 Kilómetro 10', '06', 'General Rodríguez'),
    ('4864f8ad-ea2d-5ffb-8115-ebc0cbdfcfa3', '0636403003', 'Country Club Bosque Real', '06', 'General Rodríguez'),
    ('280150a4-57e9-540f-8f28-632634f1f745', '0636403004', 'General Rodríguez', '06', 'General Rodríguez'),
    ('10ba01ad-a1d4-5f96-8439-749014ce433e', '06371010', 'General San Martín', '06', 'General San Martín'),
    ('3cfe9360-8687-5511-91b4-2433f8154416', '0637101001', 'Barrio Parque General San Martín', '06', 'General San Martín'),
    ('4ee0abda-0e89-516b-868c-4bb09d6c0a01', '0637101002', 'Billinghurst', '06', 'General San Martín'),
    ('8cab6101-e8cb-5457-8b60-e8036a2ef055', '0637101003', 'Ciudad del Libertador General San Martín', '06', 'General San Martín'),
    ('f30c78ec-3f64-5dc7-a7c2-238063235d41', '0637101004', 'Ciudad Jardín El Libertador', '06', 'General San Martín'),
    ('a8f5fa32-4465-5030-a000-f13687e24b0e', '0637101005', 'Villa Ayacucho', '06', 'General San Martín'),
    ('02f2f94e-189b-5ae0-b4d9-59e1ccc8eb83', '0637101006', 'Villa Ballester', '06', 'General San Martín'),
    ('f1666331-bbfb-5b7b-84b6-0561098855e2', '0637101007', 'Villa Bernardo Monteagudo', '06', 'General San Martín'),
    ('9e796148-84d7-5390-a740-00a51c40482b', '0637101008', 'Villa Chacabuco', '06', 'General San Martín'),
    ('27b37797-8fd8-528f-9b78-d1de236c0154', '0637101009', 'Villa Coronel José M. Zapiola', '06', 'General San Martín'),
    ('39b8ac71-b6f8-5d69-8cf8-365b6abc7cc3', '0637101010', 'Villa General Antonio J. de Sucre', '06', 'General San Martín'),
    ('0921e63d-96c0-54a5-8e05-1aceab53c551', '0637101011', 'Villa General Eugenio Necochea', '06', 'General San Martín'),
    ('b72e2169-b3c0-54ff-a46b-75d29af7d763', '0637101012', 'Villa General José Tomás Guido', '06', 'General San Martín'),
    ('ab808bd6-3366-5b62-89ee-3ef290393615', '0637101013', 'Villa General Juan G. Las Heras', '06', 'General San Martín'),
    ('46e5b26d-2333-500c-b572-5147c99d3630', '0637101014', 'Villa Godoy Cruz', '06', 'General San Martín'),
    ('9e4258d1-34b1-5621-8201-a6349a876c93', '0637101015', 'Villa Granaderos de San Martín', '06', 'General San Martín'),
    ('91957830-c5f5-5a54-b7ac-491f48d3c8a8', '0637101016', 'Villa Gregoria Matorras', '06', 'General San Martín'),
    ('bfb9f479-7f3f-5b12-b096-748e6cb638a2', '0637101017', 'Villa José León Suárez', '06', 'General San Martín'),
    ('632de988-b3e0-5078-8502-1f43861034e8', '0637101018', 'Villa Juan Martín de Pueyrredón', '06', 'General San Martín'),
    ('df38d99f-b5ca-5b62-8af8-7119023f0a0b', '0637101019', 'Villa Libertad', '06', 'General San Martín'),
    ('161bcce7-507f-5cd4-a117-63e62b7496c0', '0637101020', 'Villa Lynch', '06', 'General San Martín'),
    ('033dc6ad-437d-51ca-8b97-2e0baaadd317', '0637101021', 'Villa Maipú', '06', 'General San Martín'),
    ('c7655e86-b700-519b-9d10-5b22b75de92c', '0637101022', 'Va.María Irene de los Remedios Escalada', '06', 'General San Martín'),
    ('681d983f-bd9a-5272-aa22-dccbd0b3e23d', '0637101023', 'Va.Marqués Alejandro María de Aguado', '06', 'General San Martín'),
    ('dd50a255-ec15-5680-9796-6683cdd8418c', '0637101024', 'Villa Parque Presidente Figueroa Alcorta', '06', 'General San Martín'),
    ('14fdc70c-ce82-5b16-81f4-c085983999bb', '0637101025', 'Villa Parque San Lorenzo', '06', 'General San Martín'),
    ('dc3a536a-4a77-5e82-bcd8-7a16529bcb7d', '0637101026', 'Villa San Andrés', '06', 'General San Martín'),
    ('290694cb-249e-576f-8d07-5de4f0a667bb', '0637101027', 'Villa Yapeyú', '06', 'General San Martín')
ON CONFLICT (indec_id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('03a5f664-5f9e-5c38-a2c9-86be353aa4e5', '06385010', 'Baigorrita', '06', 'General Viamonte'),
    ('0b9bacd5-d993-56ca-a2c6-c6b883fbc9b0', '06385020', 'La Delfina', '06', 'General Viamonte'),
    ('1bb8085e-e1fc-5dbe-9358-479b66888be2', '06385030', 'Los Toldos', '06', 'General Viamonte'),
    ('f552e206-4651-5aa6-a25d-2396152fb019', '06385040', 'San Emilio', '06', 'General Viamonte'),
    ('be4ac25c-1b1b-5040-a2ec-c312c19838e9', '06385050', 'Zavalía', '06', 'General Viamonte'),
    ('ee481e47-9866-58ef-ae38-a0c17b299a75', '06392010', 'Banderaló', '06', 'General Villegas'),
    ('a7d9634a-8471-5c7d-8456-086a3dff6f3a', '06392020', 'Cañada Seca', '06', 'General Villegas'),
    ('df50a1e9-5330-551c-be7f-3d24d5598016', '06392030', 'Coronel Charlone', '06', 'General Villegas'),
    ('582bc03e-bdee-5ab4-839e-5ab572ebf644', '06392040', 'Emilio V. Bunge', '06', 'General Villegas'),
    ('49e32007-1281-530d-b0b1-b0279fc2f4c9', '06392050', 'General Villegas', '06', 'General Villegas'),
    ('87365f52-b6aa-5287-b41e-6357af45f535', '06392060', 'Massey', '06', 'General Villegas'),
    ('b661695d-5779-5cf6-a834-6cf9a7e66a40', '06392070', 'Pichincha', '06', 'General Villegas'),
    ('18597d10-cc8d-5d43-a970-90eac1870171', '06392080', 'Piedritas', '06', 'General Villegas'),
    ('27cfb77b-d996-5fb6-92bd-0f35310b4d62', '06392090', 'Santa Eleodora', '06', 'General Villegas'),
    ('37ae0980-efcf-5d2b-b35f-cccdd7f713d9', '06392100', 'Santa Regina', '06', 'General Villegas'),
    ('ff537444-772f-594d-970d-4d76034ded21', '06392110', 'Villa Saboya', '06', 'General Villegas'),
    ('1dbdb301-5c31-56ce-ac42-417738b3342a', '06392120', 'Villa Sauze', '06', 'General Villegas'),
    ('3210dd1a-b9af-5342-978f-3190bee47ed5', '06399010', 'Arroyo Venado', '06', 'Guaminí'),
    ('f924c216-34bd-5a05-b1f0-65a3c9a778ca', '06399020', 'Casbas', '06', 'Guaminí'),
    ('27994132-8a76-5d2a-891e-cb01c47e5cc7', '06399030', 'Garré', '06', 'Guaminí'),
    ('09bb1b2b-d007-5244-b953-52c7c57b6f67', '06399040', 'Guaminí', '06', 'Guaminí'),
    ('a4c1bcd4-e0c9-5b54-acb1-98550e59f153', '06399050', 'Laguna Alsina', '06', 'Guaminí'),
    ('8d94a8af-8a0c-5e00-bc46-7a4a0ea057c4', '06406010', 'Henderson', '06', 'Hipólito Yrigoyen'),
    ('e2215e1c-e015-533f-93a3-eb7a4b770f3d', '06406020', 'Herrera Vegas', '06', 'Hipólito Yrigoyen'),
    ('f4a5d638-47ea-5069-80a9-6a398c88edad', '06408010', 'Hurlingham', '06', 'Hurlingham'),
    ('87ed3bb7-06ec-575a-8b25-1f1f07b07c8e', '0640801001', 'Hurlingham', '06', 'Hurlingham'),
    ('57f5d1ba-bf9d-5c46-92b1-aa77067f2c40', '0640801002', 'Villa Santos Tesei', '06', 'Hurlingham'),
    ('9a2e2ec7-892a-55f6-93fb-e804c79baf6c', '0640801003', 'William C. Morris', '06', 'Hurlingham'),
    ('b40a0182-a1af-58bf-88d0-5b7d30bb6295', '06410010', 'Ituzaingó', '06', 'Ituzaingó'),
    ('bced3acd-4c4e-5c57-a38c-8a84b216ee37', '0641001001', 'Ituzaingó Centro', '06', 'Ituzaingó'),
    ('eaef8426-ae81-5e26-9778-37970000522c', '0641001002', 'Ituzaingó Sur', '06', 'Ituzaingó'),
    ('5fb5a8ca-c573-5860-b7ef-6f9a13e4b6f2', '0641001003', 'Villa Gobernador Udadondo', '06', 'Ituzaingó'),
    ('b9cda2e7-b7f4-5211-9597-5ae281aba059', '06412010', 'José C. Paz', '06', 'José C. Paz'),
    ('1b644c3a-da9f-5da6-a7d9-d78a7739be59', '0641201001', 'Del Viso', '06', 'José C. Paz'),
    ('a49325db-c607-52f6-b850-b55c07f07dfc', '0641201002', 'José C. Paz', '06', 'José C. Paz'),
    ('1d77078c-31a9-5145-9f06-13fe79b7847b', '0641201003', 'Tortuguitas', '06', 'José C. Paz'),
    ('a7b2ef67-7907-5012-8f14-bdd72d27ff96', '06413010', 'Agustín Roca', '06', 'Junín'),
    ('12e6cf0a-5596-580b-a29a-2e9a8a97fb3c', '06413020', 'Agustina', '06', 'Junín'),
    ('94dfbc12-070b-541b-af20-98b9cf238a66', '06413030', 'Balneario Laguna de Gómez', '06', 'Junín'),
    ('576ef826-ed1e-5b88-b645-0009c8e6385c', '06413040', 'Fortín Tiburcio', '06', 'Junín'),
    ('42744582-7d89-57c8-a08f-009d0ae7accd', '06413050', 'Junín', '06', 'Junín'),
    ('8588b4c0-7a68-5446-b4b2-eac9db5293a6', '06413060', 'Laplacette', '06', 'Junín'),
    ('2ca43452-9edd-59a5-afdf-daa78ace4850', '06413080', 'Saforcada', '06', 'Junín'),
    ('f6c427ee-7dce-581e-b195-d65d093cde37', '06420010', 'Las Toninas', '06', 'La Costa'),
    ('1a9df1b0-0e8d-541c-ae7e-9d72d54c4992', '06420020', 'Mar de Ajó - San Bernardo', '06', 'La Costa'),
    ('a8d086a7-547f-574c-ab51-763363edf96f', '0642002001', 'Aguas Verdes', '06', 'La Costa'),
    ('ce2be453-cf2c-59b9-bc89-1080aaf5bc38', '0642002002', 'Lucila del Mar', '06', 'La Costa'),
    ('dbfd0722-df24-5076-a123-6eb9b7307a8f', '0642002003', 'Mar de Ajó', '06', 'La Costa'),
    ('e797ea5e-2311-58e2-ab8b-c103152cb019', '0642002004', 'Mar de Ajó Norte', '06', 'La Costa'),
    ('911c81a3-795b-5ad2-b804-7b7370e2b414', '0642002005', 'San Bernardo', '06', 'La Costa'),
    ('d4932856-e117-535f-a78c-ef42bddb2811', '06420030', 'San Clemente del Tuyú', '06', 'La Costa'),
    ('8c9fc34c-4305-5d31-802f-ed7898d68626', '06420040', 'Santa Teresita - Mar del Tuyú', '06', 'La Costa'),
    ('12d6433c-e237-5831-a8a5-b7876499ada6', '0642004001', 'Mar del Tuyú', '06', 'La Costa'),
    ('f6615595-a0b5-583f-af6f-11b4a77524b4', '0642004002', 'Santa Teresita', '06', 'La Costa'),
    ('af1a87cc-4223-5bf4-bdf1-8200b3361381', '06427010', 'La Matanza', '06', 'La Matanza'),
    ('370313f9-1d9b-5444-8f07-f507e19b98fd', '0642701001', 'Aldo Bonzi', '06', 'La Matanza'),
    ('d3a59abc-2e9c-5ca4-bfb8-f40d895cb9f7', '0642701002', 'Ciudad Evita', '06', 'La Matanza'),
    ('63e63585-33b9-5e9a-9ca2-1052873ad564', '0642701003', 'González Catán', '06', 'La Matanza'),
    ('ed3cd7e1-810f-5634-ab15-087c54f19c4b', '0642701004', 'Gregorio de Laferrere', '06', 'La Matanza'),
    ('9c0d6b52-5896-5136-8c1a-e195cc67228e', '0642701005', 'Isidro Casanova', '06', 'La Matanza'),
    ('39a1a637-8351-5511-989a-438e7fb6ab6f', '0642701006', 'La Tablada', '06', 'La Matanza'),
    ('d73202bf-f8fb-536d-9bc2-5f7f2571e0ad', '0642701007', 'Lomas del Mirador', '06', 'La Matanza'),
    ('3a381aa9-7c25-5d01-8057-ae655423e859', '0642701008', 'Rafael Castillo', '06', 'La Matanza'),
    ('fe68c9bd-1211-518b-968e-de2e3320c1ff', '0642701009', 'Ramos Mejía', '06', 'La Matanza'),
    ('91e409bb-c54c-5f69-9a38-0b213c396877', '0642701010', 'San Justo', '06', 'La Matanza'),
    ('9692a64b-af22-5641-a615-4939378c00f5', '0642701011', 'Tapiales', '06', 'La Matanza'),
    ('7e8107ae-da95-52e9-8f43-3f3caf5ae0d3', '0642701012', '20 de Junio', '06', 'La Matanza'),
    ('b1ae42fe-43f3-5156-b2c0-18a0ed9679aa', '0642701013', 'Villa Eduardo Madero', '06', 'La Matanza'),
    ('8250f032-ed2b-56aa-bb76-11dc56c83338', '0642701014', 'Villa Luzuriaga', '06', 'La Matanza'),
    ('0cbb9116-b950-5b35-9f9d-087631ed0d37', '0642701015', 'Virrey del Pino', '06', 'La Matanza'),
    ('e3760af4-9e30-51e6-a51a-2b936b01f3f9', '06434010', 'Lanús', '06', 'Lanús'),
    ('cd9ceada-155b-5134-b353-1a1931f69e9d', '0643401001', 'Gerli', '06', 'Lanús'),
    ('e0385dc6-1263-5fcf-9771-11c0558f8d93', '0643401002', 'Lanús Este', '06', 'Lanús'),
    ('0fb8615c-caa1-550f-a741-56a84b0bfc83', '0643401003', 'Lanús Oeste', '06', 'Lanús'),
    ('25a331cd-ae37-5a27-9f2d-347faa61aba5', '0643401004', 'Monte Chingolo', '06', 'Lanús'),
    ('a3e54064-6249-5a4c-a1db-81b7a34c09e9', '0643401005', 'Remedios de Escalada de San Martín', '06', 'Lanús'),
    ('f32edb88-a597-5fbf-9071-5d245bd0502c', '0643401006', 'Valentín Alsina', '06', 'Lanús'),
    ('88959c47-29e4-552d-9579-54435026d3d5', '06441030', 'La Plata', '06', 'La Plata'),
    ('7ffc0100-1708-5a83-971b-5cb060b1fd24', '0644103001', 'Abasto', '06', 'La Plata'),
    ('0ed10ceb-1fcb-5b72-adf6-9ec711ce5c06', '0644103002', 'Ángel Etcheverry', '06', 'La Plata'),
    ('37a5290e-3809-5ae2-b587-bcb9ba6af4c5', '0644103003', 'Arana', '06', 'La Plata'),
    ('6254cfaa-ce8a-5d2d-8676-17eb18fb22fc', '0644103004', 'Arturo Seguí', '06', 'La Plata'),
    ('a6664bec-a3aa-51be-ab52-63cf2561220c', '0644103005', 'Barrio El Carmen Oeste', '06', 'La Plata'),
    ('0905c88c-f46a-5df5-af01-2340064a6fb1', '0644103006', 'Barrio Gambier', '06', 'La Plata'),
    ('10c1e703-d591-5e76-86bf-61c9e258b273', '0644103007', 'Barrio Las Malvinas', '06', 'La Plata'),
    ('fa823d5e-f0ee-594b-91d3-e85f8cbbc1c7', '0644103008', 'Barrio Las Quintas', '06', 'La Plata'),
    ('e69aa6f0-ad0c-5b91-b844-9ba192dff776', '0644103009', 'City Bell', '06', 'La Plata'),
    ('2dce3a32-52e7-56c3-869b-f34d7c0f181e', '0644103010', 'El Retiro', '06', 'La Plata'),
    ('8898fd0d-73a4-56df-b257-ca4b93f3fade', '0644103011', 'Joaquín Gorina', '06', 'La Plata'),
    ('e69a409d-6f9e-5012-8fe5-15bb716a8c20', '0644103012', 'José Hernández', '06', 'La Plata'),
    ('50eac56b-0641-5280-96c8-23fb0ba129a7', '0644103013', 'José Melchor Romero', '06', 'La Plata'),
    ('f7dadcb7-aa7e-5eca-a0bb-ff10c341aa9b', '0644103014', 'La Cumbre', '06', 'La Plata'),
    ('a7a83d7c-32a0-5497-aa31-31d88cfac756', '0644103015', 'La Plata', '06', 'La Plata'),
    ('2b664ce0-235b-5ff1-927e-780182f4445a', '0644103016', 'Lisandro Olmos', '06', 'La Plata'),
    ('c2af5e64-a420-5f48-9666-398e00ee7e24', '0644103017', 'Los Hornos', '06', 'La Plata'),
    ('d5ee68a1-bf32-5dc9-ae1f-4894d3c0aa28', '0644103018', 'Manuel B. Gonnet', '06', 'La Plata'),
    ('ca00645a-27b3-55e4-a3e7-abbf93cef6e5', '0644103019', 'Ringuelet', '06', 'La Plata'),
    ('7751400a-61bd-5c6d-8242-0eb6f606c8e9', '0644103020', 'Rufino de Elizalde', '06', 'La Plata'),
    ('03bd79de-045b-5eae-8d40-e17a0b558296', '0644103021', 'Tolosa', '06', 'La Plata'),
    ('4828efe1-ef70-56fd-9059-22772bb5ad28', '0644103022', 'Transradio', '06', 'La Plata'),
    ('e5b2806f-1824-53cc-b99d-a519d84d007b', '0644103023', 'Villa Elisa', '06', 'La Plata'),
    ('fb790d31-6f3a-5e67-9d27-ab8640cba02e', '0644103024', 'Villa Elvira', '06', 'La Plata'),
    ('843c4a21-9ea3-525a-b352-5ce738259088', '0644103025', 'Villa Garibaldi', '06', 'La Plata'),
    ('fbc419b2-bcf2-5d02-8604-1b55f6d20c42', '0644103026', 'Villa Montoro', '06', 'La Plata'),
    ('b6e1fb17-553a-58c9-b650-925769d6d60f', '0644103027', 'Villa Parque Sicardi', '06', 'La Plata'),
    ('8a80aeaa-69cd-5315-9f73-f1706568cfad', '06448010', 'Laprida', '06', 'Laprida'),
    ('ebc1a7c5-94aa-5129-8a87-a7b47f37e5a7', '06448020', 'Pueblo Nuevo', '06', 'Laprida'),
    ('97db4e76-6e7e-5425-af9d-6a892fe85d34', '06448030', 'Pueblo San Jorge', '06', 'Laprida'),
    ('228df9a7-7d54-57a7-b68e-1112f2a9be56', '06455010', 'Coronel Boerr', '06', 'Las Flores'),
    ('802be484-b91f-5a1a-ab2b-73c7b22d712a', '06455020', 'El Trigo', '06', 'Las Flores'),
    ('13c2b7b1-f3f5-5c7a-88e1-c3da31aadba4', '06455030', 'Las Flores', '06', 'Las Flores'),
    ('ffd1f588-bd87-5705-8202-8e90ea0fa3fd', '06455040', 'Pardo', '06', 'Las Flores'),
    ('1eb61e1e-7fb0-50dd-ac20-69a6f79b0206', '06462010', 'Alberdi Viejo', '06', 'Leandro N. Alem'),
    ('38b5fbaa-8b49-52c9-8461-c242f4a963f4', '06462020', 'El Dorado', '06', 'Leandro N. Alem'),
    ('02737eba-91c1-5383-9aab-3c03a66970ef', '06462030', 'Fortín Acha', '06', 'Leandro N. Alem'),
    ('2ca60c7c-7ad9-522f-ab26-ec8676790950', '06462040', 'Juan Bautista Alberdi', '06', 'Leandro N. Alem'),
    ('106f4632-cae8-56b7-a02e-598043b273c0', '06462050', 'Leandro N. Alem', '06', 'Leandro N. Alem'),
    ('1d80f365-c3d1-5e62-bc65-5ee0945ad49d', '06462060', 'Vedia', '06', 'Leandro N. Alem'),
    ('641f943b-5e8c-5665-bbe5-2fcbb8383dd4', '06466020', 'Manuel J. Cobo', '06', 'Lezama'),
    ('07afa151-81b1-51cb-9ea4-9bc8685436b9', '06469010', 'Arenaza', '06', 'Lincoln'),
    ('13981350-d3ed-531b-b712-98d8a48520c0', '06469020', 'Bayauca', '06', 'Lincoln'),
    ('6cb14202-f625-5201-ab49-609fc65f0b51', '06469030', 'Bermúdez', '06', 'Lincoln'),
    ('034b1f38-c0d0-5fd9-a1dd-efd24674b0bf', '06469040', 'Carlos Salas', '06', 'Lincoln'),
    ('7e16f612-6c3c-5bd8-9630-574305b27886', '06469050', 'Coronel Martínez de Hoz', '06', 'Lincoln'),
    ('e4eefc88-b5d9-5d54-b568-264d04b42539', '06469060', 'El Triunfo', '06', 'Lincoln'),
    ('13944bb5-66ed-54af-9add-324cd726f1e3', '06469070', 'Las Toscas', '06', 'Lincoln'),
    ('f76ca16c-d9ae-582b-909a-f9e7f328f8cb', '06469080', 'Lincoln', '06', 'Lincoln'),
    ('e859e251-f9b6-5c49-b14b-906c2be1c53f', '06469090', 'Pasteur', '06', 'Lincoln'),
    ('32aa21d4-a768-55d4-8fe6-d0247ce14f99', '06469100', 'Roberts', '06', 'Lincoln'),
    ('58e2a2dd-af67-54c4-b944-ee01f0a39580', '06469110', 'Triunvirato', '06', 'Lincoln'),
    ('562ed724-0594-59c7-8b6a-7a69f7f6af6a', '06476010', 'Arenas Verdes', '06', 'Lobería'),
    ('af3be81b-aee4-5a9a-a44e-58bba6e98235', '06476020', 'Licenciado Matienzo', '06', 'Lobería'),
    ('4367dc86-b8e3-5d74-8ca4-6b512d69494b', '06476030', 'Lobería', '06', 'Lobería'),
    ('ddaef66b-d6f9-55fd-ac60-e75255a6a6a4', '06476040', 'Pieres', '06', 'Lobería'),
    ('5cb5b995-35e5-594b-8ff3-68cc395a7e9a', '06476050', 'San Manuel', '06', 'Lobería'),
    ('60933926-5ebd-5e7e-b445-ba4dc160f222', '06476060', 'Tamangueyú', '06', 'Lobería'),
    ('eec3cf97-a4c4-5c39-b9ec-112489ce179e', '06483010', 'Antonio Carboni', '06', 'Lobos'),
    ('ca8fa5ce-a362-5233-8c58-e39f2ec43cc2', '06483020', 'Elvira', '06', 'Lobos'),
    ('070261ce-e4c4-5010-b2b7-5cfc38b79aaf', '06483030', 'Laguna de Lobos', '06', 'Lobos'),
    ('fb4d7b06-2e24-5919-9a78-b3c49a96404f', '06483040', 'Lobos', '06', 'Lobos'),
    ('422bfc79-fe6d-59c6-b8b9-ebc135c625dc', '06483050', 'Salvador María', '06', 'Lobos'),
    ('b3d13c30-210b-5544-9602-95a65b6a8fa9', '06490010', 'Lomas de Zamora', '06', 'Lomas de Zamora'),
    ('04e5c894-c622-527b-b778-bc29f31c8a67', '0649001001', 'Banfield', '06', 'Lomas de Zamora'),
    ('4fb73468-6d53-5bfb-8f70-affb2b49a24c', '0649001002', 'Llavallol', '06', 'Lomas de Zamora'),
    ('0ad53f75-a93e-5537-b28a-032339a2ece2', '0649001003', 'Lomas de Zamora', '06', 'Lomas de Zamora'),
    ('5f4c59fa-2d79-5378-ae9e-ffa85bc7f80a', '0649001004', 'Temperley', '06', 'Lomas de Zamora'),
    ('1fa74e30-52f9-572e-b1ef-c7c450bdfbbd', '0649001005', 'Turdera', '06', 'Lomas de Zamora'),
    ('fdcc076f-40c6-5ea7-bee5-aef1f82165f2', '0649001006', 'Villa Centenario', '06', 'Lomas de Zamora'),
    ('7845e522-abd4-511d-9fdc-45f1fc02ae94', '0649001007', 'Villa Fiorito', '06', 'Lomas de Zamora'),
    ('92f8bc03-0806-5d18-a9df-15ffa512ee91', '06497020', 'Carlos Keen', '06', 'Luján'),
    ('5d23abd4-cd8e-5dc2-ba6a-7a1c433aaa20', '06497060', 'Luján', '06', 'Luján'),
    ('ea23b6a5-a1c3-56ad-8f84-1247a7011101', '0649706001', 'Barrio Las Casuarinas', '06', 'Luján'),
    ('2d9d7c60-7630-5a5c-a8d0-dbfa070ace4f', '0649706002', 'Cortines', '06', 'Luján'),
    ('8de1357a-d1b5-50a6-827d-935698b84a80', '0649706003', 'Lezica y Torrezuri', '06', 'Luján'),
    ('930b6a5c-09ff-5dd7-9807-151290e129f1', '0649706004', 'Luján', '06', 'Luján'),
    ('dedc7150-2a69-5d02-9a15-4f7de202ea20', '0649706005', 'Villa Flandria Norte', '06', 'Luján'),
    ('2adf1afd-0168-5a9e-ace2-d2b4575e51a8', '0649706006', 'Villa Flandria Sur', '06', 'Luján'),
    ('8993dd96-0023-5bdb-87e9-8377f3ca8de0', '0649706007', 'Country Club Las Praderas', '06', 'Luján'),
    ('e68d0884-b447-5aa4-85e1-47ec479845d4', '0649706008', 'Open Door', '06', 'Luján'),
    ('4f0623d1-83da-5125-b368-323ea5524751', '06497070', 'Olivera', '06', 'Luján'),
    ('736000e0-07c2-5a3f-8617-698ce2af15ee', '06497090', 'Torres', '06', 'Luján'),
    ('47eee542-ff19-5e23-b61e-27cc7b82ffa0', '06505010', 'Atalaya', '06', 'Magdalena'),
    ('3465fafd-ae35-5a34-9092-8499f24feb00', '06505020', 'General Mansilla', '06', 'Magdalena'),
    ('86a1b6e8-19d0-5512-a7b4-d8abe3d69bb5', '06505030', 'Los Naranjos', '06', 'Magdalena'),
    ('9fc4b741-0acb-5202-9b52-cf2a17ccc2a1', '06505040', 'Magdalena', '06', 'Magdalena'),
    ('34675ff3-3ddb-5a8b-acbf-6ae21aa88289', '06505050', 'Roberto J. Payró', '06', 'Magdalena'),
    ('943d547b-6bc2-5da4-a75b-b6c841cdb8e8', '06505060', 'Vieytes', '06', 'Magdalena'),
    ('58cdf743-d544-59a0-a1ad-b0ea6f9e92c5', '06511010', 'Las Armas', '06', 'Maipú'),
    ('2b3ca1a5-818e-5205-94af-fdaa501e2f20', '06511020', 'Maipú', '06', 'Maipú'),
    ('3f70bd49-7be2-5d91-bf8c-f86d9c685517', '06511030', 'Santo Domingo', '06', 'Maipú'),
    ('4b00d601-f89c-54a8-ad63-c5a240bea5a5', '06515010', 'Malvinas Argentinas', '06', 'Malvinas Argentinas'),
    ('db2e4779-0a91-51d9-98b4-fa9bf4bbd5f1', '0651501001', 'Área de Promoción El Triángulo', '06', 'Malvinas Argentinas'),
    ('acbc36a8-1d86-53a2-8e75-07978765724b', '0651501002', 'Grand Bourg', '06', 'Malvinas Argentinas'),
    ('9e0e33fb-f651-5619-9a75-30e199b93460', '0651501003', 'Ingeniero Adolfo Sourdeaux', '06', 'Malvinas Argentinas'),
    ('284c38a8-12cd-5697-b112-a201a6e1985b', '0651501004', 'Ingeniero Pablo Nogués', '06', 'Malvinas Argentinas'),
    ('7804d86c-4a02-5160-a590-6ac84407170e', '0651501005', 'Los Polvorines', '06', 'Malvinas Argentinas'),
    ('9f6d2eaa-76ec-5176-b196-e08f27150d19', '0651501006', 'Malvinas Argentinas', '06', 'Malvinas Argentinas'),
    ('0e9d0e40-54a7-5f16-8f67-a99bb690c84e', '0651501007', 'Tortuguitas', '06', 'Malvinas Argentinas'),
    ('37b11a37-21f7-5a2b-b198-1ba0a5ce1dd2', '0651501008', 'Villa de Mayo', '06', 'Malvinas Argentinas'),
    ('e086d091-cad0-5a85-85ae-e0794b219088', '06518010', 'Coronel Vidal', '06', 'Mar Chiquita'),
    ('316f611b-7013-5660-8e45-9e15f7c7fc89', '06518020', 'General Pirán', '06', 'Mar Chiquita'),
    ('85ac4422-c65a-5e48-b6e1-456f606e3f26', '06518030', 'La Armonía', '06', 'Mar Chiquita'),
    ('2e9ac996-9a34-5b1f-bc4e-f7c6913c6435', '06518040', 'Mar Chiquita', '06', 'Mar Chiquita'),
    ('df4ae30e-0a8d-5747-b2e5-d70393c79cea', '06518050', 'Mar de Cobo', '06', 'Mar Chiquita'),
    ('7f5de089-5f5d-57d7-86b0-7d57663dde69', '0651805001', 'La Baliza', '06', 'Mar Chiquita'),
    ('6eecd752-23d8-5b09-93df-62bbabfc3777', '0651805002', 'La Caleta', '06', 'Mar Chiquita'),
    ('c6474907-fff7-50d4-a268-ee0844c645a5', '0651805003', 'Mar de Cobo', '06', 'Mar Chiquita'),
    ('744f4f46-c1cf-5822-8285-37c779423ab9', '06518060', 'Santa Clara del Mar', '06', 'Mar Chiquita'),
    ('c208f048-4e1c-5c27-b51f-b9b2e9a306e5', '0651806001', 'Atlántida', '06', 'Mar Chiquita'),
    ('f1c17123-a4ef-5533-b3fe-5899e3ff017b', '0651806002', 'Camet Norte', '06', 'Mar Chiquita'),
    ('f0fb6f07-0a9c-526e-83ec-6506bb173801', '0651806003', 'Frente Mar', '06', 'Mar Chiquita'),
    ('506b1d8a-db31-5dc9-a28d-98eabbd82661', '0651806004', 'Playa Dorada', '06', 'Mar Chiquita'),
    ('50da9292-4dfd-52da-8fe0-283e5e1076d1', '0651806005', 'Santa Clara del Mar', '06', 'Mar Chiquita'),
    ('7801d626-e945-5e90-9b46-74498bec267e', '0651806006', 'Santa Elena', '06', 'Mar Chiquita'),
    ('a4818be8-3b73-5d85-891c-24ac75ed6948', '06518070', 'Vivoratá', '06', 'Mar Chiquita'),
    ('46fa162f-ecb7-5487-96ae-ed7323d0c1f1', '06525020', 'Marcos Paz', '06', 'Marcos Paz'),
    ('aceb2b35-c945-539f-b14b-7bcf40e1c9a1', '0652502001', 'Barrio Lisandro de la Torre y Santa Marta', '06', 'Marcos Paz'),
    ('7492a68b-3c3d-5b2b-9103-d9f0cf2b2c9f', '0652502002', 'Marcos Paz', '06', 'Marcos Paz'),
    ('9d437d51-186f-5031-b11c-27ca99c09212', '06532010', 'Gowland', '06', 'Mercedes'),
    ('53a88f12-1f05-5bed-9fa7-7ce60aa1e32b', '06532020', 'Mercedes', '06', 'Mercedes'),
    ('866d3ba4-62f2-5edc-b949-9aa494b1704b', '06532030', 'Jorge Born', '06', 'Mercedes'),
    ('1944d59d-4fa5-55eb-ad8a-b2abacad953c', '06539010', 'Merlo', '06', 'Merlo'),
    ('3e5717c6-494f-556f-872e-017aaf01e4ac', '0653901001', 'Libertad', '06', 'Merlo'),
    ('57f35650-dc33-54f5-a968-67ce460cba0f', '0653901002', 'Mariano Acosta', '06', 'Merlo'),
    ('a227cf9f-a826-5afd-b8fd-234e3a9d4f22', '0653901003', 'Merlo', '06', 'Merlo'),
    ('06720d76-720d-521f-ba7f-0cfb6a46e96a', '0653901004', 'Pontevedra', '06', 'Merlo'),
    ('bcf1d2d8-ca90-5097-b3ad-c50a94b3e0f4', '0653901005', 'San Antonio de Padua', '06', 'Merlo'),
    ('1a74bedb-d538-54cd-b471-793ed27ea9f2', '06547010', 'Abbott', '06', 'Monte'),
    ('10e22fbe-ef5b-5d35-8ed4-36163303f5eb', '06547020', 'San Miguel del Monte', '06', 'Monte'),
    ('0f50c6f2-d4d4-5f77-b9c8-0d1352dccf70', '06547030', 'Zenón Videla Dorna', '06', 'Monte'),
    ('4fe40393-5c63-5156-bdd2-91c411fbd655', '06553010', 'Balneario Sauce Grande', '06', 'Monte Hermoso'),
    ('11418ba3-2c8b-50b1-ab08-43f40ee00eea', '06553020', 'Monte Hermoso', '06', 'Monte Hermoso'),
    ('50e272ca-1f78-5836-b29a-b22e346b306e', '06560010', 'Moreno', '06', 'Moreno'),
    ('e079fe0e-435a-5b03-8e53-97dddcb8cdf8', '0656001001', 'Cuartel V', '06', 'Moreno'),
    ('a74b8b94-3670-5d56-88ec-a353c602a2c7', '0656001002', 'Francisco Álvarez', '06', 'Moreno'),
    ('b95391c9-3c60-5edc-a653-a9ecfe862456', '0656001003', 'La Reja', '06', 'Moreno'),
    ('838e08b3-bb9c-53d3-8009-d228bbaa6df0', '0656001004', 'Moreno', '06', 'Moreno'),
    ('b7a28dd3-3526-57c7-8429-4fad10096f88', '0656001005', 'Paso del Rey', '06', 'Moreno'),
    ('5d4ca560-1309-58d2-83f8-6abc484990ee', '0656001006', 'Trujui', '06', 'Moreno'),
    ('788834be-f31b-5b9c-8ac0-f189f24db06a', '06568010', 'Morón', '06', 'Morón'),
    ('83226329-932e-5824-a507-2bc4ded9dd1f', '0656801001', 'Castelar', '06', 'Morón'),
    ('4eca76af-76d3-5ea5-a453-169439318041', '0656801002', 'El Palomar', '06', 'Morón'),
    ('959319a0-0560-599a-8275-b4ce4817b26c', '0656801003', 'Haedo', '06', 'Morón'),
    ('6f688312-f1ff-585b-891d-c586daf2a23f', '0656801004', 'Morón', '06', 'Morón'),
    ('93beed66-ca60-5652-b364-2aa06a6c61ba', '0656801005', 'Villa Sarmiento', '06', 'Morón'),
    ('98519723-7a1e-5343-ba46-bdf0a6e98334', '06574010', 'José Juan Almeyra', '06', 'Navarro'),
    ('79ebb36b-abc1-5ad6-829e-c149b0f49e4b', '06574020', 'Las Marianas', '06', 'Navarro'),
    ('d957ace7-fc82-513a-9f83-339e8923aa53', '06574030', 'Navarro', '06', 'Navarro'),
    ('57518451-581e-55b9-9304-d24c9e0719fd', '06574040', 'Villa Moll', '06', 'Navarro'),
    ('5799b74c-7ed4-50d4-8292-e143c99f6778', '06581010', 'Claraz', '06', 'Necochea'),
    ('deb1e6b4-0c5f-5f52-bd90-ca56311a7f1d', '06581030', 'Juan N. Fernández', '06', 'Necochea'),
    ('a5f8638a-9244-5943-b7a8-f6c00ecae95a', '06581040', 'Necochea - Quequén', '06', 'Necochea'),
    ('66ba3c61-be62-5403-9722-528dd313badb', '0658104001', 'Necochea', '06', 'Necochea'),
    ('4c836176-3bc1-529a-983d-645b64006c6c', '0658104002', 'Quequén', '06', 'Necochea'),
    ('becdcf93-cdff-5689-be2e-d7dbdf258c18', '0658104003', 'Costa Bonita', '06', 'Necochea'),
    ('fd1660ef-eafc-53b5-a59d-546eeac3b8fa', '06581050', 'Nicanor Olivera', '06', 'Necochea'),
    ('c3d0c673-2f74-5c54-8ba4-06d569298066', '06581060', 'Ramón Santamarina', '06', 'Necochea'),
    ('daae802d-2b2f-5d2c-8922-c583186c1ced', '06588010', 'Alfredo Demarchi', '06', '9 de Julio'),
    ('bec17366-6b14-5747-a34b-0df2200fafee', '06588020', 'Carlos María Naón', '06', '9 de Julio'),
    ('ccc87117-d303-556b-b41a-19ab541a9e42', '06588030', '12 de Octubre', '06', '9 de Julio'),
    ('707cfbd7-8d19-5302-b74e-9e51f81cfa51', '06588040', 'Dudignac', '06', '9 de Julio'),
    ('3b3e964b-63b3-5eab-890d-f6806c682539', '06588050', 'La Aurora', '06', '9 de Julio'),
    ('1eaa27f9-6cb2-5459-a3d0-4999e1531bf7', '06588060', 'Manuel B. Gonnet', '06', '9 de Julio'),
    ('b534f764-3bda-56a2-934f-05dba2b18f5c', '06588070', 'Marcelino Ugarte', '06', '9 de Julio'),
    ('18dfb7c3-f06f-5827-86a4-a3f33a449042', '06588080', 'Morea', '06', '9 de Julio'),
    ('48c476f5-7898-5077-abea-d6c4c966649e', '06588090', 'Norumbega', '06', '9 de Julio'),
    ('25d19cb5-7d4c-525b-b67c-d686001414de', '06588100', '9 de Julio', '06', '9 de Julio'),
    ('d2347545-4750-52e5-86e8-b6c3f01211af', '06588110', 'Patricios', '06', '9 de Julio'),
    ('d3a339cc-0bef-5b19-bac1-be575d16c9e8', '06588120', 'Villa Fournier', '06', '9 de Julio'),
    ('e87bda17-a986-5f61-b2c1-73420aa8aabd', '06595040', 'Colonia San Miguel', '06', 'Olavarría'),
    ('9c2ffff7-c0e2-5dbc-b823-217b62e23ff9', '06595050', 'Espigas', '06', 'Olavarría'),
    ('2c116182-c7ff-5e3b-a935-15ea648c1a53', '06595060', 'Hinojo', '06', 'Olavarría'),
    ('a368b1ad-bf62-509b-96b1-d3e7aef0efd7', '0659506001', 'Colonia Hinojo', '06', 'Olavarría'),
    ('a09d5f2c-2cc3-5497-b2e0-cff061534791', '0659506002', 'Hinojo', '06', 'Olavarría'),
    ('eb9a4d1d-ae15-5c97-bef1-313e44fc3ddf', '06595070', 'Olavarría', '06', 'Olavarría'),
    ('1816a9e5-d6ce-5ef8-ad78-a352676c13c3', '06595080', 'Recalde', '06', 'Olavarría'),
    ('7e2b6da4-919f-5bb2-85e0-0fe3f450dcfd', '06595090', 'Santa Luisa', '06', 'Olavarría'),
    ('0cc4a5ab-a7f1-5984-9289-aeb6e6c60702', '06595100', 'Sierra Chica', '06', 'Olavarría'),
    ('44987d56-189e-5b5d-8172-5ad38b4f8753', '06595110', 'Sierras Bayas', '06', 'Olavarría'),
    ('0d0eb8ae-3961-5d5e-b838-b735f3a4e3ca', '0659511001', 'Sierras Bayas', '06', 'Olavarría'),
    ('8f8dbc4d-01e5-5841-bd53-5558f3e3d952', '0659511002', 'Villa Arrieta', '06', 'Olavarría'),
    ('ed487be8-dc77-5093-9178-2c0b34d33a3b', '06595120', 'Villa Alfredo Fortabat', '06', 'Olavarría'),
    ('aac19566-536b-58e4-a457-f5f60a9ab994', '06595130', 'Villa La Serranía', '06', 'Olavarría'),
    ('d9440a40-468a-59fd-95a4-72a22d421313', '06602010', 'Bahía San Blas', '06', 'Patagones'),
    ('0646b1d9-e757-5bae-98ab-b0f7b51cf2d7', '06602020', 'Cardenal Cagliero', '06', 'Patagones'),
    ('39acd1cd-dbe6-5705-aeab-01b5784aa825', '06602030', 'Carmen de Patagones', '06', 'Patagones'),
    ('a974fcfb-ef86-5ddd-a1d9-672fbb59149d', '06602040', 'José B. Casas', '06', 'Patagones'),
    ('8fe502fc-e88b-5e70-aac1-d25e3d965452', '06602050', 'Juan A. Pradere', '06', 'Patagones'),
    ('37d5878c-3509-5395-afbf-9406b1a7439e', '06602060', 'Stroeder', '06', 'Patagones'),
    ('40fd1549-ecb9-5828-a4a3-a5f9f7bd0dfe', '06602070', 'Villalonga', '06', 'Patagones'),
    ('1f5a2589-cac3-5470-b770-5c4115563f44', '06609010', 'Capitán Castro', '06', 'Pehuajó'),
    ('44dbb9a3-8d67-5825-8fbf-bbf892d9c2bc', '06609020', 'San Esteban', '06', 'Pehuajó'),
    ('4072cd37-1bb1-5e0c-967b-ae46adede7b0', '06609030', 'Francisco Madero', '06', 'Pehuajó'),
    ('3653dcab-8e3b-547d-a243-cedf3b479178', '06609040', 'Juan José Paso', '06', 'Pehuajó'),
    ('0b3c7122-b36e-576c-9831-4ede77d2d4e6', '06609050', 'Magdala', '06', 'Pehuajó'),
    ('8a542dd0-ea9a-5d71-9048-5d9fdc383c99', '06609060', 'Mones Cazón', '06', 'Pehuajó'),
    ('bd248d86-2bbf-562c-a981-0b83b4000d11', '06609070', 'Nueva Plata', '06', 'Pehuajó'),
    ('7c6010b4-ac3b-56f2-8516-d1bf8b9b1de0', '06609080', 'Pehuajó', '06', 'Pehuajó'),
    ('bdc2ff37-3d45-5e6b-81d4-88cf9d173212', '06609090', 'San Bernardo', '06', 'Pehuajó'),
    ('061344c9-d917-5b36-96f9-d97e2a50be38', '06616010', 'Bocayuva', '06', 'Pellegrini'),
    ('451b3866-9428-57f7-b742-7423e1e5b1d4', '06616020', 'De Bary', '06', 'Pellegrini'),
    ('1f62c309-c367-5241-b6fd-c073ab19aafa', '06616030', 'Pellegrini', '06', 'Pellegrini'),
    ('40fcbc21-ec13-5c8a-a0f1-5495dfb7c38b', '06623010', 'Acevedo', '06', 'Pergamino'),
    ('41f12a8b-c139-509f-ae69-664006b7e490', '06623020', 'Fontezuela', '06', 'Pergamino'),
    ('4b0bbb9f-7ca8-547c-b087-ec9d483aa3c5', '06623030', 'Guerrico', '06', 'Pergamino'),
    ('3ce02543-2149-5e7d-8d15-fc7ca4dc2eea', '06623040', 'Juan A. de la Peña', '06', 'Pergamino'),
    ('7c98aea7-6ff3-5d9d-9bcf-74ceac192d92', '06623050', 'Juan Anchorena', '06', 'Pergamino'),
    ('0ac2c010-aefa-52fd-9d18-7065a1286ab6', '06623060', 'La Violeta', '06', 'Pergamino'),
    ('f2740190-d06c-53b3-a183-6597eae73df0', '06623070', 'Manuel Ocampo', '06', 'Pergamino'),
    ('6a84bf65-3d5c-5dc5-a23d-c7352fa00f34', '06623080', 'Mariano Benítez', '06', 'Pergamino'),
    ('538265e6-73f1-5792-a40e-e721d795ca33', '06623090', 'Mariano H. Alfonzo', '06', 'Pergamino'),
    ('8f4fadce-e34d-513d-9239-acf144b775d5', '06623100', 'Pergamino', '06', 'Pergamino'),
    ('5d0a994d-1066-55d3-abdf-df9177532443', '06623110', 'Pinzón', '06', 'Pergamino'),
    ('bc50a38f-1999-5849-8384-b5178256045d', '06623120', 'Rancagua', '06', 'Pergamino'),
    ('666c8447-b95f-5021-bd5a-78b1bf1a51ea', '06623130', 'Villa Angélica', '06', 'Pergamino'),
    ('69aabe8f-52a3-5ab6-a0d2-aa7a8bd6b3c7', '06623140', 'Villa San José', '06', 'Pergamino'),
    ('f08e2002-02b0-599d-a020-c275f1e87b77', '06630010', 'Casalins', '06', 'Pila'),
    ('bd8f7b2d-d911-5ea0-b709-317036db60af', '06630020', 'Pila', '06', 'Pila'),
    ('a2008be7-a515-5dd6-b79b-c8ede8cbab7d', '06638040', 'Pilar', '06', 'Pilar'),
    ('7670c906-f8c1-5d05-a569-40c4740e2d52', '0663804001', 'Del Viso', '06', 'Pilar'),
    ('ca66c4ec-6c0b-5c73-b8d7-10c0113ca2d5', '0663804002', 'Fátima', '06', 'Pilar'),
    ('bb6fe104-f2bf-5c12-9531-dc19d4daa71e', '0663804003', 'La Lonja', '06', 'Pilar'),
    ('d1a85a4d-249b-554d-aaa3-2246f5ac1933', '0663804004', 'Los Cachorros', '06', 'Pilar'),
    ('abbb9add-dcd7-5faa-8b4c-d1f2d5a7d6a7', '0663804005', 'Manzanares', '06', 'Pilar'),
    ('8be3726a-e922-54cc-80b6-b95c7497e397', '0663804006', 'Manzone', '06', 'Pilar'),
    ('1dad8dfd-99a5-5ba7-bb13-fcedbbdd62a4', '0663804007', 'Maquinista F. Savio Oeste', '06', 'Pilar'),
    ('45668dfa-9ddf-57b2-b760-0460cf6aa8dd', '0663804008', 'Pilar', '06', 'Pilar'),
    ('40627bf3-b5a4-5e34-885a-706f98bba85a', '0663804009', 'Presidente Derqui', '06', 'Pilar'),
    ('1a0aebc6-7001-5c13-b44a-3ab6e706412c', '0663804010', 'Roberto de Vicenzo', '06', 'Pilar'),
    ('d02b2849-50b9-5810-9538-3221d4e90489', '0663804011', 'Santa Teresa', '06', 'Pilar'),
    ('ea1e9cd7-7eab-5ce5-b0e4-2a3ca1fbab07', '0663804012', 'Tortuguitas', '06', 'Pilar'),
    ('c609254e-aa96-53a7-8e8f-3efa2228b6eb', '0663804013', 'Villa Astolfi', '06', 'Pilar'),
    ('7e5c933d-1d00-553c-b909-37dad2a19da4', '0663804014', 'Villa Rosa', '06', 'Pilar'),
    ('fd12886b-566e-5535-ba2c-47596696bad8', '0663804015', 'Zelaya', '06', 'Pilar'),
    ('b96c06be-6910-5283-b38d-15fef21edea4', '06644010', 'Pinamar', '06', 'Pinamar'),
    ('3ad48c96-099a-5937-beb3-29a66b991069', '0664401001', 'Cariló', '06', 'Pinamar'),
    ('adcb4d68-63b4-530a-8ee7-19b3404719ab', '0664401002', 'Ostende', '06', 'Pinamar'),
    ('689b5e1e-0295-5545-bd6e-30bbfad2277b', '0664401003', 'Pinamar', '06', 'Pinamar'),
    ('8a7d9ddc-d064-55a6-acba-07d851f7a34f', '0664401004', 'Valeria del Mar', '06', 'Pinamar'),
    ('33a48613-bf20-5396-b1ea-9d31faf28273', '06648010', 'Presidente Perón', '06', 'Presidente Perón'),
    ('a19297f9-8a0b-5d83-b924-8f5b9d110374', '0664801001', 'Barrio América Unida', '06', 'Presidente Perón'),
    ('2162b076-0546-510f-adab-f61368ba023c', '0664801002', 'Guernica', '06', 'Presidente Perón'),
    ('95cf6666-364d-55a7-9273-0e6f2baf3ecc', '06651010', 'Azopardo', '06', 'Puán'),
    ('06c8e9a4-be96-5bdb-833a-0669bd92f257', '06651020', 'Bordenave', '06', 'Puán'),
    ('f8bb05ee-2ee1-56a2-b5c7-a15222dfdd8d', '06651030', 'Darregueira', '06', 'Puán'),
    ('32b864c2-8fa9-51fc-ab57-7b46ca729723', '06651040', '17 de Agosto', '06', 'Puán'),
    ('36fe78a8-5526-551c-9de5-de13d09b1ca0', '06651050', 'Estela', '06', 'Puán'),
    ('b54cb406-60fd-5594-b71f-f9dc12dbd8a5', '06651060', 'Felipe Solá', '06', 'Puán'),
    ('396ff9f0-e61e-58b9-bf81-501f3dcc8b20', '06651070', 'López Lecube', '06', 'Puán'),
    ('ab03599a-852a-573a-9321-626314a3163c', '06651080', 'Puán', '06', 'Puán'),
    ('95adbf92-eb0c-5097-a052-f8586a9fa37e', '06651090', 'San Germán', '06', 'Puán'),
    ('13ed086f-9180-558b-ab0d-065f2109da68', '06651100', 'Villa Castelar', '06', 'Puán'),
    ('f949ac45-593a-58d5-aa2e-ef2aa49f52ad', '06651110', 'Villa Iris', '06', 'Puán'),
    ('b5b7aaf6-11f8-5b54-8665-0bbc12d62fcb', '06655010', 'Alvarez Jonte', '06', 'Punta Indio'),
    ('eac6872d-9850-5661-bc8b-33616b9d8bf9', '06655030', 'Pipinas', '06', 'Punta Indio'),
    ('7baa6859-f09b-54e1-a275-899b4bd1bffe', '06655040', 'Punta Indio', '06', 'Punta Indio'),
    ('277ff1ef-2761-50fe-8321-d4ccbf2f12be', '06655050', 'Verónica', '06', 'Punta Indio'),
    ('b67711ff-787f-507b-b6e1-563247e674d4', '06658010', 'Quilmes', '06', 'Quilmes'),
    ('72c29456-b97e-5f55-ae9e-68f4c0c38383', '0665801001', 'Bernal', '06', 'Quilmes'),
    ('e512c36c-4563-51cd-860a-4233f1d0c07b', '0665801002', 'Bernal Oeste', '06', 'Quilmes'),
    ('ab8df510-9fbd-5405-86a2-cce460626829', '0665801003', 'Don Bosco', '06', 'Quilmes'),
    ('d3ba8034-5f73-5a22-a7a3-2b302865676b', '0665801004', 'Ezpeleta', '06', 'Quilmes'),
    ('1339d3ac-03e6-5fdf-9b15-7351ae0e5dbc', '0665801005', 'Ezpeleta Oeste', '06', 'Quilmes'),
    ('5968209a-0d61-58fe-91ce-816e987b62b4', '0665801006', 'Quilmes', '06', 'Quilmes'),
    ('5fc4f057-06dd-523d-be6b-bd698e9a6e94', '0665801007', 'Quilmes Oeste', '06', 'Quilmes'),
    ('11b7097a-934b-5b5d-abd5-13d16faa6fc3', '0665801008', 'San Francisco Solano', '06', 'Quilmes'),
    ('cdf692c1-41eb-5d11-a437-1df108e8bbab', '0665801009', 'Villa La Florida', '06', 'Quilmes'),
    ('87cf8be6-2585-5602-93ca-6bd279eaa2e7', '06665010', 'El Paraíso', '06', 'Ramallo'),
    ('a7cb39c4-60a8-54ef-bcd0-1649f7340a28', '06665020', 'Las Bahamas', '06', 'Ramallo'),
    ('6f28163f-ffe9-5324-ad00-6f29f3017cb0', '06665030', 'Pérez Millán', '06', 'Ramallo'),
    ('71869349-ecb0-5429-983a-8c1ff2bfc675', '06665040', 'Ramallo', '06', 'Ramallo'),
    ('6a85ffab-e24e-5e25-ad8a-8e8d08f91d89', '06665050', 'Villa General Savio', '06', 'Ramallo'),
    ('c0e31f72-9dd4-573b-9e84-f20f89673374', '06665060', 'Villa Ramallo', '06', 'Ramallo'),
    ('456640ea-3654-5716-a463-d588ac854028', '06672010', 'Rauch', '06', 'Rauch'),
    ('bb56a41b-d835-5b52-87a5-7029a27bc817', '06679010', 'América', '06', 'Rivadavia'),
    ('042771b9-1a8b-5bdc-84d7-05cdfb91e731', '06679020', 'Fortín Olavarría', '06', 'Rivadavia'),
    ('f000d0fa-b27f-50a7-8dae-8d334ef8a77f', '06679030', 'González Moreno', '06', 'Rivadavia'),
    ('4f6f5a73-822d-5a90-afed-585fcef3128f', '06679040', 'Mira Pampa', '06', 'Rivadavia'),
    ('e3c5f00c-586d-5a16-9792-5b4649c3191b', '06679050', 'Roosevelt', '06', 'Rivadavia'),
    ('f837467e-3b4e-57d4-9723-a94aca7cb03b', '06679060', 'San Mauricio', '06', 'Rivadavia'),
    ('61f4a9e7-c23e-562d-bbef-355ac8b21be0', '06679070', 'Sansinena', '06', 'Rivadavia'),
    ('e37707b5-8d0d-5113-a566-19a1fdfca8d6', '06679080', 'Sundblad', '06', 'Rivadavia'),
    ('8feeda84-ef8c-5cf1-ad82-b9412259202c', '06686010', 'La Beba', '06', 'Rojas'),
    ('2101ff4f-a604-5998-bebf-f0455fc5cf02', '06686020', 'Las Carabelas', '06', 'Rojas'),
    ('bc7ee236-7206-52b7-95ec-4bb9c143e9aa', '06686030', 'Los Indios', '06', 'Rojas'),
    ('187bf4f4-aa65-5668-83ca-b7751596b902', '06686040', 'Rafael Obligado', '06', 'Rojas'),
    ('b69d18b2-8799-57eb-946e-d01cd02541e7', '06686050', 'Roberto Cano', '06', 'Rojas'),
    ('6e6bf8bb-3984-5e00-a404-67801141c960', '06686060', 'Rojas', '06', 'Rojas'),
    ('7fd296a8-cbda-5463-bc6e-1c3a6b60ce5f', '0668606001', 'Barrio Las Margaritas', '06', 'Rojas'),
    ('49908b34-e3f3-5952-b8cd-add5299641cc', '0668606002', 'Rojas', '06', 'Rojas'),
    ('7e64d504-10a9-5a18-abb1-b1ec38990ecd', '0668606003', 'Villa Parque Cecir', '06', 'Rojas'),
    ('bb9b2630-521e-52db-8576-f6d53a207067', '06686070', 'Sol de Mayo', '06', 'Rojas'),
    ('2af99879-5669-5de8-af85-36856f0d5e91', '06686080', 'Villa Manuel Pomar', '06', 'Rojas'),
    ('98a3ea45-a200-5222-91b5-493cdb620214', '06693010', 'Carlos Beguerie', '06', 'Roque Pérez'),
    ('2eb2f652-c852-5adc-8030-a64c18f8dddb', '06693020', 'Roque Pérez', '06', 'Roque Pérez'),
    ('5f1672c3-db1f-5dae-bba3-b9f63faf0dd4', '06700010', 'Arroyo Corto', '06', 'Saavedra'),
    ('9c2efaf1-9747-5035-8ef8-7ff1e06ae683', '06700020', 'Colonia San Martín', '06', 'Saavedra'),
    ('f118cda9-b7f9-5e87-b19c-c7e531f9d94d', '06700030', 'Dufaur', '06', 'Saavedra'),
    ('617fd655-f55e-5bc4-97c8-7a3e5373eb74', '06700040', 'Espartillar', '06', 'Saavedra'),
    ('fe4d1af3-2792-59d2-a046-448a710fbe36', '06700050', 'Goyena', '06', 'Saavedra'),
    ('6dcd492a-0aef-5087-951d-809625aa7aa2', '06700060', 'Pigüé', '06', 'Saavedra'),
    ('3911e2a5-7f48-5220-a2a7-12e1bba7f2d6', '06700070', 'Saavedra', '06', 'Saavedra'),
    ('66ce18e2-710e-57e6-8854-8cb0b9d6e38e', '06707010', 'Álvarez de Toledo', '06', 'Saladillo'),
    ('6128159a-c576-5e3c-ac42-ccec378febf3', '06707030', 'Cazón', '06', 'Saladillo'),
    ('00fe19af-98d3-51bf-a624-6d94c8e6ace4', '06707040', 'Del Carril', '06', 'Saladillo'),
    ('a49ad35b-e8d9-5f90-9585-121da16c4220', '06707050', 'Polvaredas', '06', 'Saladillo'),
    ('b00d6600-8cbd-57d2-9d9d-6a1f55d6fc70', '06714010', 'Arroyo Dulce', '06', 'Salto'),
    ('6ef13c96-d08e-5e40-a5f9-28a168b83b12', '06714020', 'Berdier', '06', 'Salto'),
    ('e0c936d6-5e24-5510-8da1-fa532bb4b100', '06714030', 'Gahan', '06', 'Salto'),
    ('65397e91-8035-5eca-a896-1f2f13070ef8', '06714040', 'Inés Indart', '06', 'Salto'),
    ('814a35aa-2db3-517f-97cf-940d5ff748d0', '06714050', 'La Invencible', '06', 'Salto'),
    ('71393671-756a-5e70-a705-5fbcaf4185f9', '06714060', 'Salto', '06', 'Salto'),
    ('98375d65-e0c9-5a7f-bcf6-ce29570d7ef2', '06721010', 'Quenumá', '06', 'Salliqueló'),
    ('86be7760-e1f8-539d-bc0a-fa91642c9e87', '06721020', 'Salliqueló', '06', 'Salliqueló'),
    ('85c29ce5-292c-557f-a39b-96a9a0bf26f8', '06728010', 'Azcuénaga', '06', 'San Andrés de Giles'),
    ('071a2963-06cf-5b88-a06d-4ca88164a242', '06728020', 'Cucullú', '06', 'San Andrés de Giles'),
    ('6c38dc66-bac6-5f79-867c-9080a0171228', '06728030', 'Franklin', '06', 'San Andrés de Giles'),
    ('b4c679e1-8265-5edd-ba19-666ceff8c7ad', '06728040', 'San Andrés de Giles', '06', 'San Andrés de Giles'),
    ('98ce7877-73c5-5db0-a118-20b0c029c0cc', '06728050', 'Solís', '06', 'San Andrés de Giles'),
    ('49ea6d04-a224-5556-8303-d9c1b7417ba7', '06728060', 'Villa Espil', '06', 'San Andrés de Giles')
ON CONFLICT (indec_id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('6e938cfc-006d-5e17-a1d9-5313c503a5a3', '06728070', 'Villa Ruiz', '06', 'San Andrés de Giles'),
    ('fd8763f6-6cc6-5613-8d85-003205ce1b22', '06735010', 'Duggan', '06', 'San Antonio de Areco'),
    ('dc198a51-b91b-5b97-9d3d-838060ff146d', '06735020', 'San Antonio de Areco', '06', 'San Antonio de Areco'),
    ('05b3d33d-87b1-5bfc-825c-9017f3a5ef89', '06735030', 'Villa Lía', '06', 'San Antonio de Areco'),
    ('ec891024-7b78-59e7-8b06-086e6403a74a', '06742010', 'Balneario San Cayetano', '06', 'San Cayetano'),
    ('d431dddf-6e1d-5bac-8c23-fc8a65af91bf', '06742020', 'Ochandío', '06', 'San Cayetano'),
    ('6c98d119-d638-5522-aba6-73172fb307c1', '06742030', 'San Cayetano', '06', 'San Cayetano'),
    ('22d79016-f201-5670-b9d6-5e216758ee68', '06749010', 'San Fernando', '06', 'San Fernando'),
    ('fe2470dd-a0d2-5948-b467-65988f290398', '0674901001', 'San Fernando', '06', 'San Fernando'),
    ('e527336b-cc3b-5035-9173-ef6c5936e20a', '0674901002', 'Victoria', '06', 'San Fernando'),
    ('ba456b6c-6e11-5583-8499-0b63aeb03687', '0674901003', 'Virreyes', '06', 'San Fernando'),
    ('247e688e-441b-5cbd-96d1-c97d4b2707e6', '06756010', 'San Isidro', '06', 'San Isidro'),
    ('4df063f1-86a9-565b-ab7d-725ebc4901f5', '0675601001', 'Acasusso', '06', 'San Isidro'),
    ('da40ceec-0443-5896-afee-0aa4cb63caad', '0675601002', 'Béccar', '06', 'San Isidro'),
    ('cc186ae7-c940-5494-963d-de45a4e9941a', '0675601003', 'Boulogne Sur Mer', '06', 'San Isidro'),
    ('9f227ba2-513b-59ae-a2cc-704d7f4c1c7d', '0675601004', 'Martínez', '06', 'San Isidro'),
    ('2096c3eb-f828-5f3f-b399-fe35cac9d1cd', '0675601005', 'San Isidro', '06', 'San Isidro'),
    ('3dd3255a-6082-5b4b-8999-c115a9a2fd98', '0675601006', 'Villa Adelina', '06', 'San Isidro'),
    ('53d8f4a1-524c-5fc0-882e-f2c04def0a7b', '06760010', 'San Miguel', '06', 'San Miguel'),
    ('97f9f271-495e-5d48-87af-1203b65822ec', '0676001001', 'Bella Vista', '06', 'San Miguel'),
    ('41677cee-5763-591f-b959-9914c3ad07d2', '0676001002', 'Campo de Mayo', '06', 'San Miguel'),
    ('545b65ce-4c45-5fa6-bff7-29ed4baafd76', '0676001003', 'Muñiz', '06', 'San Miguel'),
    ('f9df32dd-d0a4-53dc-9bd1-58a10c1de57a', '0676001004', 'San Miguel', '06', 'San Miguel'),
    ('e1dab5a5-c956-5486-b4a0-c231ad92c63c', '06763010', 'Conesa', '06', 'San Nicolás'),
    ('85571b00-bbe4-5f7c-9638-17c193498b94', '06763020', 'Erezcano', '06', 'San Nicolás'),
    ('b64363e7-8377-55e5-b79d-804c0ac2ed1f', '06763030', 'General Rojo', '06', 'San Nicolás'),
    ('d6e38aac-09dc-5acc-927e-cc1bbfa34c3d', '06763040', 'La Emilia', '06', 'San Nicolás'),
    ('eb6c78f3-10ee-57d7-a89d-eb878c743ed7', '0676304001', 'La Emilia', '06', 'San Nicolás'),
    ('41ebd29e-1f1c-5272-9f96-cbd0563016b8', '0676304002', 'Villa Campi', '06', 'San Nicolás'),
    ('332ec957-08e6-5cb4-a482-12c7137f1288', '0676304003', 'Villa Canto', '06', 'San Nicolás'),
    ('09627c29-5636-5c6b-b4d0-8f4a2dea6244', '0676304004', 'Villa Riccio', '06', 'San Nicolás'),
    ('d23b93c1-cea5-50a8-9afc-bc534cc6ff55', '06763050', 'San Nicolás de los Arroyos', '06', 'San Nicolás'),
    ('a90b09c5-ffe4-5a42-9a59-c994225e7a23', '0676305001', 'Campos Salles', '06', 'San Nicolás'),
    ('0a06b858-ca9e-5d29-af0b-df535a0dd8ca', '0676305002', 'San Nicolás de los Arroyos', '06', 'San Nicolás'),
    ('e6aa58fe-7c94-5943-928a-a150ebb87fe1', '06763060', 'Villa Esperanza', '06', 'San Nicolás'),
    ('830ce28c-9da0-5299-afa6-a7cbe6dd742f', '06770010', 'Gobernador Castro', '06', 'San Pedro'),
    ('d49b3f57-d950-5955-80f6-8eea4b2b0609', '06770020', 'Obligado', '06', 'San Pedro'),
    ('ac80b26e-7322-50a6-ac15-7c8389601878', '06770030', 'Pueblo Doyle', '06', 'San Pedro'),
    ('d7a292c3-aab3-5f28-b9a6-73702a8b70d1', '06770040', 'Río Tala', '06', 'San Pedro'),
    ('fe9eca1a-cbb6-5a4b-abb6-0a596b46d974', '06770050', 'San Pedro', '06', 'San Pedro'),
    ('f4c8caed-222a-5f20-9bb3-d83038da6db1', '06770060', 'Santa Lucía', '06', 'San Pedro'),
    ('ac2d0325-fd48-5786-b9e6-3d5b5c49aff5', '06778020', 'San Vicente', '06', 'San Vicente'),
    ('ce4cae7e-003a-5731-b81b-3ba927cd9d8e', '0677802001', 'Alejandro Korn', '06', 'San Vicente'),
    ('b5f39f18-a3b4-5d9f-9828-2d6585895bf5', '0677802002', 'San Vicente', '06', 'San Vicente'),
    ('2b5769df-ab51-55dd-a49f-d347a62f8d52', '0677802003', 'Domselaar', '06', 'San Vicente'),
    ('fd48ae76-f2c2-520d-ab7a-a2feeedc1137', '06784010', 'General Rivas', '06', 'Suipacha'),
    ('cf922c8b-93fc-52d7-ac12-e9854efbbc41', '06784020', 'Suipacha', '06', 'Suipacha'),
    ('1e3bdd5c-a5f2-5ed1-8711-175e9e082d75', '06791010', 'De la Canal', '06', 'Tandil'),
    ('efb6d3b6-7eea-55c0-9663-dcc9dd4a963a', '06791030', 'Gardey', '06', 'Tandil'),
    ('81260f8c-0b9e-5134-9617-57594224cc27', '06791040', 'María Ignacia', '06', 'Tandil'),
    ('eb5432a9-d9ef-5d49-b412-49c9b106ac8c', '06791050', 'Tandil', '06', 'Tandil'),
    ('18c5202e-3982-5ceb-b3f6-3a1027eafa1b', '06798010', 'Crotto', '06', 'Tapalqué'),
    ('36e31dd7-49d7-555b-94d2-0230b0bd7f5b', '06798020', 'Tapalqué', '06', 'Tapalqué'),
    ('e7242200-f0b1-551e-8aab-cac776e22190', '06798030', 'Velloso', '06', 'Tapalqué'),
    ('d8ce6087-d3b3-588e-8b88-f8d8326c34f2', '06805010', 'Tigre', '06', 'Tigre'),
    ('2b219ca1-3ae6-5300-9239-b06b2fd347bf', '0680501001', 'Benavídez', '06', 'Tigre'),
    ('a437d7f9-6590-554b-b5ac-0b607422987e', '0680501002', 'Dique Luján', '06', 'Tigre'),
    ('d3eceb81-7a3a-58c8-8457-3fa0be1e9e1a', '0680501003', 'Don Torcuato Este', '06', 'Tigre'),
    ('2406ca25-27aa-5c82-9378-9bd85d3b4737', '0680501004', 'Don Torcuato Oeste', '06', 'Tigre'),
    ('1afac54e-cfb1-56cb-bd8a-0eb07d1c5c01', '0680501005', 'El Talar', '06', 'Tigre'),
    ('2e9a17b7-2db3-5b31-97a3-9967b1ed6b4f', '0680501006', 'General Pacheco', '06', 'Tigre'),
    ('b48e5ed7-1174-5879-bf65-d0dd95817b88', '0680501007', 'Troncos del Talar', '06', 'Tigre'),
    ('05c65c01-a165-58bf-827a-1ec824640a2a', '0680501008', 'Ricardo Rojas', '06', 'Tigre'),
    ('d6499190-a29d-5c40-a8b1-f6ad460176fb', '0680501009', 'Rincón de Milberg', '06', 'Tigre'),
    ('5b9bf0ca-9189-54de-b838-e90cd51dc6cd', '0680501010', 'Tigre', '06', 'Tigre'),
    ('2a4adb7c-febd-5444-b072-13881fa43f53', '06812010', 'General Conesa', '06', 'Tordillo'),
    ('75733a22-6857-5d30-aa71-dd646c0875aa', '06819010', 'Chasicó', '06', 'Tornquist'),
    ('14c12776-c706-519b-ba33-144d7f40cbc8', '06819040', 'Tornquist', '06', 'Tornquist'),
    ('717e0ff5-5db5-523a-b1fc-84d2ab9fa502', '06819050', 'Tres Picos', '06', 'Tornquist'),
    ('7f846e27-b57b-5f07-ba2c-cda6f7312c14', '06819060', 'La Gruta', '06', 'Tornquist'),
    ('1f2e0ad7-08f2-5b75-86f9-0ad9a6215be8', '06819070', 'Villa Ventana', '06', 'Tornquist'),
    ('a33b3d44-5d35-5e54-966d-bef30e28167e', '06826010', 'Berutti', '06', 'Trenque Lauquen'),
    ('60590c85-a9fb-51e2-ab91-69f651df58af', '06826020', 'Girodias', '06', 'Trenque Lauquen'),
    ('f166738a-e162-568f-98c4-9052ee85fa0d', '06826030', 'La Carreta', '06', 'Trenque Lauquen'),
    ('d66c3193-fdb1-5509-970b-6522f44c678b', '06826040', '30 de Agosto', '06', 'Trenque Lauquen'),
    ('5b8b3deb-93e3-57d8-aa5d-02892962b3fd', '06826050', 'Trenque Lauquen', '06', 'Trenque Lauquen'),
    ('8aaec3c4-b758-585e-ba21-57100e145255', '06826060', 'Trongé', '06', 'Trenque Lauquen'),
    ('b2197a22-4ac2-50ef-95ce-25cea5bf4855', '06833010', 'Balneario Orense', '06', 'Tres Arroyos'),
    ('96c76552-6457-5032-9196-f174a5a6eb66', '06833020', 'Claromecó', '06', 'Tres Arroyos'),
    ('75a3eb65-c1cd-573d-ae99-ebedcdc67d9c', '0683302001', 'Claromecó', '06', 'Tres Arroyos'),
    ('2b6a15e6-3628-5e59-86ea-1fc3a18e6c41', '0683302002', 'Dunamar', '06', 'Tres Arroyos'),
    ('2adc2b97-696b-55b6-b878-586b3fa612b6', '06833030', 'Copetonas', '06', 'Tres Arroyos'),
    ('82592f59-e2dd-57c5-ad8f-dd7cdd670672', '06833040', 'Lin Calel', '06', 'Tres Arroyos'),
    ('50ae85cc-6818-5985-9c3b-fe2c82da37f2', '06833050', 'Micaela Cascallares', '06', 'Tres Arroyos'),
    ('e2c08970-4907-566b-a6f9-d7bc4db51fdd', '06833060', 'Orense', '06', 'Tres Arroyos'),
    ('a1a379cb-7ea9-5cbd-b98a-0350b517b842', '06833070', 'Reta', '06', 'Tres Arroyos'),
    ('3c175c0f-0d18-5bf6-bab1-696a9711fb6e', '06833080', 'San Francisco de Bellocq', '06', 'Tres Arroyos'),
    ('8f4d903b-b142-56f5-af47-0fc202ae73f6', '06833090', 'San Mayol', '06', 'Tres Arroyos'),
    ('31d0306d-85cd-5077-a1f5-69a1298485cb', '06833100', 'Tres Arroyos', '06', 'Tres Arroyos'),
    ('6a8ebbaf-f8e1-5d34-9907-aca4147e7b80', '06833110', 'Villa Rodríguez', '06', 'Tres Arroyos'),
    ('ec7b91d3-06b6-589b-a73f-4549e5e06077', '06840010', 'Tres de Febrero', '06', 'Tres de Febrero'),
    ('f217476b-cd5f-5307-99a3-efd51a4e482c', '0684001001', 'Caseros', '06', 'Tres de Febrero'),
    ('522aa373-c650-587d-9413-e28172a1f5a2', '0684001002', 'Churruca', '06', 'Tres de Febrero'),
    ('a0fe865e-e1e5-54b9-a712-0719ba424195', '0684001003', 'Ciudad Jardín Lomas del Palomar', '06', 'Tres de Febrero'),
    ('1a81df93-316b-5423-81f1-41aaceafdf65', '0684001004', 'Ciudadela', '06', 'Tres de Febrero'),
    ('96e04eb0-c361-5bd1-be53-bc3427d91a4d', '0684001005', 'El Libertador', '06', 'Tres de Febrero'),
    ('264913cf-0a3b-5611-85f3-56751c3a4304', '0684001006', 'José Ingenieros', '06', 'Tres de Febrero'),
    ('233ef95c-0ddb-527d-85d2-b0d318fc86f3', '0684001007', 'Loma Hermosa', '06', 'Tres de Febrero'),
    ('30ec7de6-0e6a-5edb-8fdd-2b257c905b72', '0684001008', 'Martín Coronado', '06', 'Tres de Febrero'),
    ('ed41feed-2bc6-5f89-9151-acf3076f94e9', '0684001009', '11 de Septiembre', '06', 'Tres de Febrero'),
    ('1a09a924-9bf1-5bff-9b5d-0c5fc01898cd', '0684001010', 'Pablo Podestá', '06', 'Tres de Febrero'),
    ('989c8372-1463-5c87-bb56-59bef1c0beaf', '0684001011', 'Remedios de Escalada', '06', 'Tres de Febrero'),
    ('f1ca7d15-00c8-5660-a1dc-b9edfcda8ed4', '0684001012', 'Sáenz Peña', '06', 'Tres de Febrero'),
    ('73bec21a-b53d-5947-8fc6-06085498029e', '0684001013', 'Santos Lugares', '06', 'Tres de Febrero'),
    ('d3ac0bcd-e265-564a-b270-e7a3f7c2dd52', '0684001014', 'Villa Bosch', '06', 'Tres de Febrero'),
    ('509524d6-4d72-5b2f-a6fe-f5dfbe5020e0', '0684001015', 'Villa Raffo', '06', 'Tres de Febrero'),
    ('25159b79-632f-5c36-be4d-6674134044a6', '06847010', 'Ingeniero Thompson', '06', 'Tres Lomas'),
    ('124e6295-aee8-5974-8524-f1ee8f518b7a', '06847020', 'Tres Lomas', '06', 'Tres Lomas'),
    ('662314f2-24b4-52bc-b480-a269ab5139da', '06854010', 'Agustín Mosconi', '06', '25 de Mayo'),
    ('5a53bc15-6a76-514f-8764-cdfa2334fcb0', '06854020', 'Del Valle', '06', '25 de Mayo'),
    ('9d09eab6-f44c-5ff7-99b8-06ba88796e5b', '06854030', 'Ernestina', '06', '25 de Mayo'),
    ('68d9762a-14be-5824-98e7-a26e483681f6', '06854040', 'Gobernador Ugarte', '06', '25 de Mayo'),
    ('61160a0f-1cd0-51e2-b529-a119fab907aa', '06854050', 'Lucas Monteverde', '06', '25 de Mayo'),
    ('01ef57a5-2c73-5a61-a1e9-32d8d468a8dc', '06854060', 'Norberto de la Riestra', '06', '25 de Mayo'),
    ('f12fdb45-5145-59f9-8ed5-acf31400da3f', '06854070', 'Pedernales', '06', '25 de Mayo'),
    ('e080df1a-5d51-59bf-bccb-ce969aaa0fa7', '06854080', 'San Enrique', '06', '25 de Mayo'),
    ('3671e97b-9775-5f75-b250-3af4b7f03a00', '06854090', 'Valdés', '06', '25 de Mayo'),
    ('a2915495-d441-5505-ae4b-835f644f58ef', '06854100', '25 de Mayo', '06', '25 de Mayo'),
    ('1b1d7c97-205e-54e7-8cb8-ad0e23af588b', '06861010', 'Vicente López', '06', 'Vicente López'),
    ('318e1f41-284b-54bd-b4ce-562677c84499', '0686101001', 'Carapachay', '06', 'Vicente López'),
    ('69e6ad61-375d-5db6-a00b-d2338e4de838', '0686101002', 'Florida', '06', 'Vicente López'),
    ('fe8ab510-f25c-5fc2-815f-a3bb6f3c7677', '0686101003', 'Florida Oeste', '06', 'Vicente López'),
    ('ef4274b4-3619-5962-8371-1d3079585280', '0686101004', 'La Lucila', '06', 'Vicente López'),
    ('8c478fe4-e5dd-5137-9d92-cca751f0f10a', '0686101005', 'Munro', '06', 'Vicente López'),
    ('4a3ee7d2-b2d3-57d9-8151-2fe7acdd8cef', '0686101006', 'Olivos', '06', 'Vicente López'),
    ('3a15faae-7e5e-5fb5-9a46-861677e3dd37', '0686101007', 'Vicente López', '06', 'Vicente López'),
    ('2840a929-66f5-55c8-a5a2-42b3b78de3e7', '0686101008', 'Villa Adelina', '06', 'Vicente López'),
    ('977238b8-c14a-5f54-9044-0198b4dda209', '0686101009', 'Villa Martelli', '06', 'Vicente López'),
    ('f1d855af-8883-523f-ab0b-13c24de830b1', '06868010', 'Mar Azul', '06', 'Villa Gesell'),
    ('62fd7d44-987d-5bea-9bdc-fc3e96809365', '0686801001', 'Mar Azul', '06', 'Villa Gesell'),
    ('751f044d-c4cd-5fd1-af8a-f61866aeed2c', '0686801002', 'Mar de las Pampas', '06', 'Villa Gesell'),
    ('0aee2ac1-25d7-5901-9b6f-23b568921312', '06868020', 'Villa Gesell', '06', 'Villa Gesell'),
    ('10949701-b209-5c4d-995d-180e9df83152', '06875010', 'Argerich', '06', 'Villarino'),
    ('d2c5fcfb-3943-58d0-ba2b-0ab714810437', '06875030', 'Hilario Ascasubi', '06', 'Villarino'),
    ('a9a93869-7ada-53c5-ad2b-e328366492c1', '06875040', 'Juan Cousté', '06', 'Villarino'),
    ('7936254b-87ca-5cef-bb7a-82e58ee29889', '06875050', 'Mayor Buratovich', '06', 'Villarino'),
    ('5b186eb8-b7b2-550f-b768-211bd675509a', '06875060', 'Médanos', '06', 'Villarino'),
    ('94b4168d-aa01-59b9-8611-d6a83d168b94', '06875070', 'Pedro Luro', '06', 'Villarino'),
    ('5cf2dbdf-2c24-544c-9841-132ab8b61325', '06875080', 'Teniente Origone', '06', 'Villarino'),
    ('2f4eeb58-f296-5e40-b7f1-2b0c58bab032', '06882030', 'Escalada', '06', 'Zárate'),
    ('f3d06ed8-dc41-57f2-a8a4-452fe8073758', '06882040', 'Lima', '06', 'Zárate'),
    ('a8b712e8-fe4f-54c1-80cb-fa1e72273a22', '06882050', 'Zárate', '06', 'Zárate'),
    ('66054ca5-c2db-5dae-bf1c-514e8f05fbef', '0688205001', 'Barrio Saavedra', '06', 'Zárate'),
    ('f1fc9eb9-c3b0-5931-8cdf-f9e768ee3dba', '0688205002', 'Zárate', '06', 'Zárate'),
    ('4cccb5a8-6ab4-5244-a419-b45f21ff4423', '10007010', 'Chuchucaruana', '10', 'Ambato'),
    ('b6323c9d-f97d-519d-b19d-3c446df4298f', '10007020', 'Colpes', '10', 'Ambato'),
    ('204dcd09-c901-55f6-9918-045fd8787e5d', '10007030', 'El Bolsón', '10', 'Ambato'),
    ('751c985f-aa4e-534b-9f95-fd0487a730d1', '10007040', 'El Rodeo', '10', 'Ambato'),
    ('2dcec40f-f13f-5e3d-af54-fa01891b21d0', '10007050', 'Huaycama', '10', 'Ambato'),
    ('11cfc36c-6742-5abe-9634-7dc1b2e16f04', '10007060', 'La Puerta', '10', 'Ambato'),
    ('e50c6c46-7f82-581e-b413-5030b2208b0d', '10007070', 'Las Chacritas', '10', 'Ambato'),
    ('5778255e-c9ca-58ff-8f91-fc05a01d5b33', '10007080', 'Las Juntas', '10', 'Ambato'),
    ('91b95d40-ff6f-5cf6-9e33-1eb9607e2388', '10007090', 'Los Castillos', '10', 'Ambato'),
    ('f6c55511-6300-513b-9fc0-c6a153b194be', '10007100', 'Los Talas', '10', 'Ambato'),
    ('fd9f6ace-25d2-5036-a845-2167ff9d8bbc', '10007110', 'Los Varela', '10', 'Ambato'),
    ('fec2b9d8-90de-57d2-a58c-5ee3b387340f', '10007120', 'Singuil', '10', 'Ambato'),
    ('7b85efa4-e8c5-550d-ba47-226abd52e88e', '10014010', 'Ancasti', '10', 'Ancasti'),
    ('7bf2b39c-4370-522c-913b-88d4f8023eb6', '10014020', 'Anquincila', '10', 'Ancasti'),
    ('529c6af7-c0ec-5a24-84be-30a0d5b1afcc', '10014030', 'La Candelaria', '10', 'Ancasti'),
    ('81773fba-09c8-507f-8fb3-bb03ca6474ba', '10014040', 'La Majada', '10', 'Ancasti'),
    ('abe203e0-7f82-5d75-a89c-b27912ea3483', '10021010', 'Amanao', '10', 'Andalgalá'),
    ('e36552f7-0a4a-58f5-b935-5c0cba877914', '10021020', 'Andalgalá', '10', 'Andalgalá'),
    ('8b168ca9-027b-5557-bfea-601d18bc6990', '10021030', 'Chaquiago', '10', 'Andalgalá'),
    ('189bd074-d972-5517-b0ba-3c443b560ac2', '10021040', 'Choya', '10', 'Andalgalá'),
    ('ce7b2740-4f72-5cf8-a9c3-195b6d3f6ae6', '10021050', 'El Alamito', '10', 'Andalgalá'),
    ('bc814142-bac0-5d32-ad52-71f70a0582ab', '1002105001', 'Buena Vista', '10', 'Andalgalá'),
    ('7ed83f7f-e170-59af-a0b8-499485050cdc', '1002105002', 'El Alamito', '10', 'Andalgalá'),
    ('4b8d0a13-3b3e-5480-adf8-2a815f5b063d', '10021060', 'El Lindero', '10', 'Andalgalá'),
    ('602a457f-13ef-579d-b1f7-0d57bc43136c', '1002106001', 'Aconquija', '10', 'Andalgalá'),
    ('ce9f13f9-bbf0-5438-9f90-dfa69c279344', '1002106002', 'Alto de las Juntas', '10', 'Andalgalá'),
    ('36c49f31-2e33-5226-b67f-2a68c0bb2c23', '1002106003', 'El Lindero', '10', 'Andalgalá'),
    ('907b9367-32fb-57e1-a90f-dee5afeee476', '1002106004', 'La Mesada', '10', 'Andalgalá'),
    ('3d6ab2d6-e3b3-5f01-a54b-8b1f8b040093', '10021070', 'El Potrero', '10', 'Andalgalá'),
    ('0c91c321-3da0-52bd-8c36-2ef7cb38f18e', '10028010', 'Antofagasta de la Sierra', '10', 'Antofagasta de la Sierra'),
    ('95c4711b-9a6c-5e2e-8c06-4978c57bbb19', '10028030', 'El Peñón', '10', 'Antofagasta de la Sierra'),
    ('8ad31715-13ed-5568-8dfa-fbba1e5e2991', '10028040', 'Los Nacimientos', '10', 'Antofagasta de la Sierra'),
    ('59293753-cbbb-58fa-ab8a-c40cddf03271', '10035010', 'Barranca Larga', '10', 'Belén'),
    ('680dddfd-d6db-53ce-a5e1-2777015ddca7', '10035020', 'Belén', '10', 'Belén'),
    ('308f773c-0b00-5c04-83e4-2dbf88e9d4f6', '10035030', 'Cóndor Huasi', '10', 'Belén'),
    ('cf08cab2-7d2a-5271-b75b-9b93e6df8b69', '10035040', 'Corral Quemado', '10', 'Belén'),
    ('3504b4d2-c3d3-58be-9869-184ce309bce7', '10035050', 'El Durazno', '10', 'Belén'),
    ('a5da39e3-0978-5c38-870f-5b5e33fc57f8', '10035060', 'Farallón Negro', '10', 'Belén'),
    ('f5c9a621-f551-55f4-909a-eab83364d8a4', '10035070', 'Hualfín', '10', 'Belén'),
    ('64931763-8ca3-58a6-b6eb-8b08d88caf90', '10035080', 'Jacipunco', '10', 'Belén'),
    ('5fe61768-3016-58d1-bf3e-1fd92ed792c5', '10035090', 'La Puntilla', '10', 'Belén'),
    ('ebae3fd4-eba2-5ac2-ada0-0e24d0eb734c', '10035100', 'Las Juntas', '10', 'Belén'),
    ('c43fee74-e2bb-52b7-8931-2b6ef3b4db43', '10035110', 'Londres', '10', 'Belén'),
    ('134c8cca-f78a-595c-99a4-488610861149', '10035120', 'Los Nacimientos', '10', 'Belén'),
    ('6e169185-de4c-5cce-9df3-8675c8f688c1', '10035130', 'Puerta de Corral Quemado', '10', 'Belén'),
    ('439dd70b-f2c2-5f3d-b7e0-7d50cf7167ac', '10035140', 'Puerta de San José', '10', 'Belén'),
    ('a77b3016-4c86-5f77-9c97-68a5fff51972', '10035150', 'Villa Vil', '10', 'Belén'),
    ('80bc208d-d463-58e1-a26a-82bc5acf2711', '10042030', 'Capayán', '10', 'Capayán'),
    ('f339e16b-32ad-5c8a-83fb-bf122d262873', '10042040', 'Chumbicha', '10', 'Capayán'),
    ('cc28b58d-0460-525f-95b9-8946a0329d62', '10042050', 'Colonia del Valle', '10', 'Capayán'),
    ('074cbc72-457e-5ed1-8beb-d955c4447df0', '10042060', 'Colonia Nueva Coneta', '10', 'Capayán'),
    ('afc5b13c-5120-5113-b34a-3193edaa1510', '10042070', 'Concepción', '10', 'Capayán'),
    ('044e1c93-a3fd-592c-83a6-1b07f22d7b0c', '10042080', 'Coneta', '10', 'Capayán'),
    ('38c48617-b8ee-5f02-a4f1-3890107710ad', '10042090', 'El Bañado', '10', 'Capayán'),
    ('d9b1e017-b833-5ba7-a646-82c09f2f9a50', '10042100', 'Huillapima', '10', 'Capayán'),
    ('b87aeab7-7279-5e07-b6e9-13ae09bcfe22', '10042110', 'Los Angeles', '10', 'Capayán'),
    ('785941d6-0303-5d7d-9e3f-9e7687c141bb', '1004211001', 'Los Ángeles Norte', '10', 'Capayán'),
    ('64eee407-23d2-506a-8426-0b510b0062ed', '1004211002', 'Los Ángeles Sur', '10', 'Capayán'),
    ('4ee23f9e-d6a3-58cf-865a-ce84b6d50cc7', '10042120', 'Miraflores', '10', 'Capayán'),
    ('0bdc3f9a-f10f-5506-bd42-81da23cf2098', '10042130', 'San Martín', '10', 'Capayán'),
    ('8f021cbe-b3d6-58a3-ade3-e6a34d4aba6a', '10042140', 'San Pablo', '10', 'Capayán'),
    ('52219934-8e9a-5e91-aab2-d0cb55f6a193', '10042150', 'San Pedro', '10', 'Capayán'),
    ('b9ef0582-d204-524f-af9e-634a6899b5b5', '10049030', 'San Fernando del Valle de Catamarca', '10', 'Capital'),
    ('ade00e63-cae2-5d9c-b028-df6d6916fce7', '10056010', 'El Alto', '10', 'El Alto'),
    ('5247f30f-7dbb-5729-9dc1-6c866cf4b4a3', '10056020', 'Guayamba', '10', 'El Alto'),
    ('f767f0a7-67b1-557a-bb3e-074802871c4e', '10056030', 'Infanzón', '10', 'El Alto'),
    ('ee464a2e-acb7-502b-8672-a3143b4396a9', '10056040', 'Los Corrales', '10', 'El Alto'),
    ('fd654d0b-ba2e-523d-95e8-8145f6542831', '10056050', 'Tapso', '10', 'El Alto'),
    ('9a09745f-3252-517b-b882-fc9136292cb4', '10056060', 'Vilismán', '10', 'El Alto'),
    ('84eceac7-6ab2-51d0-a6ff-076d0c13ed8f', '10063020', 'Pomancillo Este', '10', 'Fray Mamerto Esquiú'),
    ('1e3de9e2-1330-516b-bcc7-042afa337bd1', '10063030', 'Pomancillo Oeste', '10', 'Fray Mamerto Esquiú'),
    ('2d6a5bf4-f64d-5118-a434-fb5d19bec4cb', '10063040', 'San José', '10', 'Fray Mamerto Esquiú'),
    ('094d7739-b499-59b0-ac2a-9a3d0eb3a87b', '1006304001', 'El Hueco', '10', 'Fray Mamerto Esquiú'),
    ('d090bfbf-e264-571b-97a4-7ecc54fea70c', '1006304002', 'La Carrera', '10', 'Fray Mamerto Esquiú'),
    ('daae3c47-7379-5c98-a13c-baa1eb168db3', '1006304003', 'La Falda de San Antonio', '10', 'Fray Mamerto Esquiú'),
    ('3dcddaea-0257-5c0d-b14d-8dba26b0470f', '1006304004', 'La Tercena', '10', 'Fray Mamerto Esquiú'),
    ('f7559b7f-2763-5056-b587-8fd4541702fd', '1006304005', 'San Antonio', '10', 'Fray Mamerto Esquiú'),
    ('0670d625-4857-5d1d-9b68-d11c96033b5e', '1006304006', 'San José', '10', 'Fray Mamerto Esquiú'),
    ('536b8738-f3fe-517d-84f4-cdfba3599807', '10063050', 'Villa Las Pirquitas', '10', 'Fray Mamerto Esquiú'),
    ('3df622f1-f413-521e-bb28-47c1ea5c58c0', '10070010', 'Casa de Piedra', '10', 'La Paz'),
    ('1d3ca1b5-7ac1-5679-9042-b0139d49d025', '10070020', 'El Aybal', '10', 'La Paz'),
    ('2f42fd4e-e3f1-50d6-a3b1-504d22ac1846', '10070030', 'El Bañado', '10', 'La Paz'),
    ('f7fb51f9-e91b-528e-95fa-94d4e86523f3', '10070040', 'El Divisadero', '10', 'La Paz'),
    ('82a84db9-32c5-5051-8fa7-3caf5faf01c0', '10070050', 'El Quimilo', '10', 'La Paz'),
    ('8809a3fd-2514-583d-aed3-eaad0d2d5894', '10070060', 'Esquiú', '10', 'La Paz'),
    ('a317a005-f1a9-5df4-8f6a-d51be11e68f7', '10070070', 'Icaño', '10', 'La Paz'),
    ('48c6bcd9-f203-578a-89f1-9af467529b91', '10070080', 'La Dorada', '10', 'La Paz'),
    ('fd562797-38a7-5ad0-980b-162422a8ecbc', '10070090', 'La Guardia', '10', 'La Paz'),
    ('9b5f8e18-882b-5185-aeb7-badf9a8c64dd', '10070100', 'Las Esquinas', '10', 'La Paz'),
    ('f1a5585b-34e0-510f-9338-8b00f73ce6b2', '10070110', 'Las Palmitas', '10', 'La Paz'),
    ('27558ad2-d972-5dd4-8733-ddcd64c3c0c8', '10070120', 'Quirós', '10', 'La Paz'),
    ('918f1454-3b64-5cf1-a080-ce2fcd23a86f', '10070130', 'Ramblones', '10', 'La Paz'),
    ('0bd5962c-b70b-5104-a36c-ce8d0c486106', '10070140', 'Recreo', '10', 'La Paz'),
    ('a84d817d-78ed-5ba0-a338-ce57c47960b6', '10070150', 'San Antonio', '10', 'La Paz'),
    ('9dac6935-ba51-51d1-a073-faffaaf26fb1', '10077010', 'Amadores', '10', 'Paclín'),
    ('eb76ad9c-e0e1-51ce-8aa9-205ad1b25547', '10077040', 'La Higuera', '10', 'Paclín'),
    ('25fc7fbc-8fe4-5530-bed6-06e738b8ad3e', '10077050', 'La Merced', '10', 'Paclín'),
    ('9c82ce8b-7feb-584a-a270-ad2a260b39bb', '10077060', 'La Viña', '10', 'Paclín'),
    ('dd788fbe-ab20-5d07-9991-35bbf9f78dc7', '10077070', 'Las Lajas', '10', 'Paclín'),
    ('db0f788c-3081-5d13-870a-f6af1ffd8cc3', '10077080', 'Monte Potrero', '10', 'Paclín'),
    ('37fdb6c4-288a-53bf-a791-b8ae3f118c4c', '10077090', 'Palo Labrado', '10', 'Paclín'),
    ('5ebfccd3-9a33-5f38-8d3d-1b4cb467c8fe', '10077100', 'San Antonio', '10', 'Paclín'),
    ('a157de70-191b-5115-b1be-5fc702c06e43', '10077110', 'Villa de Balcozna', '10', 'Paclín'),
    ('a4458f70-39d3-57bf-9c2c-b7bff1166989', '10084020', 'Colana', '10', 'Pomán'),
    ('0d4b4c67-8fd4-56c3-b926-50d8c2de8359', '10084030', 'Colpes', '10', 'Pomán'),
    ('b0423037-7980-52eb-b1f8-dda4afbdf043', '10084040', 'El Pajonal', '10', 'Pomán'),
    ('e846c669-12b5-56cf-9199-eeed8174f033', '10084060', 'Mutquin', '10', 'Pomán'),
    ('13729893-a2c0-5a22-a93e-7110c50e9424', '10084070', 'Pomán', '10', 'Pomán'),
    ('b6d64a4d-7a8e-5559-a65e-27b3fe5da134', '10084080', 'Rincón', '10', 'Pomán'),
    ('577c21a1-3a36-5292-8587-23b243bdea21', '10084090', 'San Miguel', '10', 'Pomán'),
    ('abbdf9f6-1c2e-5b8a-8072-5ba9ddb39041', '10084100', 'Saujil', '10', 'Pomán'),
    ('712d0486-fafe-5b7c-8bcf-a817feb7640c', '10084110', 'Siján', '10', 'Pomán'),
    ('2ae20af0-040a-59a4-a7ad-3f97f6fb5f22', '10091010', 'Andalhualá', '10', 'Santa María'),
    ('5171e04d-1d88-583b-bc8d-7ce2cf7b11d8', '10091030', 'Chañar Punco', '10', 'Santa María'),
    ('110e89eb-03d2-5511-b4f7-e202973d8cb7', '1009103001', 'Chañar Punco', '10', 'Santa María'),
    ('c54bd25e-cf9b-5aa2-add1-38fbb1f2a44d', '1009103002', 'Lampacito', '10', 'Santa María'),
    ('f8a60b40-5cec-5f5e-8aed-01cbee30292f', '1009103003', 'Medanitos', '10', 'Santa María'),
    ('0d124c4c-7cad-5ac4-9698-c8b1e2732bef', '10091040', 'El Cajón', '10', 'Santa María'),
    ('3aa21903-8b95-59e8-a500-357e9514ddb6', '10091050', 'El Desmonte', '10', 'Santa María'),
    ('7a67ed4c-f4dc-56ee-961c-cd31eb1b74ec', '10091060', 'El Puesto', '10', 'Santa María'),
    ('897c32c2-453c-551b-aaac-fe0e78597678', '10091070', 'Famatanca', '10', 'Santa María'),
    ('ba90822a-0eba-5584-8624-1c9d8a18aded', '1009107001', 'Famatanca', '10', 'Santa María'),
    ('11ca01b8-1c83-574b-ae68-8b1ef41bdb22', '1009107002', 'San José Banda', '10', 'Santa María'),
    ('df3f8ec1-d4d0-523e-babd-cc341f993ddd', '10091080', 'Fuerte Quemado', '10', 'Santa María'),
    ('dde3a936-90b5-5375-bd1d-daf855a7bfd0', '10091090', 'La Hoyada', '10', 'Santa María'),
    ('356f9347-a6c3-59d7-aa84-1b1ddd05f662', '10091110', 'Las Mojarras', '10', 'Santa María'),
    ('0130f307-c87f-5c2d-9833-4fa6365aa0d3', '1009111001', 'El Cerrito', '10', 'Santa María'),
    ('9cdc8200-b169-5404-9583-08f150529c02', '1009111002', 'Las Mojarras', '10', 'Santa María'),
    ('2acc2ba7-78d2-5e95-96cc-425f34a7d943', '10091130', 'Punta de Balasto', '10', 'Santa María'),
    ('fe576aa1-a885-5968-a422-ca155a8f1b21', '10091140', 'San José', '10', 'Santa María'),
    ('7f3aaae9-1621-5517-9ecf-3b441b91636c', '1009114001', 'Casa de Piedra', '10', 'Santa María'),
    ('e750fe53-2f2a-5a76-a442-9da64d000d21', '1009114002', 'La Puntilla', '10', 'Santa María'),
    ('be5a1c4d-0a7b-5550-82b3-16c7826c89be', '1009114003', 'Palo Seco', '10', 'Santa María'),
    ('05e05cee-201c-52f2-a1f8-bfdd7639ded3', '1009114004', 'San José Norte', '10', 'Santa María'),
    ('983a1385-391e-53bd-bce2-1392c47acea9', '1009114005', 'San José Villa', '10', 'Santa María'),
    ('56ea85de-6fb5-545f-823b-034481bb2ea8', '10091150', 'Santa María', '10', 'Santa María'),
    ('45ac3849-2d80-575b-b02f-dfbe01f09c59', '10091160', 'Yapes', '10', 'Santa María'),
    ('b521f07b-5e4a-5222-b85d-9ab2b3ad41fd', '10098010', 'Alijilán', '10', 'Santa Rosa'),
    ('d0d97584-e2d6-513f-aff4-2cb3ee3e7a9e', '10098020', 'Bañado de Ovanta', '10', 'Santa Rosa'),
    ('637b0833-9fde-5a82-9643-343c2448ed27', '10098030', 'Las Cañas', '10', 'Santa Rosa'),
    ('1da06a31-399b-5779-ae49-7334947ddd1a', '10098040', 'Lavalle', '10', 'Santa Rosa'),
    ('50062535-68d0-5eeb-8375-d62a2fcb41ff', '10098050', 'Los Altos', '10', 'Santa Rosa'),
    ('3a6b5ac7-6449-5bf0-8e7b-4404cce45171', '10098060', 'Manantiales', '10', 'Santa Rosa'),
    ('0f053883-4d71-5503-b5fb-56df40d64d92', '10098070', 'San Pedro', '10', 'Santa Rosa'),
    ('ec0c999b-95dd-5e90-bc59-15c3e6b8d2f0', '10105010', 'Anillaco', '10', 'Tinogasta'),
    ('e27595a6-2851-5291-8aca-82e209df190c', '10105050', 'Copacabana', '10', 'Tinogasta'),
    ('a9b24985-592f-5d38-b4f0-abcb7f70ceb3', '1010505001', 'Copacabana', '10', 'Tinogasta'),
    ('6bd4923e-7fab-5644-ab41-e3a36db6958c', '1010505002', 'La Puntilla', '10', 'Tinogasta'),
    ('6791c52a-146d-5c9d-b35c-b449192f6c31', '10105090', 'El Puesto', '10', 'Tinogasta'),
    ('4467b6ed-c3f3-560f-91c4-dcd5ed623f75', '10105100', 'El Salado', '10', 'Tinogasta'),
    ('4bff4e16-7082-5d38-8e72-9dfa1b3983f0', '10105110', 'Fiambalá', '10', 'Tinogasta'),
    ('3f9f8b9b-7cf0-5224-ac23-0c9de4a46f70', '1010511001', 'Fiambalá', '10', 'Tinogasta'),
    ('abfd49f7-75ef-557b-a7fd-c415dba4a1aa', '1010511002', 'La Ramadita', '10', 'Tinogasta'),
    ('862010c7-9314-5a0f-b995-64357a65d8b0', '1010511003', 'Pampa Blanca', '10', 'Tinogasta'),
    ('50de069f-84b0-5a7c-bf34-8acede5bcf43', '10105130', 'Medanitos', '10', 'Tinogasta'),
    ('e726aa3a-9917-59fb-934f-5dcd8892cbb3', '10105140', 'Palo Blanco', '10', 'Tinogasta'),
    ('ea47f847-7e2f-5cb4-a56b-9a1bc4ab3f2c', '10105160', 'Saujil', '10', 'Tinogasta'),
    ('d172969b-356f-586d-b32f-8d87504c9bbd', '10105180', 'Tinogasta', '10', 'Tinogasta'),
    ('4d15cf77-0d49-5388-9568-0f7bed3117b4', '10112010', 'El Portezuelo', '10', 'Valle Viejo'),
    ('ae6a853b-294c-56c5-81fc-86ffae233a9c', '10112020', 'Huaycama', '10', 'Valle Viejo'),
    ('fba7c2e9-43c7-56c0-becc-612a27b998f5', '10112030', 'Las Tejas', '10', 'Valle Viejo'),
    ('144ea2c2-f594-50a4-aeb9-5384f911ea8e', '10112040', 'San Isidro', '10', 'Valle Viejo'),
    ('5a051892-1a2b-5356-9634-b0a7fd99e058', '1011204001', 'El Bañado', '10', 'Valle Viejo'),
    ('1d2e7915-1709-5696-a18d-601083ffd44c', '1011204002', 'Polcos', '10', 'Valle Viejo'),
    ('7f2444b9-be79-54e4-a661-f4f3916166e2', '1011204003', 'Pozo del Mistol', '10', 'Valle Viejo'),
    ('39ad0c7a-2496-57da-8ffe-bad9632001ca', '1011204004', 'San Isidro', '10', 'Valle Viejo'),
    ('b4710cc6-afe5-5956-b9b5-c9d35c01d4be', '1011204005', 'Santa Rosa', '10', 'Valle Viejo'),
    ('88d089c7-2723-54ca-afb9-ce2e09ee88b1', '1011204006', 'Sumalao', '10', 'Valle Viejo'),
    ('7fb16b6f-1d1a-52e5-8b5d-434888a77fca', '1011204007', 'Villa Dolores', '10', 'Valle Viejo'),
    ('511c2362-443a-5393-a2e0-d105cd3592b1', '10112050', 'Santa Cruz', '10', 'Valle Viejo'),
    ('9b1d2740-ecf4-5352-a6b3-22e7d5ea4a5a', '14007010', 'Amboy', '14', 'Calamuchita'),
    ('9068e0bb-3fcb-519b-8c0f-e5ecda6d2290', '14007020', 'Arroyo San Antonio', '14', 'Calamuchita'),
    ('4c533479-3199-52f4-9040-321f5656d524', '14007030', 'Cañada del Sauce', '14', 'Calamuchita'),
    ('b75e0449-587e-5c88-a040-a4312a255a31', '14007050', 'El Corcovado - El Torreón', '14', 'Calamuchita'),
    ('454bccdf-abe1-53c1-aada-0a52585d7a72', '14007055', 'El Durazno', '14', 'Calamuchita'),
    ('6f239ebb-5d9e-589c-bcd3-fa26d66e59b6', '14007060', 'Embalse', '14', 'Calamuchita'),
    ('6ac62dd8-5ac7-524b-afa8-06dec11da123', '14007070', 'La Cruz', '14', 'Calamuchita'),
    ('3322ec5c-9a0c-5459-bef0-7811f4c342d0', '14007080', 'La Cumbrecita', '14', 'Calamuchita'),
    ('8ce85274-6efb-5b79-a290-e075f8372840', '14007090', 'Las Bajadas', '14', 'Calamuchita'),
    ('c46e8b94-9a08-5fe5-a11b-c75ec2a5967f', '14007100', 'Las Caleras', '14', 'Calamuchita'),
    ('a50f07b3-fa80-5fd5-a400-82bc612a6514', '14007110', 'Los Cóndores', '14', 'Calamuchita'),
    ('869ef238-8b87-514e-a641-a8f396078d5a', '14007120', 'Los Molinos', '14', 'Calamuchita'),
    ('fd32c544-1834-5bf3-b47a-2e27acb1f7a1', '1400712001', 'Los Molinos', '14', 'Calamuchita'),
    ('d7a17db7-4374-5b83-95f8-277884b2eb99', '1400712002', 'Villa San Miguel', '14', 'Calamuchita'),
    ('dec03203-0447-51f6-be42-272c96e06fbf', '14007130', 'Los Reartes', '14', 'Calamuchita'),
    ('0bfd7fed-ecf9-5229-95af-06c0484157c2', '14007140', 'Lutti', '14', 'Calamuchita'),
    ('7be595be-3496-59d1-98e2-00397b68447c', '14007160', 'Parque Calmayo', '14', 'Calamuchita'),
    ('311fe4dd-4c5e-50dc-bec9-853409bec2ea', '14007170', 'Río de los Sauces', '14', 'Calamuchita'),
    ('6365ed23-880a-5c96-ab6c-b3daf21fb3ab', '14007180', 'San Agustín', '14', 'Calamuchita'),
    ('d634f64e-7615-5a12-91f0-6131f622877c', '14007190', 'San Ignacio (Loteo San Javier)', '14', 'Calamuchita'),
    ('b786fac2-6ea8-5b2c-905d-2c9b9bae015c', '14007210', 'Santa Rosa de Calamuchita', '14', 'Calamuchita'),
    ('7c01912d-2ae2-5731-b8f4-8ed546e78563', '1400721001', 'Santa Mónica', '14', 'Calamuchita'),
    ('e3774f42-b67e-5a3e-b4ad-96d19f358657', '1400721002', 'Santa Rosa de Calamuchita', '14', 'Calamuchita'),
    ('6bb35ce0-79c9-54e6-8c2d-baddaebcaa56', '1400721003', 'San Ignacio (Loteo Vélez Crespo)', '14', 'Calamuchita'),
    ('cdb841a0-8ca3-57b9-89ea-bcf752c243ad', '14007220', 'Segunda Usina', '14', 'Calamuchita'),
    ('b21114bb-0264-5dac-97f0-a8fb01e222bf', '14007230', 'Solar de los Molinos', '14', 'Calamuchita'),
    ('ec7eadbf-cc17-509c-98bc-4c35894a5e9a', '14007240', 'Villa Alpina', '14', 'Calamuchita'),
    ('eaadce42-2803-57d2-8706-6e9d8d986157', '14007250', 'Villa Amancay', '14', 'Calamuchita'),
    ('5c109d93-110c-5a38-8219-b1bf16cd2e4b', '14007260', 'Villa Berna', '14', 'Calamuchita'),
    ('be60b0f6-5ba8-5b76-82d1-9aa5f6cae93c', '14007270', 'Villa Ciudad Parque Los Reartes', '14', 'Calamuchita'),
    ('6454c2a1-341a-52f2-af5a-b37b4b6c9610', '1400727001', 'Villa Ciudad Parque Los Reartes', '14', 'Calamuchita'),
    ('345253f2-0a29-5010-a88d-c26d45af9bbe', '1400727002', 'Va.Ciudad Pque.Los Reartes (1° Sección)', '14', 'Calamuchita'),
    ('c88f241f-49b2-5db4-af99-845bb4f538e0', '1400727003', 'Va.Ciudad Pque.Los Reartes (3° Sección)', '14', 'Calamuchita'),
    ('700a8322-19f5-5073-af56-2a601abb39c9', '14007290', 'Villa del Dique', '14', 'Calamuchita'),
    ('b8cf79eb-7c72-5f36-8110-e37a5ac0d2e0', '14007300', 'Villa El Tala', '14', 'Calamuchita'),
    ('a1a50617-e369-5b63-8b25-e219e64bf0ff', '14007310', 'Villa General Belgrano', '14', 'Calamuchita'),
    ('bc8f2c39-15d6-56f1-b372-5b46aa49786e', '14007320', 'Villa La Rivera', '14', 'Calamuchita'),
    ('e1b6342e-f44f-58ac-a35b-c49ef7346a1f', '14007330', 'Villa Quillinzo', '14', 'Calamuchita'),
    ('1113817a-324d-57b0-b7f4-4ee84d657dfb', '14007340', 'Villa Rumipal', '14', 'Calamuchita'),
    ('7e80a759-a3f1-5bdb-9200-c3523ea89633', '14007360', 'Villa Yacanto', '14', 'Calamuchita'),
    ('6b1a4447-aeaa-5951-af26-9548afdd7ecc', '14014010', 'Córdoba', '14', 'Capital'),
    ('8d30a97d-1709-51d4-9628-9c3e31fc1b18', '1401401001', 'Jardín Arenales', '14', 'Capital'),
    ('516803e3-73db-5e7f-b732-6acf553b7d98', '1401401002', 'La Floresta', '14', 'Capital'),
    ('0bab5d9d-3ea9-5077-9900-02dfd34dafd5', '1401401003', 'Córdoba', '14', 'Capital'),
    ('38b383c8-c156-585b-b954-e0583f9ab6fb', '14021010', 'Agua de Oro', '14', 'Colón'),
    ('bbd8bd50-0adf-5f04-a874-5425e067f6b2', '14021020', 'Ascochinga', '14', 'Colón'),
    ('7e6fd9a9-5782-5fd0-a41b-d8a9c29bbbee', '14021050', 'Colonia Caroya', '14', 'Colón'),
    ('65be17c2-b1b0-5bd4-a8e2-82b2b03a1fb8', '14021060', 'Colonia Tirolesa', '14', 'Colón'),
    ('792a3179-404d-5ce5-8b95-e12a4bd961c1', '14021070', 'Colonia Vicente Agüero', '14', 'Colón'),
    ('198f888d-a7e6-55c9-bb9b-b5eeae4cf069', '14021075', 'Villa Corazón de María', '14', 'Colón'),
    ('ba6e5314-07c2-56a6-a967-86ff2e336b08', '14021110', 'El Manzano', '14', 'Colón'),
    ('0f4babd4-f125-5b49-89bb-f3601acab5d3', '14021130', 'General Paz', '14', 'Colón'),
    ('5108aab3-6315-54aa-9ad8-6e6a11371c6d', '14021140', 'Jesús María', '14', 'Colón'),
    ('3d2e0438-6435-5829-8be7-6cba5238c22b', '14021150', 'La Calera', '14', 'Colón'),
    ('4c749044-61c5-5839-991d-457b18ae4fb8', '1402115001', 'Dumesnil', '14', 'Colón'),
    ('a19cc6ac-64f2-5e8e-b65c-5920454f0f3e', '1402115002', 'La Calera', '14', 'Colón'),
    ('b2fddf52-2884-54f7-b0ce-a355af9ce341', '1402115003', 'El Diquecito', '14', 'Colón'),
    ('bb7facd7-b724-5365-a6cc-292abec63593', '14021160', 'La Granja', '14', 'Colón'),
    ('0ec7179f-03c7-5b82-9f8e-5f431c4dd663', '14021170', 'La Puerta', '14', 'Colón'),
    ('9cc44add-a1eb-50f4-bd3d-361cf63ba8e1', '14021190', 'Malvinas Argentinas', '14', 'Colón'),
    ('2d077ebd-2839-58e8-839a-23148fab8c3e', '14021200', 'Mendiolaza', '14', 'Colón'),
    ('f8795be1-5090-546f-9770-158eaba224be', '14021210', 'Mi Granja', '14', 'Colón'),
    ('b163f7ba-eac4-5cf4-9d69-20f16a63b82b', '14021230', 'Río Ceballos', '14', 'Colón'),
    ('4af689de-ac40-5b0e-8151-edf678906432', '14021240', 'Saldán', '14', 'Colón'),
    ('ec2fec62-56b0-5846-ab8c-7a0172a560d6', '14021250', 'Salsipuedes', '14', 'Colón'),
    ('06129a15-79ba-54f5-87fb-c896ef1bd312', '1402125001', 'El Pueblito', '14', 'Colón'),
    ('63f7f8b0-a1a3-5f57-b0bd-33f8ceb66942', '1402125002', 'Salsipuedes', '14', 'Colón'),
    ('670e65c2-c70a-5205-9faa-8a0cf1e5c61a', '14021270', 'Tinoco', '14', 'Colón'),
    ('1f0afcf1-6151-5ce5-bdd2-702f2e114f04', '14021280', 'Unquillo', '14', 'Colón'),
    ('0bdc7cec-57f5-5f91-90ea-bef54c5daa20', '14021290', 'Villa Allende', '14', 'Colón'),
    ('08b515e1-43db-5984-b656-8d6cb04cd937', '14021300', 'Villa Cerro Azul', '14', 'Colón'),
    ('a8c81ae0-1eb9-51f4-9ec6-d1b5f3ba4071', '14021310', 'Parque Norte - Ciudad de los Niños - Guiñazú Norte', '14', 'Colón'),
    ('83f4f407-fd43-557b-ba10-b917ca30989f', '1402131001', 'Guiñazú Norte', '14', 'Colón'),
    ('3f9018a4-4263-54b4-ac55-f18fe50ea07e', '1402131002', 'Parque Norte', '14', 'Colón'),
    ('86e71e7d-831c-5eab-856b-8ca8ffc41ebe', '1402131004', '1° de Agosto', '14', 'Colón'),
    ('ca85ea07-397b-5bc5-82ea-863019f8fabb', '1402131005', 'Allmirante Brown', '14', 'Colón'),
    ('54fb25d6-223c-58aa-bbfb-9b9c015306ca', '1402131006', 'Ciudad de los Niños', '14', 'Colón'),
    ('b3790154-45c5-51f2-93ab-140e4cf51a91', '1402131007', 'Villa Pastora', '14', 'Colón'),
    ('e23ebdb9-ccfa-5dcb-bc5e-6a2742cc3d15', '14021320', 'Villa Los Llanos - Juárez Celman', '14', 'Colón'),
    ('c4f211af-0987-5134-92cd-446883a76bdf', '1402132001', 'Juárez Celman', '14', 'Colón'),
    ('3ec946d6-1b3b-5d52-a8e3-3de98c6beb20', '1402132002', 'Villa Los Llanos', '14', 'Colón'),
    ('66a08097-1763-506f-bbf2-c6e3d567e1be', '14028010', 'Alto de los Quebrachos', '14', 'Cruz del Eje'),
    ('3a766c37-8426-5fd3-a43c-209e32524943', '14028020', 'Bañado de Soto', '14', 'Cruz del Eje'),
    ('8fbe25b8-9610-587f-9b9e-44da30dfa94c', '14028040', 'Cruz de Caña', '14', 'Cruz del Eje'),
    ('c84c62cc-334d-54be-9560-2938cbe29e6a', '14028050', 'Cruz del Eje', '14', 'Cruz del Eje'),
    ('4a5e8fbd-e17c-53b1-84ce-55e35ec96571', '14028060', 'El Brete', '14', 'Cruz del Eje')
ON CONFLICT (indec_id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('114a8e77-6a62-5b4f-bb37-58f688b248c6', '14028080', 'Guanaco Muerto', '14', 'Cruz del Eje'),
    ('d47aae86-4c4c-52f8-93bc-7f54ca0c7a9c', '14028100', 'La Batea', '14', 'Cruz del Eje'),
    ('c4007e40-a41e-512f-b131-fb2eb9653a66', '14028110', 'La Higuera', '14', 'Cruz del Eje'),
    ('59a290f7-6ad4-58ba-9376-88e9389b9697', '14028120', 'Las Cañadas', '14', 'Cruz del Eje'),
    ('fd786668-1b81-509f-9bc3-b634076482b1', '14028130', 'Las Playas', '14', 'Cruz del Eje'),
    ('f4eaee35-8f4e-545e-b6b9-b491361d5d07', '14028140', 'Los Chañaritos', '14', 'Cruz del Eje'),
    ('3d91d87b-2232-56de-9216-db5452e1a70f', '14028150', 'Media Naranja', '14', 'Cruz del Eje'),
    ('23da30ff-92de-583f-999e-964bc52282fe', '14028160', 'Paso Viejo', '14', 'Cruz del Eje'),
    ('1dafb584-7abc-57c7-9c47-5bbf6d53f3e9', '14028170', 'San Marcos Sierra', '14', 'Cruz del Eje'),
    ('b26d4089-7aed-5eb3-8975-2d4c68c96ce7', '14028180', 'Serrezuela', '14', 'Cruz del Eje'),
    ('b5108382-baee-5298-bba7-e52a95ae8768', '14028190', 'Tuclame', '14', 'Cruz del Eje'),
    ('23e93595-82f9-5b4f-961d-aa60bebe0a94', '14028200', 'Villa de Soto', '14', 'Cruz del Eje'),
    ('31a40fff-3e50-515b-8e1f-14c8ae4e7d99', '14035010', 'Del Campillo', '14', 'General Roca'),
    ('c9e47d3d-ceb5-54f3-99aa-9a3fa24046e1', '14035020', 'Estación Lecueder', '14', 'General Roca'),
    ('73104acb-e327-5214-ac85-69e8c24a98bc', '14035030', 'Hipólito Bouchard', '14', 'General Roca'),
    ('ece704e1-8d1f-58e6-a757-4c76ea0c3175', '14035040', 'Huinca Renancó', '14', 'General Roca'),
    ('9acae995-fef0-5486-8818-23738c3da8b0', '14035050', 'Italó', '14', 'General Roca'),
    ('82a539c7-275c-5ba8-b050-d35636fa2874', '14035060', 'Mattaldi', '14', 'General Roca'),
    ('17a71cfe-853a-5f40-a1cf-96ade341de96', '14035070', 'Nicolás Bruzzone', '14', 'General Roca'),
    ('e3cc35b0-613e-571a-8a53-a217a531bb0e', '14035080', 'Onagoity', '14', 'General Roca'),
    ('8423a26b-8ea1-5b5a-958a-27ef77a0a9ff', '14035090', 'Pincén', '14', 'General Roca'),
    ('5c40103c-a5f8-5739-9f30-d30b61d88a0f', '14035100', 'Ranqueles', '14', 'General Roca'),
    ('3aa10f2f-375b-51bb-8465-93492b6567e8', '14035110', 'Santa Magdalena', '14', 'General Roca'),
    ('a3589844-2317-5f31-aeb1-09e2daa18a78', '14035120', 'Villa Huidobro', '14', 'General Roca'),
    ('7242baea-bb19-5403-a843-5e8f0f2b5c16', '14035130', 'Villa Sarmiento', '14', 'General Roca'),
    ('98818c5a-1d5d-5035-a076-406b220ec2ea', '14035140', 'Villa Valeria', '14', 'General Roca'),
    ('37350319-2f46-53a5-93ce-adb6ffb4bbba', '14042010', 'Arroyo Algodón', '14', 'General San Martín'),
    ('0a693935-214a-55d1-9cec-cccc77156b43', '14042020', 'Arroyo Cabral', '14', 'General San Martín'),
    ('688236f9-8874-5083-ae3f-d0f11e756ecf', '14042030', 'Ausonia', '14', 'General San Martín'),
    ('95499030-6aa7-58e9-9c56-21f1a6b5d8e8', '14042040', 'Chazón', '14', 'General San Martín'),
    ('8f226fc7-db3f-5749-afee-12de523e5256', '14042050', 'Etruria', '14', 'General San Martín'),
    ('4b5582c6-38fd-5eb7-a7ce-2559a85529ad', '14042060', 'La Laguna', '14', 'General San Martín'),
    ('342b5cec-8c79-56f8-885c-6de8dba33bb3', '14042070', 'La Palestina', '14', 'General San Martín'),
    ('deb1f147-4d0d-5b6d-a8e1-4015977b6339', '14042080', 'La Playosa', '14', 'General San Martín'),
    ('9d77cb1a-5a35-52b1-a238-5291e2d5fde0', '14042090', 'Las Mojarras', '14', 'General San Martín'),
    ('325736af-fe1c-5b4d-996e-c39bd09c8e3c', '14042100', 'Luca', '14', 'General San Martín'),
    ('20b9a100-e3b6-52df-a6dd-e5cc82d249ba', '14042110', 'Pasco', '14', 'General San Martín'),
    ('43856f46-17dd-5dd8-8b99-03a8ba1cab5b', '14042120', 'Sanabria', '14', 'General San Martín'),
    ('ec0054f2-bd80-5b60-8bc2-ba63057ec0aa', '14042130', 'Silvio Pellico', '14', 'General San Martín'),
    ('912993d0-03d3-56b4-9ff3-f7313682c5b7', '14042140', 'Ticino', '14', 'General San Martín'),
    ('246788a0-6e49-53e6-a425-8e194216cb44', '14042150', 'Tío Pujio', '14', 'General San Martín'),
    ('91adbaf2-1fb8-5a54-8b6b-cff4117fa207', '14042170', 'Villa María', '14', 'General San Martín'),
    ('45f55454-bb41-58af-b712-31c52bca8540', '14042180', 'Villa Nueva', '14', 'General San Martín'),
    ('fea54a26-dac5-5877-aa0c-1369aabf6cc3', '14042190', 'Villa Oeste', '14', 'General San Martín'),
    ('4f7adbbb-1267-551a-b062-cedf72fc1ac4', '14049010', 'Avellaneda', '14', 'Ischilín'),
    ('5b837782-0489-5974-8765-54f78ec1229a', '14049020', 'Cañada de Río Pinto', '14', 'Ischilín'),
    ('a64a7f9f-5b38-5505-bd6b-4b602c78ed3e', '14049030', 'Chuña', '14', 'Ischilín'),
    ('9f8c52bc-4945-51b5-914d-b4cdf251cae2', '14049040', 'Copacabana', '14', 'Ischilín'),
    ('7ad563be-60a7-5e78-83d3-06a9f53bd4e8', '14049050', 'Deán Funes', '14', 'Ischilín'),
    ('b8fe5fd2-6623-5010-8ad2-746321133de4', '14049080', 'Los Pozos', '14', 'Ischilín'),
    ('e1294c1d-4e15-531c-b4bb-01bf55fa4fd6', '14049090', 'Olivares de San Nicolás', '14', 'Ischilín'),
    ('4cea77d4-10f6-51b0-8549-ff9cc38b8c50', '14049100', 'Quilino', '14', 'Ischilín'),
    ('e71f86fc-ebea-5064-b783-49aaa157a681', '14049110', 'San Pedro de Toyos', '14', 'Ischilín'),
    ('f2481539-5307-50ad-9501-a92b13ebea88', '14049120', 'Villa Gutiérrez', '14', 'Ischilín'),
    ('220d341e-30e2-50f3-96a8-ca66b20b0a7d', '14056010', 'Alejandro Roca', '14', 'Juárez Celman'),
    ('38b55b17-3d8c-5eb5-9b7d-dec9e50f3a5c', '14056020', 'Assunta', '14', 'Juárez Celman'),
    ('01970d78-8139-5b95-8781-a3f7165dc69a', '14056030', 'Bengolea', '14', 'Juárez Celman'),
    ('6302733a-ca94-5bab-b1bc-d20b3625149a', '14056040', 'Carnerillo', '14', 'Juárez Celman'),
    ('d6c25d8e-f66c-5936-b81c-809b000eed4d', '14056050', 'Charras', '14', 'Juárez Celman'),
    ('b4487272-58e5-5a23-a2bd-fb63335e07dd', '14056060', 'El Rastreador', '14', 'Juárez Celman'),
    ('1336d2cd-3bf5-590d-b57b-c32228663c82', '14056070', 'General Cabrera', '14', 'Juárez Celman'),
    ('3ee0c822-4761-5927-840f-f4ccf9f2d31c', '14056080', 'General Deheza', '14', 'Juárez Celman'),
    ('df90b82c-854d-5c97-b5cc-08e3e641f9ba', '14056090', 'Huanchillas', '14', 'Juárez Celman'),
    ('9971b78b-d875-5e9f-b80e-d7c3eea08ddc', '14056100', 'La Carlota', '14', 'Juárez Celman'),
    ('bab6ea9f-1d82-568a-960d-b9855157dde1', '14056110', 'Los Cisnes', '14', 'Juárez Celman'),
    ('1b5f7ce2-7f5b-5554-8b0a-19c0efe2be67', '14056120', 'Olaeta', '14', 'Juárez Celman'),
    ('d03ceebb-d314-5472-8c49-935e987a1b6b', '14056130', 'Pacheco de Melo', '14', 'Juárez Celman'),
    ('6f96b6d3-a6dd-553d-91ed-f577c3d02fdc', '14056140', 'Paso del Durazno', '14', 'Juárez Celman'),
    ('01b5c00d-1bd7-57e9-8720-02d3b55dd55b', '14056150', 'Santa Eufemia', '14', 'Juárez Celman'),
    ('a7f6af48-5ea5-5615-aca0-f7aa7c94b176', '14056160', 'Ucacha', '14', 'Juárez Celman'),
    ('1a116aee-596c-5ab1-8b96-2e60b1bab259', '14056170', 'Villa Reducción', '14', 'Juárez Celman'),
    ('fcbe967a-78ca-57f2-ac6d-25782a96d8bf', '14063010', 'Alejo Ledesma', '14', 'Marcos Juárez'),
    ('701eb253-47b5-55ec-bc77-135f405795c7', '14063020', 'Arias', '14', 'Marcos Juárez'),
    ('3b8152fe-7174-5d62-9479-0366ec175fe2', '14063030', 'Camilo Aldao', '14', 'Marcos Juárez'),
    ('723ff3da-0515-549e-bd3c-8ba83030d785', '14063040', 'Capitán General Bernardo O''Higgins', '14', 'Marcos Juárez'),
    ('33222348-9107-5140-ae4d-193b520a021e', '14063050', 'Cavanagh', '14', 'Marcos Juárez'),
    ('045fa88f-577a-5f9e-b09e-e5d2acfea57a', '14063060', 'Colonia Barge', '14', 'Marcos Juárez'),
    ('d53ebb10-6c2d-5258-8b2f-5428fc5642cb', '14063070', 'Colonia Italiana', '14', 'Marcos Juárez'),
    ('1b6d1771-3540-5f5b-8f8a-3208c22ebc30', '14063080', 'Colonia Veinticinco', '14', 'Marcos Juárez'),
    ('dcff8b8f-f537-5259-ab12-5ca971bd16c4', '14063090', 'Corral de Bustos', '14', 'Marcos Juárez'),
    ('f3d064e0-210c-513b-96b6-dbbc9285ff38', '14063100', 'Cruz Alta', '14', 'Marcos Juárez'),
    ('9c6180cd-6198-5e49-a17d-76f8184b9e63', '14063110', 'General Baldissera', '14', 'Marcos Juárez'),
    ('66d8e047-9d4f-5e6e-b1ba-e24554b15387', '14063120', 'General Roca', '14', 'Marcos Juárez'),
    ('e042c32f-bc23-54a6-8e9c-4d1197f5b52c', '14063130', 'Guatimozín', '14', 'Marcos Juárez'),
    ('a0331c20-4aee-5f14-9498-4d3855f4c108', '14063140', 'Inriville', '14', 'Marcos Juárez'),
    ('feca34de-4b7a-58ce-886d-333986672648', '14063150', 'Isla Verde', '14', 'Marcos Juárez'),
    ('f314dc79-6407-51af-ada7-fa1337b1e02c', '14063160', 'Leones', '14', 'Marcos Juárez'),
    ('bbe6bbfd-a6b7-5224-aba2-87568176e562', '14063170', 'Los Surgentes', '14', 'Marcos Juárez'),
    ('736c6aef-4d74-59bc-b8b4-ffaee0004618', '14063180', 'Marcos Juárez', '14', 'Marcos Juárez'),
    ('c6f6050b-54bf-5414-bf75-701ffbb72198', '14063190', 'Monte Buey', '14', 'Marcos Juárez'),
    ('acc3ba0e-c5cb-50af-8fa8-4dd4225c8c1c', '14063210', 'Saira', '14', 'Marcos Juárez'),
    ('c3e3365f-08f6-59ae-95a0-2d0145ffb739', '14063220', 'Saladillo', '14', 'Marcos Juárez'),
    ('09baa0fb-c693-59fc-8b10-c64fa27fb146', '14063230', 'Villa Elisa', '14', 'Marcos Juárez'),
    ('dc12dc36-ae53-502f-a598-a6a8f79d764a', '14070010', 'Ciénaga del Coro', '14', 'Minas'),
    ('1238e6b1-10dc-58da-9772-4d74476d4eff', '14070020', 'El Chacho', '14', 'Minas'),
    ('35831ab5-b212-5fd8-9b10-5c466ca4e6ba', '14070030', 'Estancia de Guadalupe', '14', 'Minas'),
    ('38fc0925-420a-58c5-b29c-a931064953e8', '14070040', 'Guasapampa', '14', 'Minas'),
    ('4d1266ec-08cd-5035-8230-2628e91f5b87', '14070050', 'La Playa', '14', 'Minas'),
    ('375ed080-2517-56f7-aadc-d5c6d67d7ff2', '14070060', 'San Carlos Minas', '14', 'Minas'),
    ('5b8b7587-93c8-5108-91b3-8a5476755180', '14070070', 'Talaini', '14', 'Minas'),
    ('fd9d4889-79ec-58b7-8d2f-32b79fd13764', '14070080', 'Tosno', '14', 'Minas'),
    ('5591d36b-a791-5948-adb8-2c03bb9d3b9f', '14077010', 'Chancani', '14', 'Pocho'),
    ('c53f312a-3598-59f8-9411-e8a898600e2f', '14077020', 'Las Palmas', '14', 'Pocho'),
    ('0ed6cda3-33a8-51b4-96e7-7d6dd670b187', '14077030', 'Los Talares', '14', 'Pocho'),
    ('2437e675-d217-5d41-bea1-6122b9c3b559', '14077040', 'Salsacate', '14', 'Pocho'),
    ('fd72b61d-9f46-5ece-8e29-eb9abe64b195', '14077050', 'San Gerónimo', '14', 'Pocho'),
    ('22c9ef15-cd76-562b-b382-71bbd0c0b67f', '14077060', 'Tala Cañada', '14', 'Pocho'),
    ('3b5cb40d-e68f-51d3-a393-6084a949acad', '14077080', 'Villa de Pocho', '14', 'Pocho'),
    ('f901e01c-b511-5ca1-b7d8-48d46480e843', '14084010', 'General Levalle', '14', 'Presidente Roque Sáenz Peña'),
    ('9ca4fe1a-4492-5016-8fe6-ca4f328eb499', '14084020', 'La Cesira', '14', 'Presidente Roque Sáenz Peña'),
    ('b8e74a0f-ed23-5c7e-bb78-03229593f3ca', '14084030', 'Laboulaye', '14', 'Presidente Roque Sáenz Peña'),
    ('9fe3bd85-ce40-5d4a-be9a-322569a33ac9', '14084040', 'Leguizamón', '14', 'Presidente Roque Sáenz Peña'),
    ('90e62582-f0d4-5bda-a59e-02417315f011', '14084050', 'Melo', '14', 'Presidente Roque Sáenz Peña'),
    ('091cdca6-18be-5df5-850e-e8b48580c3d3', '14084060', 'Río Bamba', '14', 'Presidente Roque Sáenz Peña'),
    ('5abe039b-ef91-5c83-a582-d6840dbe05ca', '14084070', 'Rosales', '14', 'Presidente Roque Sáenz Peña'),
    ('8559be04-fc25-5a02-8c26-f63e77f53e5e', '14084080', 'San Joaquín', '14', 'Presidente Roque Sáenz Peña'),
    ('dee8d5d7-8b09-5929-b214-8c9fe7352a27', '14084090', 'Serrano', '14', 'Presidente Roque Sáenz Peña'),
    ('6dcfc8c1-23c8-543f-9957-7378a21800dc', '14084100', 'Villa Rossi', '14', 'Presidente Roque Sáenz Peña'),
    ('900dd501-77f0-5061-a583-4794af155868', '14091020', 'Bialet Massé', '14', 'Punilla'),
    ('ebcfff55-1bb4-54e5-9e27-c10b47699034', '1409102001', 'Bialet Massé', '14', 'Punilla'),
    ('692f4aa3-9659-5a01-8423-32781ed08d1e', '1409102002', 'San Roque del Lago', '14', 'Punilla'),
    ('2a620844-c799-5a47-9340-00f54979c4c4', '14091030', 'Cabalango', '14', 'Punilla'),
    ('8f82e786-c876-54af-a74b-ccb49e94de7a', '14091040', 'Capilla del Monte', '14', 'Punilla'),
    ('ee33ca12-37ff-58b3-ab5e-01038f0fa110', '14091050', 'Casa Grande', '14', 'Punilla'),
    ('2b2a1db3-47f3-5959-80ea-bd0c14f8da0e', '14091060', 'Charbonier', '14', 'Punilla'),
    ('431e205d-b399-507f-b8fb-b8a0d33c5fa6', '14091070', 'Cosquín', '14', 'Punilla'),
    ('42dd156f-0524-5e35-a437-463f7f604ec0', '14091080', 'Cuesta Blanca', '14', 'Punilla'),
    ('058f6452-af5d-537c-a45b-02e984862cf6', '14091090', 'Estancia Vieja', '14', 'Punilla'),
    ('184fed6f-d79c-55b4-a8ad-ac9f9c6a6d0c', '14091100', 'Huerta Grande', '14', 'Punilla'),
    ('7c5e16df-8fa8-57f9-991b-74cf53d86106', '14091110', 'La Cumbre', '14', 'Punilla'),
    ('5751c348-d315-599c-ab1a-4f4afc7b3ecb', '14091120', 'La Falda', '14', 'Punilla'),
    ('deae3f1e-4afd-57da-91a0-f12534a2e21f', '14091130', 'Las Jarillas', '14', 'Punilla'),
    ('fe332b6c-336f-5c9a-b42d-6a00a32a1842', '14091140', 'Los Cocos', '14', 'Punilla'),
    ('6a1b2639-70a7-5626-9727-36e29a8ba994', '14091150', 'Mallín', '14', 'Punilla'),
    ('9031d368-ac18-50a6-ac2c-c946eaa2f356', '14091160', 'Mayu Sumaj', '14', 'Punilla'),
    ('2c2826ad-00fe-5198-8c35-12bc42a1e490', '14091180', 'San Antonio de Arredondo', '14', 'Punilla'),
    ('a5cc75b8-3a88-56ef-ba9c-bd1ba52ec3bf', '14091190', 'San Esteban', '14', 'Punilla'),
    ('f814cf2b-3043-5ddc-b119-57d1c2fac89d', '14091200', 'San Roque', '14', 'Punilla'),
    ('911515fc-eae6-5ec0-846e-901b55d15e47', '14091210', 'Santa María de Punilla', '14', 'Punilla'),
    ('6cf11ede-1e2f-5f8f-bc81-8162ddbf528c', '14091220', 'Tala Huasi', '14', 'Punilla'),
    ('78077139-bd34-5ee9-bc02-b625821ec17c', '14091230', 'Tanti', '14', 'Punilla'),
    ('bf2f6e2f-ff82-53ed-ae4c-622f4cab03e4', '14091240', 'Valle Hermoso', '14', 'Punilla'),
    ('794b6535-c04c-5dbb-82ec-24a06209f199', '14091250', 'Villa Carlos Paz', '14', 'Punilla'),
    ('c2a0ad43-e5b9-5191-b5e2-8d317277fee7', '14091260', 'Villa Flor Serrana', '14', 'Punilla'),
    ('5e4c44f5-b535-5b38-b72c-d4a2be4ad666', '14091270', 'Villa Giardino', '14', 'Punilla'),
    ('63178a9f-6fc5-5eaf-8151-8b86c6ec6020', '14091280', 'Villa Lago Azul', '14', 'Punilla'),
    ('d7016e92-fdd0-544e-96af-8a9255ce87fa', '14091290', 'Villa Parque Siquimán', '14', 'Punilla'),
    ('71899fd1-2948-5a41-86db-5d4b649f2645', '14091300', 'Villa Río Icho Cruz', '14', 'Punilla'),
    ('afdb6780-abbc-5728-904f-0dd5f781ccae', '14091320', 'Villa Santa Cruz del Lago', '14', 'Punilla'),
    ('e6350e80-3be1-57c8-939f-dcd2819088a3', '14098010', 'Achiras', '14', 'Río Cuarto'),
    ('f679a0f8-54c3-5ee8-946e-2da1154710a2', '14098020', 'Adelia María', '14', 'Río Cuarto'),
    ('1849692c-379f-5724-a0a1-c593918ffb7b', '14098030', 'Alcira Gigena', '14', 'Río Cuarto'),
    ('ecad0f6a-a5e6-57c5-903d-aa237ce7dd3c', '14098040', 'Alpa Corral', '14', 'Río Cuarto'),
    ('80420810-60a0-525e-b9e1-a37336dde340', '14098050', 'Berrotarán', '14', 'Río Cuarto'),
    ('8d067eaf-b5b4-522a-8886-10f9e93b0e93', '14098060', 'Bulnes', '14', 'Río Cuarto'),
    ('98723d84-8fcf-5368-82ca-15ee7d30d4d3', '14098070', 'Chaján', '14', 'Río Cuarto'),
    ('9f9f1c64-4315-5d19-b95f-da46e40a667e', '14098080', 'Chucul', '14', 'Río Cuarto'),
    ('32fec23d-0161-5970-80c0-3892e9ab0155', '14098090', 'Coronel Baigorria', '14', 'Río Cuarto'),
    ('109f0a34-9e1b-50d9-9021-f8402685a853', '14098100', 'Coronel Moldes', '14', 'Río Cuarto'),
    ('61c4c67a-5ee3-5838-97e4-69f743241228', '14098110', 'Elena', '14', 'Río Cuarto'),
    ('2010ed4a-d410-51b4-a41d-508d0d5ed3b5', '14098120', 'La Carolina', '14', 'Río Cuarto'),
    ('7c131fde-46dd-582f-b5d1-666303808fbc', '14098130', 'La Cautiva', '14', 'Río Cuarto'),
    ('6363cd3a-5c15-50d6-a013-0d0cbfaa3189', '14098140', 'La Gilda', '14', 'Río Cuarto'),
    ('1f5e233f-14dd-5116-ae54-a29084080c47', '14098150', 'Las Acequias', '14', 'Río Cuarto'),
    ('9af0ffcc-6a3a-5d8a-8c78-80580e1367b3', '14098160', 'Las Albahacas', '14', 'Río Cuarto'),
    ('d558fd19-e731-5ad2-9cd8-ee99a0c8fe9a', '14098170', 'Las Higueras', '14', 'Río Cuarto'),
    ('161653d9-bb9a-5228-8edb-ea233e6edec4', '14098180', 'Las Peñas', '14', 'Río Cuarto'),
    ('30701889-d767-55ba-8d72-9c337daa89e6', '14098190', 'Las Vertientes', '14', 'Río Cuarto'),
    ('e42e5fd5-5ecf-5713-a348-30cacaaea7c1', '14098200', 'Malena', '14', 'Río Cuarto'),
    ('a246bd67-dd6f-52bc-a8b7-353ac5fc32e8', '14098210', 'Monte de los Gauchos', '14', 'Río Cuarto'),
    ('30d50a2d-684d-5dc2-af7a-033e7c5eb231', '14098230', 'Río Cuarto', '14', 'Río Cuarto'),
    ('71f0a161-ba59-5a22-8570-1686b645c8be', '14098240', 'Sampacho', '14', 'Río Cuarto'),
    ('c675cc51-5146-5d6a-803e-5cfbbc6f8b51', '14098250', 'San Basilio', '14', 'Río Cuarto'),
    ('4995b06c-597d-50a0-bc48-3542cfb2af47', '14098260', 'Santa Catalina Holmberg', '14', 'Río Cuarto'),
    ('df4bea68-57d7-525c-9d83-2b634419537a', '14098270', 'Suco', '14', 'Río Cuarto'),
    ('d8e3995e-8fb5-5154-ad8e-59b1b5d4d3f8', '14098280', 'Tosquitas', '14', 'Río Cuarto'),
    ('0ab88115-4b69-52ec-9132-c403c53b420f', '14098290', 'Vicuña Mackenna', '14', 'Río Cuarto'),
    ('82fbfe32-816d-583b-85ca-37ac1c3b3375', '14098300', 'Villa El Chacay', '14', 'Río Cuarto'),
    ('ec411121-98d2-5a13-ba0d-a4000b8ac50d', '14098320', 'Washington', '14', 'Río Cuarto'),
    ('f44d76da-53f2-563c-b1cd-2019ac346cc5', '14105010', 'Atahona', '14', 'Río Primero'),
    ('017db07e-457d-5d65-9e28-f743473b9f23', '14105020', 'Cañada de Machado', '14', 'Río Primero'),
    ('2005b535-41fe-58d0-a57e-162e4fc186a5', '14105030', 'Capilla de los Remedios', '14', 'Río Primero'),
    ('4f011e9f-b700-55dc-a492-42eda14f727a', '14105040', 'Chalacea', '14', 'Río Primero'),
    ('0276b04d-9760-5e9a-a31e-6c802cb9d8ce', '14105050', 'Colonia Las Cuatro Esquinas', '14', 'Río Primero'),
    ('e830b439-2309-5e40-b411-95bd45bd47a7', '14105060', 'Diego de Rojas', '14', 'Río Primero'),
    ('8d4df5fa-a73a-5340-8c5a-570affac78dc', '14105070', 'El Alcalde', '14', 'Río Primero'),
    ('d9621c74-8a62-50fb-a49a-f9fb5bf90c1d', '14105080', 'El Crispín', '14', 'Río Primero'),
    ('47203fd6-7db8-5872-99e9-f63b03adb5b8', '14105090', 'Esquina', '14', 'Río Primero'),
    ('08628526-e347-5928-9971-aec9b4f2e07f', '14105100', 'Kilómetro 658', '14', 'Río Primero'),
    ('48345576-c356-50d6-8932-244520f636f8', '14105110', 'La Para', '14', 'Río Primero'),
    ('a985181d-ed6f-5c1b-9622-ac2f258c9123', '14105120', 'La Posta', '14', 'Río Primero'),
    ('b3f822cd-d37e-59bf-9272-2eaeae2dd415', '14105130', 'La Puerta', '14', 'Río Primero'),
    ('7f6458c2-58a5-5633-90c6-f4b05afd591c', '14105140', 'La Quinta', '14', 'Río Primero'),
    ('71250325-7443-598a-81ba-1dc5ccb6a26d', '14105150', 'Las Gramillas', '14', 'Río Primero'),
    ('22401263-0eba-5f2d-aa1f-eab2172cd9f6', '14105160', 'Las Saladas', '14', 'Río Primero'),
    ('4403bd74-36bb-5e84-9123-fccd36844ef7', '14105170', 'Maquinista Gallini', '14', 'Río Primero'),
    ('e42f27bf-b6d9-53ad-92c8-1627a542132a', '14105180', 'Monte del Rosario', '14', 'Río Primero'),
    ('23667f72-ff71-5b28-9e44-77ede588dcf6', '14105190', 'Montecristo', '14', 'Río Primero'),
    ('789a0055-adf2-5157-91f7-62df14850858', '14105200', 'Obispo Trejo', '14', 'Río Primero'),
    ('ac6cbbec-af45-5074-88a7-31e2449e7e19', '14105210', 'Piquillín', '14', 'Río Primero'),
    ('ab4f422d-d543-5a80-826b-c6cf76b54576', '14105220', 'Plaza de Mercedes', '14', 'Río Primero'),
    ('7702fadc-1203-562e-9478-7c57c8bf3845', '14105230', 'Pueblo Comechingones', '14', 'Río Primero'),
    ('e14976d7-09bf-5ba8-8a02-d9d985f38509', '14105240', 'Río Primero', '14', 'Río Primero'),
    ('288342ec-e7bb-5cf4-8910-e639644ef581', '14105250', 'Sagrada Familia', '14', 'Río Primero'),
    ('84d23bda-5a1b-5085-9a20-8b3d3bcf3fa9', '14105260', 'Santa Rosa de Río Primero', '14', 'Río Primero'),
    ('0c4b4090-9975-5d18-b183-902c24330672', '14105270', 'Villa Fontana', '14', 'Río Primero'),
    ('e69737b8-9c5b-565c-8825-91fc2297998e', '14112010', 'Cerro Colorado', '14', 'Río Seco'),
    ('fd7aef80-6cd4-5794-b939-47d098d07f6a', '14112020', 'Chañar Viejo', '14', 'Río Seco'),
    ('0c0d168a-bf2a-5141-9f5f-6318bb41db53', '14112030', 'Eufrasio Loza', '14', 'Río Seco'),
    ('14f85d2f-81c3-508c-b568-068cdcedcbc8', '14112040', 'Gutemberg', '14', 'Río Seco'),
    ('3445358f-9c2b-54c3-b4dd-d87d30aa0d6c', '14112050', 'La Rinconada', '14', 'Río Seco'),
    ('861e8f0f-ad7a-5647-9165-0272f1d4656a', '14112060', 'Los Hoyos', '14', 'Río Seco'),
    ('8fa511ad-efc5-5cfd-8af8-dda3c7ecca99', '14112070', 'Puesto de Castro', '14', 'Río Seco'),
    ('f8632721-ffa0-5afd-8a93-62dd897b4cc0', '14112080', 'Rayo Cortado', '14', 'Río Seco'),
    ('a82b3ade-2904-5882-9503-6bd6db11aaef', '14112090', 'San Pedro de Gütemberg', '14', 'Río Seco'),
    ('c695b31e-dbba-5558-82a0-8f5d0435b712', '14112100', 'Santa Elena', '14', 'Río Seco'),
    ('82121dc0-19ea-5346-83ad-9b79215c3e25', '14112110', 'Sebastián Elcano', '14', 'Río Seco'),
    ('04272a23-dd21-5621-9bea-2ed1e8370a7a', '14112120', 'Villa Candelaria', '14', 'Río Seco'),
    ('e1974dd0-f9b3-582e-9d1b-911eb6cdaccc', '14112130', 'Villa de María', '14', 'Río Seco'),
    ('b4f56aad-59bf-5d59-b53e-7822ee965397', '14119010', 'Calchín', '14', 'Río Segundo'),
    ('c666553d-266b-58ef-850f-d1febfadc5b4', '14119020', 'Calchín Oeste', '14', 'Río Segundo'),
    ('25f0d0d2-d250-50f1-b277-ffa2cc60d21b', '14119030', 'Capilla del Carmen', '14', 'Río Segundo'),
    ('bbd22a05-c96a-5b75-8d68-455cb457f39b', '14119040', 'Carrilobo', '14', 'Río Segundo'),
    ('512797d9-64a7-5a3a-b315-ea160d0df56e', '14119050', 'Colazo', '14', 'Río Segundo'),
    ('279ce2ce-56c2-50c7-afe0-f5ad72ebb298', '14119060', 'Colonia Videla', '14', 'Río Segundo'),
    ('920fc429-eee0-5bab-b4d3-ca663b4392ad', '14119070', 'Costasacate', '14', 'Río Segundo'),
    ('cae03128-2b45-5055-8dc5-7003fd34a47c', '14119080', 'Impira', '14', 'Río Segundo'),
    ('92534b6b-89f0-5b3e-b5e6-81543383ecf4', '14119090', 'Laguna Larga', '14', 'Río Segundo'),
    ('824d6cbf-8a96-5d19-9440-039ae3c53bc4', '14119100', 'Las Junturas', '14', 'Río Segundo'),
    ('f7fdc24d-8c46-514f-906e-66339d2ce88f', '14119110', 'Los Chañaritos', '14', 'Río Segundo'),
    ('1ddeedc7-2616-5b50-ae3c-86bd1497edbb', '14119120', 'Luque', '14', 'Río Segundo'),
    ('3b94a202-6fe8-5454-9a08-9f89b3554643', '14119130', 'Manfredi', '14', 'Río Segundo'),
    ('9f35da71-dae7-5c37-ab0b-9902aacd70df', '14119140', 'Matorrales', '14', 'Río Segundo'),
    ('152f99d3-8485-56c7-bedd-023998b6c33e', '14119150', 'Oncativo', '14', 'Río Segundo'),
    ('376a80cc-4b30-5e96-9c70-2875da6d05a9', '14119160', 'Pilar', '14', 'Río Segundo'),
    ('93a381b4-15fb-5bcc-97b1-5343c1f8e052', '14119170', 'Pozo del Molle', '14', 'Río Segundo'),
    ('b1654290-8bee-56df-9a83-69cf5a6b53a5', '14119180', 'Rincón', '14', 'Río Segundo'),
    ('3bbdaea5-8ff8-5353-844f-ddc9ba8b3e06', '14119190', 'Río Segundo', '14', 'Río Segundo'),
    ('59daca34-19b2-5f58-906c-94823ccd57de', '14119200', 'Santiago Temple', '14', 'Río Segundo'),
    ('23c6f0b1-9381-588c-b569-73a8736fcd98', '14119210', 'Villa del Rosario', '14', 'Río Segundo'),
    ('6cbe0e53-c07f-593a-b8bc-628364207501', '14126010', 'Ambul', '14', 'San Alberto'),
    ('e51e6d81-8f1b-53a4-924f-10a15b1da9ea', '14126020', 'Arroyo Los Patos', '14', 'San Alberto'),
    ('22ef397a-aa42-5932-896c-ae9a83462dfc', '14126050', 'Las Calles', '14', 'San Alberto'),
    ('8e7e106c-1194-5a3c-9e19-74bd17a8300d', '14126070', 'Las Rabonas', '14', 'San Alberto'),
    ('b3e7a370-c484-5247-b57e-8db53333b3a4', '14126090', 'Mina Clavero', '14', 'San Alberto'),
    ('aa9a075a-eb9f-5c93-95e8-a34c14f8dc3f', '14126100', 'Mussi', '14', 'San Alberto'),
    ('00ba1e9a-7908-5f4d-8a34-b9a539eedfb1', '14126110', 'Nono', '14', 'San Alberto'),
    ('0dec4478-fb09-55fd-a7f5-4136eea81534', '14126120', 'Panaholma', '14', 'San Alberto'),
    ('d731b7b6-d1e6-541f-9378-b7f21c948cd3', '14126140', 'San Lorenzo', '14', 'San Alberto'),
    ('5c9515f8-6f44-5952-9be3-a36986473ed7', '14126150', 'San Martín', '14', 'San Alberto'),
    ('36749156-6c88-54b8-af35-064a40436d6c', '14126160', 'San Pedro', '14', 'San Alberto'),
    ('fc3e8cbb-3029-5e70-8574-7eb8aba45792', '14126170', 'San Vicente', '14', 'San Alberto'),
    ('7560dd23-2087-586b-a471-de59f7135303', '14126180', 'Sauce Arriba', '14', 'San Alberto'),
    ('e9898cf8-11b9-5974-a8c7-a9fcf0e89fe9', '14126200', 'Villa Cura Brochero', '14', 'San Alberto'),
    ('5abb3397-5379-5f52-950c-42040d2fac83', '14126210', 'Villa Sarmiento', '14', 'San Alberto'),
    ('e78f584d-9bed-50ad-a5d9-499f53e568d2', '14133010', 'Conlara', '14', 'San Javier'),
    ('229aab40-a959-5803-bd8b-ee89913c3052', '14133060', 'La Paz', '14', 'San Javier'),
    ('83e5f637-d77a-595e-a4d7-53a7b90f4bb2', '14133070', 'La Población', '14', 'San Javier'),
    ('f55b33d9-8204-560e-9167-9ef81c8349c4', '14133090', 'La Travesía', '14', 'San Javier'),
    ('2ef17fea-65f5-5fe1-83ab-69fe9dcab76e', '14133100', 'Las Tapias', '14', 'San Javier'),
    ('8d7b7ecc-cdf3-5344-9a2f-39b73b366e39', '14133110', 'Los Cerrillos', '14', 'San Javier'),
    ('1d68c66f-63e5-5d31-9075-5dc85bc32f45', '14133120', 'Los Hornillos', '14', 'San Javier'),
    ('d757fa4a-9658-58f8-9ba7-2e1f0320c5eb', '14133150', 'Luyaba', '14', 'San Javier'),
    ('0034381b-20b7-57dd-97bf-c80f63ba95bd', '14133170', 'San Javier y Yacanto', '14', 'San Javier'),
    ('2d859423-8376-5cd1-8e28-2842f5b4ad08', '14133180', 'San José', '14', 'San Javier'),
    ('86d66927-282c-507d-b40a-79c6b04501ab', '14133190', 'Villa de las Rosas', '14', 'San Javier'),
    ('a9dee1e5-6e2d-58d5-bbc7-01310fa12149', '1413319001', 'Alto Resbaloso - El Barrial', '14', 'San Javier'),
    ('90c1301e-c188-5953-987e-2f5f2871ebd6', '1413319002', 'El Pueblito', '14', 'San Javier'),
    ('03cd05e2-9e30-5d65-a40b-0f622700fabd', '1413319003', 'El Valle', '14', 'San Javier'),
    ('799caaae-8aa9-5bac-bc9e-a781f7c77409', '1413319004', 'Las Chacras', '14', 'San Javier'),
    ('57425edb-c8b0-5f68-a328-1d20f8256b9d', '1413319005', 'Villa de las Rosas', '14', 'San Javier'),
    ('0c8646ca-8b8b-5cd0-9795-2e984d1b7708', '14133200', 'Villa Dolores', '14', 'San Javier'),
    ('7d91b773-b6c2-5dff-9b4f-4952a23f395c', '14133210', 'Villa La Viña', '14', 'San Javier'),
    ('73a21d40-7d70-5d1b-8ba0-7550e82746cc', '14140010', 'Alicia', '14', 'San Justo'),
    ('b0cf14ef-0908-5473-9673-135655a3de04', '14140020', 'Altos de Chipión', '14', 'San Justo'),
    ('a578f69f-8d56-5ff5-9ac4-eb5274a5f6a8', '14140030', 'Arroyito', '14', 'San Justo'),
    ('e366ab4e-9c83-5a63-8d3b-5d62130177d0', '14140040', 'Balnearia', '14', 'San Justo'),
    ('5c841d15-0166-5903-b530-c9a2cc08341a', '14140050', 'Brinkmann', '14', 'San Justo'),
    ('fd8344a6-140c-5d23-8e4e-2c92fff3bf5e', '14140060', 'Colonia Anita', '14', 'San Justo'),
    ('9156a679-55ac-5572-a437-fd4cc4853827', '14140070', 'Colonia 10 de Julio', '14', 'San Justo'),
    ('2b69ee8c-9b87-572f-906f-0baeb4facba5', '14140080', 'Colonia Las Pichanas', '14', 'San Justo'),
    ('dc83a850-b47f-520f-b4ea-b719b7bd3473', '14140090', 'Colonia Marina', '14', 'San Justo'),
    ('3e37bf48-446b-5437-a7be-fd39584adbba', '14140100', 'Colonia Prosperidad', '14', 'San Justo'),
    ('67a2349f-b9d7-5f51-aa3a-62bdfed14a5a', '14140110', 'Colonia San Bartolomé', '14', 'San Justo'),
    ('3cbd6313-b7a6-5caf-945f-ea9ec185bc08', '14140120', 'Colonia San Pedro', '14', 'San Justo'),
    ('9a7a054f-b9a3-5c26-9c46-9fb77ec3b684', '14140130', 'Colonia Santa María', '14', 'San Justo'),
    ('e4f00199-c4ee-5b8e-a903-e061e6b5a554', '14140140', 'Colonia Valtelina', '14', 'San Justo'),
    ('8eeded88-0102-5149-b22c-9a0c0776004a', '14140150', 'Colonia Vignaud', '14', 'San Justo'),
    ('77aab1dd-5e14-5097-b639-6bc9755a67b7', '14140160', 'Devoto', '14', 'San Justo'),
    ('6ede0e65-7320-5c26-9aa4-599296a06540', '14140170', 'El Arañado', '14', 'San Justo'),
    ('30430257-d1a3-5da5-8d88-6cc949487b61', '14140180', 'El Fortín', '14', 'San Justo'),
    ('08e31cdf-c096-595f-87d7-2107e2883f79', '14140190', 'El Fuertecito', '14', 'San Justo'),
    ('fd4cc77f-850d-591f-bddf-030f5212cef6', '14140200', 'El Tío', '14', 'San Justo'),
    ('b0fcb0ac-583c-5500-acbe-c395008a542c', '14140210', 'Estación Luxardo', '14', 'San Justo'),
    ('d1df5d5e-0f81-5b15-a2f4-c25c0f2c871b', '14140215', 'Colonia Iturraspe', '14', 'San Justo'),
    ('34b92167-94de-5723-a827-6cba2ff4c82c', '14140220', 'Freyre', '14', 'San Justo'),
    ('7fa0648f-5d52-5695-a3b5-b6c55af0f09c', '14140230', 'La Francia', '14', 'San Justo'),
    ('4bf7c32f-108f-5187-9d06-9721dbc38b22', '14140240', 'La Paquita', '14', 'San Justo'),
    ('4fbdd6ba-15c8-593a-bbd1-95e8cb1ebb6e', '14140250', 'La Tordilla', '14', 'San Justo'),
    ('f4618821-04dd-5527-baff-4da1af0935b5', '14140260', 'Las Varas', '14', 'San Justo'),
    ('0b2abbef-9630-51b4-afbd-117cab2b039d', '14140270', 'Las Varillas', '14', 'San Justo'),
    ('eade6113-cd48-5eac-ac01-5093be52e44d', '14140280', 'Marull', '14', 'San Justo'),
    ('9a357a01-d6f1-5826-bb6d-1133d7972dd4', '14140290', 'Miramar', '14', 'San Justo'),
    ('3afe659e-d228-5c67-868d-8c5f46c6c98f', '14140300', 'Morteros', '14', 'San Justo'),
    ('fc7cb67c-564a-5d1a-a19a-204d6d097ad7', '14140310', 'Plaza Luxardo', '14', 'San Justo'),
    ('37b690b5-4055-50f3-804a-1ebfd7ae541f', '14140320', 'Plaza San Francisco', '14', 'San Justo'),
    ('ddab9790-da02-50d5-b92f-78187bfecb0c', '14140330', 'Porteña', '14', 'San Justo'),
    ('943cf0ed-4163-5876-ad98-4db7b1ba6d12', '14140340', 'Quebracho Herrado', '14', 'San Justo'),
    ('4d3c9575-7440-539d-9785-c841647120d2', '14140350', 'Sacanta', '14', 'San Justo'),
    ('3856c7c4-7b30-562c-b80d-e1faf901016c', '14140360', 'San Francisco', '14', 'San Justo'),
    ('b7ceb6e8-17ec-56eb-a243-6aacf249dda7', '14140370', 'Saturnino María Laspiur', '14', 'San Justo'),
    ('b7c3e079-bc58-5892-bf24-518068141ce6', '14140380', 'Seeber', '14', 'San Justo'),
    ('5e289829-50ad-58ef-b299-06e8c7732ca3', '14140390', 'Toro Pujio', '14', 'San Justo'),
    ('a25c4f36-86df-53de-a529-93701a50ed34', '14140400', 'Tránsito', '14', 'San Justo'),
    ('6bdff342-b8f0-59f6-aa3c-051e6fece19d', '14140420', 'Villa Concepción del Tío', '14', 'San Justo'),
    ('721dce5a-3f6c-589c-9e0f-0f3fe9c7bab6', '14140430', 'Villa del Tránsito', '14', 'San Justo'),
    ('4faf6dc1-3637-59a6-8b09-3c72e273f39a', '14140440', 'Villa San Esteban', '14', 'San Justo'),
    ('8ad27f14-814d-58b7-8bd0-2c2aee437de7', '14147010', 'Alta Gracia', '14', 'Santa María'),
    ('691f5c04-9aa8-55f4-8d34-5a53df1fa3df', '14147020', 'Anisacate', '14', 'Santa María'),
    ('e46d2a59-f88f-56fa-a236-1d40b2d55a0e', '14147030', 'Barrio Gilbert (1º de Mayo) - Tejas Tres', '14', 'Santa María'),
    ('28a8cbb2-2c80-525c-9003-626809a5c513', '1414703001', 'Barrio Gilbert', '14', 'Santa María'),
    ('bf5db1af-f0ba-569b-bc9b-dc91b5f66f57', '1414703002', 'Tejas Tres', '14', 'Santa María'),
    ('c9087e2a-010f-501f-ba58-4d28526610ca', '14147050', 'Bouwer', '14', 'Santa María'),
    ('a5409b94-d7ac-501b-9be5-1681b8e06387', '14147060', 'Caseros Centro', '14', 'Santa María'),
    ('de0b06b8-7004-54fc-83d3-2b0a192e3e68', '14147080', 'Despeñaderos', '14', 'Santa María'),
    ('61fba692-d92f-5a78-8537-92ef1c99d68f', '14147090', 'Dique Chico', '14', 'Santa María'),
    ('6e8f3b51-ed2d-530d-8ec6-7e9165e0857b', '14147100', 'Falda del Cañete', '14', 'Santa María'),
    ('e46ccfcd-c85b-5ab2-8ece-09285a6b58e1', '14147110', 'Falda del Carmen', '14', 'Santa María'),
    ('b220fe19-c611-57f7-ad68-306f088c05d1', '14147115', 'José de la Quintana', '14', 'Santa María'),
    ('5651d213-96c1-5230-83e4-366ca8305538', '14147120', 'La Boca del Río', '14', 'Santa María'),
    ('b4200cac-dd98-5049-828d-a70bd1e3d956', '14147150', 'La Paisanita', '14', 'Santa María'),
    ('ae51035a-a9ff-5f79-8b83-d29e8253af9c', '14147170', 'La Rancherita y Las Cascadas', '14', 'Santa María'),
    ('be341790-3526-5c4d-8cb0-82561dd77cd3', '14147180', 'La Serranita', '14', 'Santa María'),
    ('cb18ebce-9d64-5e2a-9b61-3df85ca881a0', '14147190', 'Los Cedros', '14', 'Santa María'),
    ('f2732417-a681-5ea7-9f7f-b1113bd8460a', '1414719001', 'Las Quintas', '14', 'Santa María'),
    ('ebb67c71-5eb3-50ce-9ab9-688eb7b8843f', '1414719002', 'Los Cedros', '14', 'Santa María'),
    ('97ff866a-9983-5500-9f46-c22f3fbf9b10', '14147200', 'Lozada', '14', 'Santa María'),
    ('dd8fe3a7-0745-5077-a6ed-daa157f491ff', '14147210', 'Malagueño', '14', 'Santa María'),
    ('e0b7f910-5e35-5216-ae96-09c81782c17f', '14147220', 'Monte Ralo', '14', 'Santa María'),
    ('11dbfeb7-9c54-5662-b7fa-6bdd37af75d7', '14147230', 'Potrero de Garay', '14', 'Santa María'),
    ('18bd21ec-6361-5803-a3c3-d468c6210c10', '14147240', 'Rafael García', '14', 'Santa María'),
    ('2024d199-a68f-5e95-8715-dab80dea5ae4', '14147250', 'San Clemente', '14', 'Santa María'),
    ('f6b39ecb-1718-5118-a9f1-6a5ce1a0c693', '14147270', 'Socavones', '14', 'Santa María'),
    ('99e8f621-27b3-5ced-958f-67806b24dca0', '14147280', 'Toledo', '14', 'Santa María'),
    ('3393a7f9-749c-5680-9047-6253a4cfbfc6', '14147300', 'Valle de Anisacate', '14', 'Santa María'),
    ('5fd2d012-01f5-57a8-bcc4-125eebf841a6', '14147310', 'Villa Ciudad de América', '14', 'Santa María'),
    ('9f12cf84-0d8a-5718-b099-3e2428e396d0', '1414731001', 'Barrio Villa del Parque', '14', 'Santa María'),
    ('4b8a3505-913d-54a4-8ed6-1a317d056355', '1414731002', 'Villa Ciudad de América', '14', 'Santa María'),
    ('127820d1-bbb9-5814-9038-f5942de32aa0', '14147320', 'Villa del Prado', '14', 'Santa María'),
    ('60aa933d-af55-51ca-9ae8-534a39d32869', '1414732001', 'Villa del Prado', '14', 'Santa María'),
    ('32d49dc4-86e5-5043-9bbd-cb41f3a0eb09', '1414732002', 'La Donosa', '14', 'Santa María'),
    ('51590078-379a-5fa1-9515-cb5087276a60', '14147330', 'Villa La Bolsa', '14', 'Santa María'),
    ('ef862f10-a721-50a0-ba33-e2497cc30d73', '14147340', 'Villa Los Aromos', '14', 'Santa María'),
    ('7d283737-97c5-532a-ba6b-e5c3f8ef84d4', '14147350', 'Villa Parque Santa Ana', '14', 'Santa María'),
    ('ff99770f-3608-559a-beee-4205b2de99bf', '1414735001', 'Mi Valle', '14', 'Santa María'),
    ('572dcfd1-d530-5a53-9e71-6de0034e296f', '1414735002', 'Villa Parque Santa Ana', '14', 'Santa María'),
    ('127b33d9-f294-579b-9324-9781b1eb8127', '14147360', 'Villa San Isidro', '14', 'Santa María'),
    ('d54cf69b-c7a9-55b4-8fd1-fddece8af171', '14154010', 'Caminiaga', '14', 'Sobremonte'),
    ('e02a7059-086b-510a-a980-9e84f1ef1c4c', '14154030', 'Chuña Huasi', '14', 'Sobremonte'),
    ('33bc5fb9-a440-5dc0-a3cb-c751b72c29ba', '14154040', 'Pozo Nuevo', '14', 'Sobremonte'),
    ('e4402e7b-6cd8-5e00-9aa0-4efea7a6be58', '14154050', 'San Francisco del Chañar', '14', 'Sobremonte'),
    ('1f32acb4-de82-5777-a2a9-b08542672d72', '14161010', 'Almafuerte', '14', 'Tercero Arriba'),
    ('6a060d6c-99e5-544c-ba91-cca5d7aacdf0', '14161020', 'Colonia Almada', '14', 'Tercero Arriba'),
    ('d02e7e0a-b972-53bd-a438-8b5d03bc3070', '14161030', 'Corralito', '14', 'Tercero Arriba'),
    ('3abfa036-ab9a-58d6-96c3-e0ce76d30156', '14161040', 'Dalmacio Vélez', '14', 'Tercero Arriba'),
    ('0358b629-e039-570a-8eb3-4e60fd25c9c6', '14161050', 'General Fotheringham', '14', 'Tercero Arriba'),
    ('63606712-49ca-5cfb-b6bd-b71ff288ed14', '14161060', 'Hernando', '14', 'Tercero Arriba'),
    ('46568166-7e2f-5cd4-9cdb-6f6ffa335cc5', '14161070', 'James Craik', '14', 'Tercero Arriba'),
    ('aeaff8d1-e7a4-5196-8637-59a9a9bb6abd', '14161080', 'Las Isletillas', '14', 'Tercero Arriba'),
    ('9033cebf-193b-538a-adf9-be3c7a5ee491', '14161090', 'Las Perdices', '14', 'Tercero Arriba'),
    ('9874287d-8806-533f-85c8-98700053bfcc', '14161100', 'Los Zorros', '14', 'Tercero Arriba'),
    ('ea64f185-ee6b-589c-91bc-ecef51d671de', '14161110', 'Oliva', '14', 'Tercero Arriba'),
    ('fe618c6f-7953-5e64-9b19-ea5b7c11dfc2', '14161120', 'Pampayasta Norte', '14', 'Tercero Arriba'),
    ('4c0a835a-7a13-595c-90d2-c09221dfebb1', '14161130', 'Pampayasta Sud', '14', 'Tercero Arriba'),
    ('64c6625f-a3e3-52e3-a4e5-6c28d7b9973b', '14161140', 'Punta del Agua', '14', 'Tercero Arriba'),
    ('f60847bf-d2ac-5ba8-a89f-dd4355b5f0d9', '14161150', 'Río Tercero', '14', 'Tercero Arriba'),
    ('5ee091de-d6e2-58fa-a0d9-aa556a13bb36', '14161160', 'Tancacha', '14', 'Tercero Arriba'),
    ('283fc523-24db-5378-8ac1-89dd83865077', '14161170', 'Villa Ascasubi', '14', 'Tercero Arriba'),
    ('0e747dce-7349-54c8-a668-dcb527ff0210', '14168010', 'Candelaria Sur', '14', 'Totoral'),
    ('a22ea306-1623-582d-96cc-52ebe7fd0d0d', '14168020', 'Cañada de Luque', '14', 'Totoral'),
    ('d6cb9c93-b8f3-5104-9276-563aa3dc8827', '14168030', 'Capilla de Sitón', '14', 'Totoral'),
    ('adb19b1d-1cd3-54d5-ad40-0632ae9f80e2', '14168040', 'La Pampa', '14', 'Totoral'),
    ('31a0add3-47a8-53b4-b538-d151d5e2b6b9', '14168060', 'Las Peñas', '14', 'Totoral'),
    ('c2edba92-8ec0-5a46-8bfe-011833d94115', '14168070', 'Los Mistoles', '14', 'Totoral'),
    ('81a405a6-755b-5270-8e61-cf9abb361069', '14168080', 'Santa Catalina', '14', 'Totoral'),
    ('527449f3-c5c6-5639-afa7-1b64db7b685e', '14168090', 'Sarmiento', '14', 'Totoral'),
    ('b7147072-3191-5272-881a-83e6be2bfeb6', '14168100', 'Simbolar', '14', 'Totoral'),
    ('4fbe09d4-0bd5-5ab9-80f2-b3c9b70c9b91', '14168110', 'Sinsacate', '14', 'Totoral'),
    ('d555b61c-83c9-53eb-8d0f-b5bc3534f6a8', '14168120', 'Villa del Totoral', '14', 'Totoral'),
    ('d52c8f65-e0bd-5c0d-b46d-6139054720db', '14175020', 'Churqui Cañada', '14', 'Tulumba'),
    ('329a858b-3c7d-52fd-ab7a-208d12988b95', '14175030', 'El Rodeo', '14', 'Tulumba'),
    ('ccbabfa5-e74f-5817-8e10-9ee566dfa80c', '14175040', 'El Tuscal', '14', 'Tulumba'),
    ('f5f1a835-1bd6-5172-a3d8-2c7f3e45f4eb', '14175050', 'Las Arrias', '14', 'Tulumba'),
    ('01ed3b03-3c58-5247-b5be-edc366e33468', '14175060', 'Lucio V. Mansilla', '14', 'Tulumba'),
    ('a15301e6-34bb-567e-bec7-2b28cee61396', '14175070', 'Rosario del Saladillo', '14', 'Tulumba'),
    ('9dddeb70-a252-561c-951d-e6bb7ab2b52d', '14175080', 'San José de la Dormida', '14', 'Tulumba'),
    ('42bcee44-5928-594a-b42d-c0c4378f4d49', '14175090', 'San José de las Salinas', '14', 'Tulumba'),
    ('d43a82cc-c61f-5f11-bfd3-11953fdf349c', '14175100', 'San Pedro Norte', '14', 'Tulumba'),
    ('35bad6c0-8e0d-5813-bec9-3910a1ad0424', '14175110', 'Villa Tulumba', '14', 'Tulumba'),
    ('e1cf8cee-7d27-5c64-8ed4-89610e307c6c', '14182010', 'Aldea Santa María', '14', 'Unión')
ON CONFLICT (indec_id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('ef3e2578-941f-5984-b749-f4eea85a69e9', '14182020', 'Alto Alegre', '14', 'Unión'),
    ('65e9dfd0-4f92-5be5-b6e7-70263b03dae5', '14182030', 'Ana Zumarán', '14', 'Unión'),
    ('5c48ca6f-0c86-5b83-af32-7d063cfd3ecf', '14182040', 'Ballesteros', '14', 'Unión'),
    ('4a685c1c-a379-511f-9204-ca953d800bff', '14182050', 'Ballesteros Sud', '14', 'Unión'),
    ('76968a35-38fc-5d63-9084-c5f23a6bf3f6', '14182060', 'Bell Ville', '14', 'Unión'),
    ('5800ad49-3f41-579e-9303-1417ddb6acd6', '14182070', 'Benjamín Gould', '14', 'Unión'),
    ('9c6573c8-e790-58f9-9949-09cfa91541df', '14182080', 'Canals', '14', 'Unión'),
    ('a4671c02-e7fd-5f45-99d2-f02313dc742d', '14182090', 'Chilibroste', '14', 'Unión'),
    ('3ce7c916-cbcd-57f9-9350-f5a4b547baea', '14182100', 'Cintra', '14', 'Unión'),
    ('1bc79d08-3b37-51e0-a9a3-7e53d4883ee8', '14182110', 'Colonia Bismarck', '14', 'Unión'),
    ('9c170b79-5a5a-593b-b736-b7871351c51c', '14182120', 'Colonia Bremen', '14', 'Unión'),
    ('c2c3d3d1-8b9d-5fa4-a11b-f794e3dcad66', '14182130', 'Idiazabal', '14', 'Unión'),
    ('c0621306-b2c7-5350-be2a-a6c61ea84aa1', '14182140', 'Justiniano Posse', '14', 'Unión'),
    ('288d1581-bab4-50bb-b62b-df3a7ad1bb72', '14182150', 'Laborde', '14', 'Unión'),
    ('3edbff3a-2bd1-50d1-9d71-1160a625d3fc', '14182160', 'Monte Leña', '14', 'Unión'),
    ('f074ede8-c67c-5445-933c-9d7343483a94', '14182170', 'Monte Maíz', '14', 'Unión'),
    ('35afdb61-f1a7-504d-b5f9-afc107190d40', '14182180', 'Morrison', '14', 'Unión'),
    ('1e03a4b9-5e5d-5172-b12d-5ed18a241733', '14182190', 'Noetinger', '14', 'Unión'),
    ('8346a6b4-273a-532b-80ba-bd9eb6149a6c', '14182200', 'Ordoñez', '14', 'Unión'),
    ('8f137664-594b-5643-ac96-af3461286c6d', '14182210', 'Pascanas', '14', 'Unión'),
    ('347fca07-ae74-59f8-8ad1-e5b4c7381154', '14182220', 'Pueblo Italiano', '14', 'Unión'),
    ('013cbc60-84d2-5cc6-a206-62eac83421f1', '14182230', 'Ramón J. Cárcano', '14', 'Unión'),
    ('6ab70797-d053-5f66-83d4-ad9a026a1ec8', '14182240', 'San Antonio de Litín', '14', 'Unión'),
    ('c1d487a3-c474-5e83-9fc9-0d338263c4fe', '14182250', 'San Marcos', '14', 'Unión'),
    ('1ee291df-741d-5732-8331-691e2d8ab582', '14182260', 'San Severo', '14', 'Unión'),
    ('b0e263dd-65a7-5b63-985e-ad747b8a6f08', '14182270', 'Viamonte', '14', 'Unión'),
    ('867fe2a5-b0fb-5f41-9f0c-0d4419fa18c7', '14182280', 'Villa Los Patos', '14', 'Unión'),
    ('272d6513-2b44-53c4-9b49-e08782119494', '14182290', 'Wenceslao Escalante', '14', 'Unión'),
    ('611e4f97-b96a-5453-a7b1-d18a30d400de', '18007010', 'Bella Vista', '18', 'Bella Vista'),
    ('c444d665-34dd-5f8b-af39-133641c1ed64', '18014010', 'Berón de Astrada', '18', 'Berón de Astrada'),
    ('362beb5f-a0ac-5e64-9407-78e4c6e172af', '18014020', 'Yahapé', '18', 'Berón de Astrada'),
    ('ed199aca-9f81-52f2-929f-495fb87b57ce', '18021020', 'Corrientes', '18', 'Capital'),
    ('48995dd6-3064-5a58-a79e-db80794201a0', '18021040', 'Riachuelo', '18', 'Capital'),
    ('01fb3fe0-2a80-53d1-8b4a-73204f084b1b', '18021050', 'San Cayetano', '18', 'Capital'),
    ('6010e1db-be14-57a8-bfa3-e4ebb0fc1785', '18028010', 'Concepción', '18', 'Concepción'),
    ('de3afe69-9ccd-512b-a370-94db7daac2f7', '18028020', 'Santa Rosa', '18', 'Concepción'),
    ('c646443b-2bd1-544b-a6fe-f5b63961a868', '18028030', 'Tabay', '18', 'Concepción'),
    ('8efa9bce-fd3d-542d-93fb-8b2685687330', '18028040', 'Tatacua', '18', 'Concepción'),
    ('a8d58f69-ff68-566f-b2f5-0ca6a7481477', '18035010', 'Cazadores Correntinos', '18', 'Curuzú Cuatiá'),
    ('40f3281e-8202-5a61-8d3a-3b7278ef49a7', '18035020', 'Curuzú Cuatiá', '18', 'Curuzú Cuatiá'),
    ('ac4c5743-e742-5677-b493-e926433359e6', '18035030', 'Perugorría', '18', 'Curuzú Cuatiá'),
    ('3d35c6c6-4eab-597a-8a9c-585c3044fa87', '18042010', 'El Sombrero', '18', 'Empedrado'),
    ('043ca48e-f5aa-5e88-a51a-555ec4b035d0', '18042020', 'Empedrado', '18', 'Empedrado'),
    ('6843fd4b-1c5d-59f6-a312-d52d664f16b6', '18049010', 'Esquina', '18', 'Esquina'),
    ('d0ca8cc1-ce32-53e1-8bac-7499dd37703a', '18049020', 'Pueblo Libertador', '18', 'Esquina'),
    ('e8fdfe3b-6dfa-52b5-9446-7333e290ecfe', '18056010', 'Alvear', '18', 'General Alvear'),
    ('74fa468c-66db-5203-a5a7-85828a20692b', '18056020', 'Estación Torrent', '18', 'General Alvear'),
    ('edf242f6-22ac-5c0b-afd4-08e830ce0c0f', '18063010', 'Itá Ibaté', '18', 'General Paz'),
    ('c7c51f87-5b31-5462-ac63-99f6e96a449e', '18063020', 'Lomas de Vallejos', '18', 'General Paz'),
    ('37011f15-b19b-5dc1-a603-0d8c37c20103', '18063030', 'Nuestra Señora del Rosario de Caá Catí', '18', 'General Paz'),
    ('94b50632-dd3c-593a-995f-02b1ecf6072f', '18063040', 'Palmar Grande', '18', 'General Paz'),
    ('d95bd9ab-6efc-558b-8db9-b088b16a904c', '18070010', 'Carolina', '18', 'Goya'),
    ('5379d161-1c79-5a11-89a6-0379570718d7', '18070020', 'Goya', '18', 'Goya'),
    ('ca64b2c4-3828-5f27-896b-be483f6ac0d9', '18077010', 'Itatí', '18', 'Itatí'),
    ('67c800f8-e8c3-5879-a3a0-aa54307f4a21', '18077020', 'Ramada Paso', '18', 'Itatí'),
    ('1b180eb6-6905-5a01-b75f-116ecd1444ac', '18084010', 'Colonia Liebig''s', '18', 'Ituzaingó'),
    ('d4aaa2da-5b5c-5472-a9ff-c4fede67f1c4', '18084020', 'Ituzaingó', '18', 'Ituzaingó'),
    ('80b07885-ed93-5e50-9410-d8d626bdd464', '18084030', 'San Antonio', '18', 'Ituzaingó'),
    ('4d4db0c1-1d97-594b-8bae-32a56e8fda1d', '18084040', 'San Carlos', '18', 'Ituzaingó'),
    ('ee20a7d4-25f8-5264-9f16-acfe7d57cc31', '18084050', 'Villa Olivari', '18', 'Ituzaingó'),
    ('c6e585f0-f66b-55a0-9ba8-5c74a52334bd', '18091010', 'Cruz de los Milagros', '18', 'Lavalle'),
    ('da0356cd-f2ee-5b63-a4b2-970524d04ae3', '18091020', 'Gobernador Juan E. Martínez', '18', 'Lavalle'),
    ('0ceafcf2-def5-5aa6-b55c-9696cabc932e', '18091030', 'Lavalle', '18', 'Lavalle'),
    ('88f4065e-22da-5d06-ac6a-22d48ec482e1', '18091040', 'Santa Lucía', '18', 'Lavalle'),
    ('63c1ec68-cb4b-5bca-935b-8c6c6b55ad05', '18091050', 'Villa Córdoba', '18', 'Lavalle'),
    ('30bbbb09-9571-50ac-8480-73f90cff4878', '18091060', 'Yatayti Calle', '18', 'Lavalle'),
    ('9fe3c97f-5457-5564-9b5f-a04ff6ab43e8', '18098010', 'Mburucuyá', '18', 'Mburucuyá'),
    ('a4d4ce6e-0935-5980-aafc-1282d0a4d571', '18105010', 'Felipe Yofré', '18', 'Mercedes'),
    ('9831606d-d233-52a9-917e-172885d8ad70', '18105020', 'Mariano I. Loza', '18', 'Mercedes'),
    ('4dd1c6c2-e382-5b36-84ba-ff8f63a466d7', '18105030', 'Mercedes', '18', 'Mercedes'),
    ('4462e9d4-6419-5387-84f0-6d3b6d7763f9', '18112010', 'Colonia Libertad', '18', 'Monte Caseros'),
    ('1319fd8c-ede2-55a3-be47-74786370765d', '18112020', 'Estación Libertad', '18', 'Monte Caseros'),
    ('82b6411f-ae68-59cc-ba8c-21011bb01ff9', '18112030', 'Juan Pujol', '18', 'Monte Caseros'),
    ('b41aed36-768d-5989-9108-60ae67c1fe1e', '18112040', 'Mocoretá', '18', 'Monte Caseros'),
    ('73a5fe7f-0df8-5159-8063-28c9ee3ce104', '18112050', 'Monte Caseros', '18', 'Monte Caseros'),
    ('c538a063-fcf6-53c8-b51b-5313b3d4f0b4', '18112060', 'Parada Acuña', '18', 'Monte Caseros'),
    ('9d322989-46ee-5172-ab82-5858f987015d', '18112070', 'Parada Labougle', '18', 'Monte Caseros'),
    ('290b872c-c8fe-5ac7-bcdc-6193455e3c43', '18119010', 'Bonpland', '18', 'Paso de los Libres'),
    ('46afc725-4a0c-560c-b50d-69963619dc2c', '18119020', 'Parada Pucheta', '18', 'Paso de los Libres'),
    ('3dc54313-b693-5486-b5fe-69ea891770b6', '18119030', 'Paso de los Libres', '18', 'Paso de los Libres'),
    ('c34a7b92-a4e9-5ee5-8db9-e028e66db687', '18119040', 'Tapebicuá', '18', 'Paso de los Libres'),
    ('581cab70-0971-53c8-bb12-607b49fbe128', '18126010', 'Saladas', '18', 'Saladas'),
    ('93528843-afed-5dca-a424-642ecd26b5fc', '18126020', 'San Lorenzo', '18', 'Saladas'),
    ('107a1085-c42e-553b-a97e-971c3f47d1d6', '18133010', 'Ingenio Primer Correntino', '18', 'San Cosme'),
    ('5f1a008b-8d4c-573c-a7d3-2cb0b1625a9a', '18133020', 'Paso de la Patria', '18', 'San Cosme'),
    ('d36d21b0-cc1f-55ab-b7aa-8a4bd6776bfa', '18133030', 'San Cosme', '18', 'San Cosme'),
    ('3890664b-b2dc-5515-a824-c279e7a829c4', '18133040', 'Santa Ana', '18', 'San Cosme'),
    ('30974068-5245-5b0b-9a99-4b2d3c90133f', '18140010', 'San Luis del Palmar', '18', 'San Luis del Palmar'),
    ('651f3ccd-880f-5787-9f84-69a5a0a00ed8', '18147010', 'Colonia Carlos Pellegrini', '18', 'San Martín'),
    ('4ef4940d-c9a8-5d0f-9972-eab13fcc28cf', '18147020', 'Guaviraví', '18', 'San Martín'),
    ('0eac5d65-318b-5612-b4a9-dd095675f7a9', '18147030', 'La Cruz', '18', 'San Martín'),
    ('c5836c42-f4a8-5101-89e4-303eabd50824', '18147040', 'Yapeyú', '18', 'San Martín'),
    ('4be6ff8c-b8e9-516e-8c14-1a616d1e5253', '18154010', 'Loreto', '18', 'San Miguel'),
    ('68e0d0f3-4847-57f8-b2fa-4127d23c01d3', '18154020', 'San Miguel', '18', 'San Miguel'),
    ('8be85f0d-97b3-5c26-b557-e01ad40c7534', '18161010', 'Chavarría', '18', 'San Roque'),
    ('d43d52dc-9a74-5328-a1f9-80f2c71cb122', '18161020', 'Colonia Pando', '18', 'San Roque'),
    ('67002595-cc8d-56b6-98db-337f1239b28f', '18161030', '9 de Julio', '18', 'San Roque'),
    ('12cc0d0f-6ed3-59ca-8327-3841352b2123', '18161040', 'Pedro R. Fernández', '18', 'San Roque'),
    ('f584a9ab-5d1a-5d86-805d-3071bb683d75', '18161050', 'San Roque', '18', 'San Roque'),
    ('8b495447-f110-5783-b2ac-49e2ecd9f086', '18168010', 'José Rafael Gómez', '18', 'Santo Tomé'),
    ('3f93fdf4-849d-5f0a-9a39-a4c8e288f962', '18168020', 'Garruchos', '18', 'Santo Tomé'),
    ('bff55e03-c5fa-5f84-b2c6-2bc5a60ad8af', '18168030', 'Gobernador Igr. Valentín Virasoro', '18', 'Santo Tomé'),
    ('f2fccadb-7494-5a5a-9a21-cb6b5b5a42e7', '18168040', 'Santo Tomé', '18', 'Santo Tomé'),
    ('7c00ea36-7cbc-5528-8d9d-7089e47bf23a', '18175010', 'Sauce', '18', 'Sauce'),
    ('01ec0447-da4f-547f-99a1-35b87587d6d9', '22007010', 'Concepción del Bermejo', '22', 'Almirante Brown'),
    ('76c4f278-6e3f-51bd-a95a-c3574087e9b1', '22007020', 'Los Frentones', '22', 'Almirante Brown'),
    ('291aa7aa-b815-5206-8872-7cb99a79ac0b', '22007030', 'Pampa del Infierno', '22', 'Almirante Brown'),
    ('20edf401-74b6-5df2-ae9a-5db0aa1d6494', '22007040', 'Río Muerto', '22', 'Almirante Brown'),
    ('50075fd4-26f3-539c-87e3-0a3de063442c', '22007050', 'Taco Pozo', '22', 'Almirante Brown'),
    ('9c235a0c-8729-56dd-ba45-fe0fb61e6264', '22014010', 'General Vedia', '22', 'Bermejo'),
    ('97790ed1-8d84-5c8c-b0e8-222e2cc7ab84', '22014020', 'Isla del Cerrito', '22', 'Bermejo'),
    ('82ccec9f-054d-5dba-b264-fdf4211150fb', '22014030', 'La Leonesa', '22', 'Bermejo'),
    ('1cfb758c-6a0b-556a-a5bd-3db87274697a', '22014040', 'Las Palmas', '22', 'Bermejo'),
    ('1e62c52a-5778-5761-977d-95e22e529859', '22014050', 'Puerto Bermejo Nuevo', '22', 'Bermejo'),
    ('8637ee38-8411-5df8-b50d-caf0920b7ff8', '22014060', 'Puerto Bermejo Viejo', '22', 'Bermejo'),
    ('6e749470-5baf-5eac-ab90-c8d8aadeed14', '22014070', 'Puerto Eva Perón', '22', 'Bermejo'),
    ('e5d0a193-8d10-506c-8fa6-c1368cd4ca74', '22021010', 'Presidencia Roque Sáenz Peña', '22', 'Comandante Fernández'),
    ('094d76ce-3dbe-51e1-8e8b-95bc87cbd924', '22028010', 'Charata', '22', 'Chacabuco'),
    ('d9801eba-3e35-5932-8889-6cfd9a6563b5', '22036010', 'Gancedo', '22', '12 de Octubre'),
    ('b0556b2d-5cd3-544c-b1a0-180714963218', '22036020', 'General Capdevila', '22', '12 de Octubre'),
    ('d0adb3f8-b8fe-554a-b723-a764923c13fc', '22036030', 'General Pinedo', '22', '12 de Octubre'),
    ('fb0d202e-18cc-5736-8bbb-cf06a2e70290', '22036040', 'Mesón de Fierro', '22', '12 de Octubre'),
    ('ff2edfda-f303-5618-a660-41a15b0a9a53', '22036050', 'Pampa Landriel', '22', '12 de Octubre'),
    ('bb43d8a0-a6e0-5461-bd80-1c38c1519c73', '22039010', 'Hermoso Campo', '22', '2 de Abril'),
    ('4c553b82-9087-55cb-a35d-b9216a043b07', '22039020', 'Itín', '22', '2 de Abril'),
    ('3e34a039-048e-504e-8892-5ee3aa5e664c', '22043010', 'Chorotis', '22', 'Fray Justo Santa María de Oro'),
    ('ad480f38-c7fa-53fb-953e-bdf828e1a40c', '22043020', 'Santa Sylvina', '22', 'Fray Justo Santa María de Oro'),
    ('ac18283e-cfe3-59e8-a963-4bfeb930ed53', '22043030', 'Venados Grandes', '22', 'Fray Justo Santa María de Oro'),
    ('93cd6d6e-6ea3-5db1-818a-90911f08a201', '22049010', 'Corzuela', '22', 'General Belgrano'),
    ('863fd135-a495-54cc-8c67-924e331e087f', '22056010', 'La Escondida', '22', 'General Donovan'),
    ('9284eb34-5a49-5ada-9a0e-4c4f055877f5', '22056020', 'La Verde', '22', 'General Donovan'),
    ('06c70798-4161-5e4e-af4f-f6d3e40895fb', '22056030', 'Lapachito', '22', 'General Donovan'),
    ('cdcb6ccc-bce0-592a-be76-2598b247dbfb', '22056040', 'Makallé', '22', 'General Donovan'),
    ('70d6c38c-5391-54b1-b1fa-3356742f8b24', '22063010', 'El Espinillo', '22', 'General Güemes'),
    ('91deb535-679b-58b3-9c33-e04680bf559e', '22063020', 'El Sauzal', '22', 'General Güemes'),
    ('4c82531d-29a3-59a8-92c6-6845d311f6b7', '22063030', 'El Sauzalito', '22', 'General Güemes'),
    ('b6a73c1a-7e03-5a86-83c4-ee6255747bf4', '22063040', 'Fortín Lavalle', '22', 'General Güemes'),
    ('9f11d0e9-28a4-51af-b9c6-d9a3e39970d9', '22063050', 'Fuerte Esperanza', '22', 'General Güemes'),
    ('55c86064-5026-5a48-87fb-5b26e389fabc', '22063060', 'Juan José Castelli', '22', 'General Güemes'),
    ('9d9cf142-da26-5609-beae-0a59fc415ea8', '22063070', 'Miraflores', '22', 'General Güemes'),
    ('f973f0e6-3a14-5818-a173-51d081d827e7', '22063080', 'Nueva Pompeya', '22', 'General Güemes'),
    ('7a4ef9c1-bc03-5d35-bb43-64b8e1fd66f6', '22063100', 'Villa Río Bermejito', '22', 'General Güemes'),
    ('e71e8984-a78a-50b9-a14e-4a2357987126', '22063110', 'Wichi', '22', 'General Güemes'),
    ('e09298b6-ccae-596e-885f-8a3cfa9d3b21', '22063120', 'Zaparinqui', '22', 'General Güemes'),
    ('03a58e8c-210e-5cd8-af85-13069c76ed3c', '22070010', 'Avia Terai', '22', 'Independencia'),
    ('6e23b206-7834-51db-989b-99fe161d45fc', '22070020', 'Campo Largo', '22', 'Independencia'),
    ('f2563a21-f6a1-5735-a341-91d61c35ab99', '22070030', 'Fortín Las Chuñas', '22', 'Independencia'),
    ('92a4ff25-3116-56f4-8b2a-4550c63ad514', '22070040', 'Napenay', '22', 'Independencia'),
    ('a3890893-f8a4-57a0-9b49-0a0eac54bed6', '22077010', 'Colonia Popular', '22', 'Libertad'),
    ('6144da7a-41ec-5725-9a54-8712db9c4dcf', '22077020', 'Estación General Obligado', '22', 'Libertad'),
    ('1e967c62-5e07-5bbf-a76f-77b979630748', '22077030', 'Laguna Blanca', '22', 'Libertad'),
    ('2ca24273-1cbe-5be1-84ee-aa94d014a732', '22077040', 'Puerto Tirol', '22', 'Libertad'),
    ('b48e6dbc-a8f0-5d68-979a-724235859245', '22084010', 'Ciervo Petiso', '22', 'Libertador General San Martín'),
    ('7f0507db-4067-5b5b-8d75-72a4428aee43', '22084020', 'General José de San Martín', '22', 'Libertador General San Martín'),
    ('30ea8a38-a4a4-539b-bb72-33c33f943d5a', '22084030', 'La Eduvigis', '22', 'Libertador General San Martín'),
    ('a48dff60-b446-586e-9105-4dfb0fa9fbf3', '22084040', 'Laguna Limpia', '22', 'Libertador General San Martín'),
    ('f16b5de0-bb9a-570d-b752-9a6763657195', '22084050', 'Pampa Almirón', '22', 'Libertador General San Martín'),
    ('0cc09f81-17c8-5f90-b2e2-56e8ff04003a', '22084060', 'Pampa del Indio', '22', 'Libertador General San Martín'),
    ('06000407-3b63-5658-9a9e-9d3c439a8a95', '22084070', 'Presidencia Roca', '22', 'Libertador General San Martín'),
    ('a5d50ff7-03e0-56c4-9949-e1f4b12ae8a3', '22084080', 'Selvas del Río de Oro', '22', 'Libertador General San Martín'),
    ('b6ff874c-c062-50dc-b95e-d3a24858aca8', '22091010', 'Tres Isletas', '22', 'Maipú'),
    ('2b30b4ce-4ea1-56f1-a12a-a5561635890f', '22098010', 'Coronel Du Graty', '22', 'Mayor Luis J. Fontana'),
    ('3654a614-813c-59b8-829e-7ce5503126b4', '22098020', 'Enrique Urien', '22', 'Mayor Luis J. Fontana'),
    ('66d278f2-f250-55df-a674-404ee034f017', '22098030', 'Villa Angela', '22', 'Mayor Luis J. Fontana'),
    ('ac51ab82-6519-5d8f-a662-a8937e38c728', '22105010', 'Las Breñas', '22', '9 de Julio'),
    ('ad615863-a53e-51aa-ad8d-bc30768fa2f1', '22112010', 'La Clotilde', '22', 'O''Higgins'),
    ('56d36153-8713-51d5-bab8-7f9bd8103864', '22112020', 'La Tigra', '22', 'O''Higgins'),
    ('87d18dee-4432-5487-8a05-211388991e59', '22112030', 'San Bernardo', '22', 'O''Higgins'),
    ('c2e179e2-c7d1-5b94-82ce-b6f11b3a589d', '22119010', 'Presidencia de la Plaza', '22', 'Presidencia de la Plaza'),
    ('0d7d15ff-0ed0-5b92-bcb3-814fe3abee05', '22126010', 'Barrio de los Pescadores', '22', '1° de Mayo'),
    ('d750c9f0-bb48-5310-8057-5c069c8274f2', '22126020', 'Colonia Benítez', '22', '1° de Mayo'),
    ('2aa2e220-3510-5798-bf99-b8082a1ba150', '22126030', 'Margarita Belén', '22', '1° de Mayo'),
    ('b08e7949-1538-5401-802d-f5ae9d5a4120', '22133010', 'Quitilipi', '22', 'Quitilipi'),
    ('ad355ce2-1462-5ae4-b927-98570b4b51d0', '22133020', 'Villa El Palmar', '22', 'Quitilipi'),
    ('84d963d6-bb6f-5c59-a98b-f41294a04c39', '22140010', 'Barranqueras', '22', 'San Fernando'),
    ('f855c225-3253-5352-8271-055fda426b0d', '22140020', 'Basail', '22', 'San Fernando'),
    ('1a75cc45-6de2-5817-acda-30696b027a87', '22140030', 'Colonia Baranda', '22', 'San Fernando'),
    ('cf4996d5-a7e2-56bc-ad76-4720909ef3c6', '22140040', 'Fontana', '22', 'San Fernando'),
    ('58def574-d342-5333-848f-8d32c9d4a1ce', '22140050', 'Puerto Vilelas', '22', 'San Fernando'),
    ('9ceac9eb-24a1-5b09-a14e-01e6750750ff', '22140060', 'Resistencia', '22', 'San Fernando'),
    ('567adbe6-202f-5a7c-bd8b-720e916ee15d', '22147010', 'Samuhú', '22', 'San Lorenzo'),
    ('aa6aafe0-212e-577d-ac03-166a4d42e98b', '22147020', 'Villa Berthet', '22', 'San Lorenzo'),
    ('725a0cdd-a3ba-5b26-be90-7303cef93e8b', '22154010', 'Capitán Solari', '22', 'Sargento Cabral'),
    ('b2c105cf-ad17-5617-a149-53c9833fbdfe', '22154020', 'Colonia Elisa', '22', 'Sargento Cabral'),
    ('52d3aef0-b35a-5ecb-b684-730df97ab493', '22154030', 'Colonias Unidas', '22', 'Sargento Cabral'),
    ('e5eca88b-169f-58b1-b3a7-fcc53d9aad1b', '22154040', 'Ingeniero Barbet', '22', 'Sargento Cabral'),
    ('3b37973a-d069-5978-adcb-37ddf51da3ca', '22154050', 'Las Garcitas', '22', 'Sargento Cabral'),
    ('7eeb4212-0b43-53c4-8787-4f471c0e6b90', '22161010', 'Charadai', '22', 'Tapenagá'),
    ('b0d83066-abea-5892-88d6-65ebb8e44499', '22161020', 'Cote Lai', '22', 'Tapenagá'),
    ('2c214cca-6bcf-5845-8aaf-11370eacf059', '22161030', 'Haumonia', '22', 'Tapenagá'),
    ('7dbee9ca-d6e4-54f0-89fb-23e7a5de9047', '22161040', 'Horquilla', '22', 'Tapenagá'),
    ('bf96c656-3a86-521d-bda5-2371f9289051', '22161050', 'La Sabana', '22', 'Tapenagá'),
    ('26a214b3-5958-51ce-9d3d-30a1064aac15', '22168010', 'Colonia Aborigen', '22', '25 de Mayo'),
    ('317c107b-1adb-548a-af86-78a4717d9f3c', '22168020', 'Machagai', '22', '25 de Mayo'),
    ('1ad0df19-285c-5043-8200-d5c0d1feffda', '22168030', 'Napalpí', '22', '25 de Mayo'),
    ('13790bcc-ddaa-5a37-9512-ebde43100345', '26007010', 'Arroyo Verde', '26', 'Biedma'),
    ('227c151b-5933-51d8-b2d8-e630b4f6d6b1', '26007020', 'Puerto Madryn', '26', 'Biedma'),
    ('12d359a9-6c23-53b6-a1f6-960616408013', '26007030', 'Puerto Pirámides', '26', 'Biedma'),
    ('150fa893-ab14-5023-acc9-210e542df366', '26007040', 'Quintas El Mirador', '26', 'Biedma'),
    ('e7ba6b80-1529-5e76-beb6-afd2fb964fb6', '26007050', 'Reserva Area Protegida El Doradillo', '26', 'Biedma'),
    ('e8651624-81a8-59a4-8907-ff0710e3a02e', '26014010', 'Buenos Aires Chico', '26', 'Cushamen'),
    ('57ba5514-f8f4-5b9f-ba12-6831ccdf3395', '26014020', 'Cholila', '26', 'Cushamen'),
    ('96760743-9eb1-5d5f-8be4-dc4b3ef763d5', '26014025', 'Costa del Chubut', '26', 'Cushamen'),
    ('b0ef3706-25cb-5a9e-93fc-dfffde8e5d83', '26014030', 'Cushamen Centro', '26', 'Cushamen'),
    ('e8ffd7a1-c626-5592-984a-379225ad7dfc', '26014040', 'El Hoyo', '26', 'Cushamen'),
    ('04080c10-994b-512c-bb37-d9a3794115e9', '26014050', 'El Maitén', '26', 'Cushamen'),
    ('91d0398e-5320-5eff-9be4-f180cdbbc2e6', '26014060', 'Epuyén', '26', 'Cushamen'),
    ('437433b1-7d48-5f52-9ff0-df181c46b43b', '26014065', 'Fofo Cahuel', '26', 'Cushamen'),
    ('8defc560-f802-5b85-80f9-b3dd6569a9ea', '26014070', 'Gualjaina', '26', 'Cushamen'),
    ('efdefe55-6e63-550c-9876-ac9b8264452f', '26014080', 'Lago Epuyén', '26', 'Cushamen'),
    ('4901ae4e-9c40-5247-9f25-14307601f610', '26014090', 'Lago Puelo', '26', 'Cushamen'),
    ('cf0a6006-7d26-5295-a5ea-bdc747ff37fe', '26014100', 'Leleque', '26', 'Cushamen'),
    ('f70c31df-82b4-5e0c-bb83-6f1b44c82841', '26021010', 'Astra', '26', 'Escalante'),
    ('ec418d2d-3964-5652-8ba9-1b01f4ddb1b6', '26021020', 'Bahía Bustamante', '26', 'Escalante'),
    ('edc7220b-42ce-50bb-bbdd-9f276ba6ef71', '26021030', 'Comodoro Rivadavia', '26', 'Escalante'),
    ('e28e2dcf-3811-5eb6-8477-bb975aa070e5', '2602103001', 'Acceso Norte', '26', 'Escalante'),
    ('14025044-a9f5-57c5-a944-5cb93f356542', '2602103002', 'Barrio Caleta Córdova', '26', 'Escalante'),
    ('497b6954-f87d-5918-ac27-092d687ffa64', '2602103003', 'Caleta Olivares', '26', 'Escalante'),
    ('be846571-1731-541f-9729-cb2e4cdf23fc', '2602103004', 'Barrio Castelli', '26', 'Escalante'),
    ('c1e281d2-2acd-5567-949a-59c9ccf53760', '2602103005', 'Barrio Ciudadela', '26', 'Escalante'),
    ('6376657d-b5d1-574b-875f-929c205cf36b', '2602103006', 'Barrio Gasoducto', '26', 'Escalante'),
    ('6c6ed168-3691-5806-b4ad-88127e8070b4', '2602103007', 'Barrio Güemes', '26', 'Escalante'),
    ('20081212-75ec-51c7-844e-150dcb52b13f', '2602103008', 'Barrio Laprida', '26', 'Escalante'),
    ('439950d2-57f3-5933-9ac2-61ba570ebce8', '2602103009', 'Barrio Manantial Rosales', '26', 'Escalante'),
    ('2b8e2c6c-9bcb-5f4c-a340-2dfb8091b5cc', '2602103010', 'Barrio Militar y Aeropuerto', '26', 'Escalante'),
    ('89fdbbdf-7494-56ed-a7a2-fca52293370c', '2602103011', 'Barrio Próspero Palazzo', '26', 'Escalante'),
    ('c1be8252-75f9-5889-aaa2-31ff5f6aea7c', '2602103012', 'Barrio Restinga Alí', '26', 'Escalante'),
    ('62dcff27-feb3-5820-82a8-e3b59ced7b20', '2602103013', 'Barrio Rodríguez Peña', '26', 'Escalante'),
    ('05a93d0f-13b4-5ddc-9323-baf64cb90b18', '2602103014', 'Barrio Saavedra', '26', 'Escalante'),
    ('e8aaabc9-b9a6-5e59-8ffe-f7abb4cb3867', '2602103015', 'Barrio Sarmiento', '26', 'Escalante'),
    ('23832557-0f50-5792-8b58-746df21dff44', '2602103016', 'Barrio 25 de Mayo', '26', 'Escalante'),
    ('33649b78-f303-5c91-aed1-20dc22edbe88', '2602103017', 'Barrio Villa S.U.P.E.', '26', 'Escalante'),
    ('d71c31d5-8349-5a8e-b975-8f2e2d41410c', '2602103018', 'Comodoro Rivadavia', '26', 'Escalante'),
    ('d1a8bfd3-0678-5da1-9ded-788dfccfab49', '2602103019', 'Kilómetro 5 - Presidente Ortíz', '26', 'Escalante'),
    ('f84ffc5d-8509-5a8e-93a8-bc8029d89c62', '2602103020', 'Kilómetro 8 - Don Bosco', '26', 'Escalante'),
    ('2f0950e9-785d-52ee-89ae-7a147bcb3461', '2602103021', 'Kilómetro 11 - Cuarteles', '26', 'Escalante'),
    ('2a0ec2e1-4c23-56d0-9056-2ad60860f8d1', '2602103022', 'Kilómetro 3 - General Mosconi', '26', 'Escalante'),
    ('5d28d758-4ad1-5c76-a8da-d33116922a0a', '26021040', 'Diadema Argentina', '26', 'Escalante'),
    ('516eb84c-667d-56f4-8b69-fef8103140bd', '26021050', 'Rada Tilly', '26', 'Escalante'),
    ('e45c7497-f416-5ed0-9334-7643cc9b7c7d', '26028010', 'Camarones', '26', 'Florentino Ameghino'),
    ('a3464b7a-c7c5-5053-ae5c-1460f3872236', '26028020', 'Garayalde', '26', 'Florentino Ameghino'),
    ('bead7799-cb21-5f7d-a259-d3e87798fd79', '26035010', 'Aldea Escolar (Los Rápidos)', '26', 'Futaleufú'),
    ('d8339ee8-141b-51c6-982f-7f1854ae8ca2', '26035020', 'Corcovado', '26', 'Futaleufú'),
    ('a3a87375-83c8-5c80-b835-10eb74fcbb91', '26035030', 'Esquel', '26', 'Futaleufú'),
    ('daf09109-0f07-5f5e-91b8-5b2325f15331', '26035040', 'Lago Rosario', '26', 'Futaleufú'),
    ('a4536a66-a128-50c8-bbbd-ed6070e75360', '26035050', 'Los Cipreses', '26', 'Futaleufú'),
    ('c8134ec8-45f9-513c-ac03-1a54a181f206', '26035060', 'Trevelín', '26', 'Futaleufú'),
    ('a7805461-7cc2-5c0a-a36d-483eee18e05a', '26035070', 'Villa Futalaufquen', '26', 'Futaleufú'),
    ('f403e63e-e545-5d50-9eeb-62d81136a48d', '26042010', 'Dique Florentino Ameghino', '26', 'Gaiman'),
    ('120c67dc-c3ed-525c-a400-1fc1d62f2719', '26042020', 'Dolavon', '26', 'Gaiman'),
    ('9812c844-787c-5b3e-acdc-c4f228c1bdf0', '26042030', 'Gaiman', '26', 'Gaiman'),
    ('a824594d-91ac-571e-881d-7f66f17c3f18', '26042040', '28 de Julio', '26', 'Gaiman'),
    ('5e4a15e4-8dcc-5420-b2d4-0345fc956f53', '26049010', 'Blancuntre', '26', 'Gastre'),
    ('e0df6b7c-b586-51e4-b63f-ec70aae2975b', '26049020', 'El Escorial', '26', 'Gastre'),
    ('b21ddb9a-19e1-5a7e-aa31-867a9f67da6d', '26049030', 'Gastre', '26', 'Gastre'),
    ('cbe06e0c-bd9e-5ecd-982e-2ab6e27598f6', '26049040', 'Lagunita Salada', '26', 'Gastre'),
    ('d85888c8-6126-5180-94ad-2db42ea083af', '26049050', 'Yala Laubat', '26', 'Gastre'),
    ('5eba2a82-83c3-542e-afad-36efb4c519e7', '26056010', 'Aldea Epulef', '26', 'Languiñeo'),
    ('b1f7f992-46c0-5b83-85ba-b48389479c41', '26056020', 'Carrenleufú', '26', 'Languiñeo'),
    ('cc3aa5f1-8cae-5f41-a7cd-b875c9960ce1', '26056030', 'Colan Conhué', '26', 'Languiñeo'),
    ('96d1a3a6-6241-543b-9374-50b6eb92d93c', '26056040', 'Paso del Sapo', '26', 'Languiñeo'),
    ('279f56e6-82c6-5d32-8c95-a65a828f0625', '26056050', 'Tecka', '26', 'Languiñeo'),
    ('a3f3d3ef-59d8-5100-9bef-0a6e2daebc69', '26063010', 'El Mirasol', '26', 'Mártires'),
    ('549ae63e-4dc8-5ae5-8143-2ec6dfb2f80a', '26063020', 'Las Plumas', '26', 'Mártires'),
    ('bf79ca9e-d0e5-5e40-8b21-d254ecbfb9db', '26070010', 'Cerro Cóndor', '26', 'Paso de Indios'),
    ('cf867fd9-556f-54f9-9f00-a87ccaa09995', '26070020', 'Los Altares', '26', 'Paso de Indios'),
    ('68a1589b-8dfa-5250-95e0-100eac868c93', '26070030', 'Paso de Indios', '26', 'Paso de Indios'),
    ('336c07c5-326d-566c-bacb-d73316ba85d8', '26077010', 'Playa Magagna', '26', 'Rawson'),
    ('fdfdb19d-7b0c-5236-87bc-11c7c62083c1', '26077020', 'Playa Unión', '26', 'Rawson'),
    ('a0c07842-a58b-5aed-b89f-6532d376f0cf', '26077030', 'Rawson', '26', 'Rawson'),
    ('f666abcf-3d44-5544-8000-8556a7ff9fb6', '26077040', 'Trelew', '26', 'Rawson'),
    ('1ba77042-7686-567b-98cb-4d72db81b2e9', '26084010', 'Aldea Apeleg', '26', 'Río Senguer'),
    ('f17d44da-2676-531a-8572-9138903353b2', '26084020', 'Aldea Beleiro', '26', 'Río Senguer'),
    ('3a833c68-431a-5414-b72b-03184563b911', '26084030', 'Alto Río Senguer', '26', 'Río Senguer'),
    ('fd6a1ad7-41d2-5aa0-bcfb-293a2afb9e7a', '26084040', 'Doctor Ricardo Rojas', '26', 'Río Senguer'),
    ('5ad41dae-4c58-5797-bf93-a1999ac8605e', '26084050', 'Facundo', '26', 'Río Senguer'),
    ('c4a1166d-6190-5ce0-9aa2-1d5978b75190', '26084060', 'Lago Blanco', '26', 'Río Senguer'),
    ('f52f5203-3481-5ea8-8a25-d599b7b3571b', '26084070', 'Río Mayo', '26', 'Río Senguer'),
    ('a99d3c5f-8edf-534d-8988-21bd927c0d10', '26091010', 'Buen Pasto', '26', 'Sarmiento'),
    ('fdfea0ec-f6f7-5b0f-8807-74c4ec241a5d', '26091020', 'Sarmiento', '26', 'Sarmiento'),
    ('1aae847d-021c-5376-b8e8-cd44b986d43f', '26098010', 'Doctor Oscar Atilio Viglione (Frontera de Río Pico)', '26', 'Tehuelches'),
    ('8fb23d0a-910a-55d9-977b-1294ce020512', '26098020', 'Gobernador Costa', '26', 'Tehuelches'),
    ('fd56f91c-ba87-5435-8d37-0f4ee3b44428', '26098030', 'José de San Martín', '26', 'Tehuelches'),
    ('06aaa37c-d9ca-5fc7-a14e-a9a67224dc3d', '26098040', 'Río Pico', '26', 'Tehuelches'),
    ('d5954725-2960-5fd3-a138-2ac82410f4f8', '26105010', 'Gan Gan', '26', 'Telsen'),
    ('b076adee-9740-53eb-a46a-c58f94cfe056', '26105020', 'Telsen', '26', 'Telsen'),
    ('02c47c0e-4c97-59d2-87be-bcddb80282b5', '30008010', 'Arroyo Barú', '30', 'Colón'),
    ('31f2eb9c-2aa8-515c-8f69-f2367ed8eaef', '30008020', 'Colón', '30', 'Colón'),
    ('a05edc99-3212-558a-ad62-125cdec7386f', '30008030', 'Colonia Hugues', '30', 'Colón'),
    ('61eeddd4-e3b6-5b58-82ce-22589355e075', '30008040', 'Hambis', '30', 'Colón'),
    ('22d4c9a2-92f4-5fa2-9e4d-ce763bbb15fd', '30008050', 'Hocker', '30', 'Colón'),
    ('ef462c51-b462-53c8-ad0e-3c9560ad3e96', '30008060', 'La Clarita', '30', 'Colón'),
    ('a5b0c694-eb0d-56d1-8cc4-fae0d31d9eed', '30008070', 'Pueblo Cazes', '30', 'Colón'),
    ('1f1d5ba9-042a-5f60-a948-92d9ee67f9cf', '30008080', 'Pueblo Liebig''s', '30', 'Colón'),
    ('3e2837fb-70dc-5e8c-b9a9-bd183aaebe6e', '30008090', 'San José', '30', 'Colón'),
    ('e50ec1cd-52bd-58d5-8dfe-644d8b93662e', '3000809001', 'El Brillante', '30', 'Colón'),
    ('4090e47b-7d22-5248-a5af-3a595257e6ff', '3000809002', 'El Colorado', '30', 'Colón'),
    ('030f72cc-6720-51b1-8534-713927d01574', '3000809003', 'San José', '30', 'Colón'),
    ('7bbb2f82-7725-5547-a8ab-59c8240bc9e4', '30008100', 'Ubajay', '30', 'Colón'),
    ('28b48137-7c4a-537e-94e5-fcf4c34ff66f', '30008110', 'Villa Elisa', '30', 'Colón'),
    ('f47e3cca-c43b-5b21-97b3-14b8b6e433bd', '30015010', 'Calabacilla', '30', 'Concordia'),
    ('59814def-2c76-5748-9e3b-01ca26e46411', '30015020', 'Clodomiro Ledesma', '30', 'Concordia'),
    ('9416f742-569f-571e-9158-23d8f5468f35', '30015030', 'Colonia Ayuí', '30', 'Concordia'),
    ('f97f3464-aab2-54fc-9643-afd114c73eb1', '30015040', 'Colonia General Roca', '30', 'Concordia'),
    ('245c2505-1b33-5c2c-91ab-aaf4c10f8e6a', '30015060', 'Concordia', '30', 'Concordia'),
    ('2b241340-ada2-5144-853a-3a8b4f734008', '3001506001', 'Benito Legerén', '30', 'Concordia'),
    ('9f5e0de2-f852-52ab-b9ca-7a53a26817c0', '3001506002', 'Villa Adela', '30', 'Concordia'),
    ('b8e48e2e-820b-5c42-8396-6ca909092d1d', '3001506003', 'Las Tejas', '30', 'Concordia'),
    ('32a76316-6f12-57df-9e71-508aaeedf4dd', '3001506005', 'Concordia', '30', 'Concordia'),
    ('320bab4b-dd39-54db-9b9f-f67d1e87f4f2', '3001506007', 'Villa Zorraquín', '30', 'Concordia'),
    ('fd46b87d-3222-5bfb-a4a2-366969b7646f', '30015080', 'Estación Yeruá', '30', 'Concordia'),
    ('b19a6deb-6cc0-5b3c-ab2e-3d42e9648b68', '30015083', 'Estación Yuquerí', '30', 'Concordia'),
    ('32428758-cb49-5ca7-82dc-b6d1aefcb66e', '30015087', 'Estancia Grande', '30', 'Concordia'),
    ('99aab9a4-447b-51d7-b903-026725eb28bb', '30015090', 'La Criolla', '30', 'Concordia'),
    ('69d1b7f0-9b76-56cb-b6e8-56b4a5dfb5d5', '30015100', 'Los Charrúas', '30', 'Concordia'),
    ('38f81ecb-0dc5-5fb9-9d02-cc5d8490f451', '30015110', 'Nueva Escocia', '30', 'Concordia'),
    ('a7c53e84-82a2-51bc-ab9c-792bf865f16e', '30015120', 'Osvaldo Magnasco', '30', 'Concordia'),
    ('fd10bef2-59ea-5c64-a508-4bbc73a47617', '30015130', 'Pedernal', '30', 'Concordia'),
    ('c18a8415-3f92-5a4c-aaaa-29675e62d542', '30015140', 'Puerto Yeruá', '30', 'Concordia'),
    ('da027e50-366a-5654-9c32-96826e5c4697', '30021010', 'Aldea Brasilera', '30', 'Diamante'),
    ('71d7b409-fbd1-52bb-8f23-139b8766e210', '30021015', 'Aldea Grapschental', '30', 'Diamante'),
    ('9c714f17-0115-57f5-b9b7-efc83f78ebe6', '30021020', 'Aldea Protestante', '30', 'Diamante'),
    ('ecde9ec4-8d43-5719-b19f-eb4bcb1b65dd', '30021030', 'Aldea Salto', '30', 'Diamante'),
    ('969186cc-83b6-5083-882a-56045a3ef29b', '30021040', 'Aldea San Francisco', '30', 'Diamante'),
    ('39d5bd9e-e6aa-5b19-a6c4-49aab75a18f3', '30021050', 'Aldea Spatzenkutter', '30', 'Diamante'),
    ('96a81108-a384-5110-b1a2-33fe8544c9c6', '30021060', 'Aldea Valle María', '30', 'Diamante'),
    ('ad6a4249-c438-5a30-9e42-1820754cb287', '30021070', 'Colonia Ensayo', '30', 'Diamante'),
    ('c0d84a9f-97ce-52c5-8562-5f8518061b1c', '30021080', 'Diamante', '30', 'Diamante'),
    ('c85f8d6c-1a2e-5ccd-8b5c-f81e74e9f542', '3002108001', 'Diamante', '30', 'Diamante'),
    ('1c304cdf-c55a-5d1b-a810-a439d30fb93a', '3002108002', 'Strobel', '30', 'Diamante'),
    ('3cb8c187-5c6c-5c3f-912d-87f0aaf408e3', '30021090', 'Estación Camps', '30', 'Diamante'),
    ('18fb1810-3569-5130-9afb-9960851f3d5c', '30021100', 'General Alvear', '30', 'Diamante'),
    ('638d46f6-87cb-59ce-bded-2ed2f6caa6a6', '30021110', 'General Racedo (El Carmen)', '30', 'Diamante'),
    ('b9aaa171-1194-56ff-8759-399dfe9645fc', '30021120', 'General Ramírez', '30', 'Diamante'),
    ('ac909a4a-f41a-5d43-b9fa-89300e7cefcc', '30021123', 'La Juanita', '30', 'Diamante'),
    ('e5af2bf0-2b05-5512-bfdb-280ef0c27a1c', '30021127', 'Las Jaulas', '30', 'Diamante'),
    ('41971513-5d74-5475-a6c7-b1e50d9e3c88', '30021130', 'Paraje La Virgen', '30', 'Diamante'),
    ('71c7ba76-c845-5115-9d6b-5d919fb3a581', '30021140', 'Puerto Las Cuevas', '30', 'Diamante'),
    ('ccfae352-d797-5a8a-941b-fe821cf6c016', '30021150', 'Villa Libertador San Martín', '30', 'Diamante'),
    ('a00d8d9c-10c5-5615-a8f2-83e053e7fcfe', '3002115001', 'Estación Puiggari', '30', 'Diamante'),
    ('bc24a941-3de2-53c2-8942-86802cc32d84', '3002115002', 'Villa Libertador San Martín', '30', 'Diamante'),
    ('a00573ec-c50f-5916-8c65-324e9fee2fcf', '30028010', 'Chajarí', '30', 'Federación'),
    ('415a74be-b0b5-5eb3-85c4-eec98505b1f1', '30028020', 'Colonia Alemana', '30', 'Federación'),
    ('ea0ec476-f712-5ccb-a4d2-8f29df061ace', '30028040', 'Colonia La Argentina', '30', 'Federación'),
    ('99cf3e88-e0e3-5b4a-9a85-f6e813d364ce', '30028070', 'Federación', '30', 'Federación'),
    ('e569aeb9-5558-53f8-8a07-b5065a214c5a', '30028080', 'Los Conquistadores', '30', 'Federación'),
    ('1c409996-f8d0-5d09-8076-c4715acf870e', '30028090', 'San Jaime de la Frontera', '30', 'Federación'),
    ('b4dae720-93cc-5a53-ad96-46511e5d230e', '30028100', 'San Pedro', '30', 'Federación'),
    ('c20159b8-c9de-5a08-b7a2-393d8f9cd09e', '30028105', 'San Ramón', '30', 'Federación'),
    ('2711161e-24fa-5534-a4b4-a18687976abc', '30028110', 'Santa Ana', '30', 'Federación'),
    ('9b359cee-8be6-5a05-a1ac-ede1ec13af7b', '30028120', 'Villa del Rosario', '30', 'Federación'),
    ('b51b5e47-72d1-5260-92ea-797d90d45be7', '30035010', 'Conscripto Bernardi', '30', 'Federal'),
    ('91a033f9-3a0e-5910-98ca-84fe2c94604c', '30035020', 'Aldea San Isidro (El Cimarrón)', '30', 'Federal'),
    ('c2a41d13-dda0-5065-a79c-13ae7994ffc2', '30035030', 'Federal', '30', 'Federal'),
    ('ec3115b5-b0f0-58bf-aeac-6eeeea0e852a', '30035040', 'Nueva Vizcaya', '30', 'Federal'),
    ('c132efe6-cbcf-5099-a24a-41e6f38d20c5', '30035050', 'Sauce de Luna', '30', 'Federal'),
    ('26eaff3d-aa61-5dfc-a6ff-a47d5ed13366', '30042010', 'San José de Feliciano', '30', 'Feliciano'),
    ('6089bffa-d0b3-5f5e-b69b-3ca205e0f09b', '30042020', 'San Víctor', '30', 'Feliciano'),
    ('41f346ea-9d51-5126-b85c-a8fd258823d5', '30049010', 'Aldea Asunción', '30', 'Gualeguay'),
    ('ee4379e5-e7e8-59ac-9256-d69c6aa39b43', '30049020', 'Estación Lazo', '30', 'Gualeguay'),
    ('291633c7-533f-5e10-bd41-976f80297a6f', '30049030', 'General Galarza', '30', 'Gualeguay'),
    ('b1b98690-ec61-5cd6-a599-2611c9ec417a', '30049040', 'Gualeguay', '30', 'Gualeguay'),
    ('ede49e0e-2471-55b0-b438-672d63ada7fd', '30049050', 'Puerto Ruiz', '30', 'Gualeguay'),
    ('9c10cc7d-5aa2-53bb-8663-5027918bd178', '30056010', 'Aldea San Antonio', '30', 'Gualeguaychú'),
    ('0b769c5e-603d-535c-9c1a-59ca49ac2777', '30056020', 'Aldea San Juan', '30', 'Gualeguaychú'),
    ('958d3b68-0b90-5aa3-a220-893c6ad8d5e8', '30056030', 'Enrique Carbó', '30', 'Gualeguaychú'),
    ('02c7b18d-d539-5f59-bed9-9f0cda8e7f24', '30056035', 'Estación Escriña', '30', 'Gualeguaychú'),
    ('ceabd05c-7f6f-5714-b71c-53d9249d7430', '30056040', 'Faustino M. Parera', '30', 'Gualeguaychú'),
    ('93669255-ffb5-5ddb-b476-3a1c978abe97', '30056050', 'General Almada', '30', 'Gualeguaychú'),
    ('39d00515-0225-5c9c-abc4-121eb14cd622', '30056060', 'Gilbert', '30', 'Gualeguaychú'),
    ('7057d23d-fc9d-52e2-8a90-5bc8e3419b07', '30056070', 'Gualeguaychú', '30', 'Gualeguaychú'),
    ('eccc6ea1-c09b-52ef-971c-f798ff37c7db', '30056080', 'Irazusta', '30', 'Gualeguaychú'),
    ('775e19e4-b36d-5c1d-9504-560822196af2', '30056090', 'Larroque', '30', 'Gualeguaychú'),
    ('f623ea46-1ae9-58fa-b672-5d8fabb1f787', '30056095', 'Pastor Britos', '30', 'Gualeguaychú'),
    ('400991f7-33b4-577a-89d9-1d11239437e0', '30056100', 'Pueblo General Belgrano', '30', 'Gualeguaychú'),
    ('0c308d94-2d8d-519a-860c-2d0af3bef95f', '30056110', 'Urdinarrain', '30', 'Gualeguaychú'),
    ('46a81d72-490e-5984-9fae-56549fa986e2', '30063020', 'Ceibas', '30', 'Islas del Ibicuy'),
    ('951d1061-0626-58bc-9252-20d707657078', '30063030', 'Ibicuy', '30', 'Islas del Ibicuy'),
    ('17a45435-5e2c-5627-ad14-d26495ea6783', '30063040', 'Médanos', '30', 'Islas del Ibicuy'),
    ('eeae391e-0fcf-5479-a7ce-a735c1010259', '30063060', 'Villa Paranacito', '30', 'Islas del Ibicuy'),
    ('db260037-c1f7-563d-a390-a2e27ab68892', '30070005', 'Alcaraz', '30', 'La Paz'),
    ('6b0f8d81-f29a-55e4-8680-802681001927', '30070010', 'Bovril', '30', 'La Paz'),
    ('dc15c036-0219-55ed-94e2-aa7722137eed', '30070020', 'Colonia Avigdor', '30', 'La Paz'),
    ('4874de68-85d6-5ad4-8439-0ea3b7d6e663', '30070030', 'El Solar', '30', 'La Paz'),
    ('7122bab1-e582-57d3-af3b-fa51a5b4d41e', '30070040', 'La Paz', '30', 'La Paz'),
    ('a1c60a4c-0b2c-5901-89cc-1dca7b109561', '30070050', 'Piedras Blancas', '30', 'La Paz'),
    ('6ed1fa92-cf02-5bfe-9c51-ef5eac537c4b', '30070070', 'San Gustavo', '30', 'La Paz'),
    ('83db3c06-ad34-52f1-aeb6-f456897c532d', '30070080', 'Santa Elena', '30', 'La Paz'),
    ('e2a91c79-614d-5f7b-a784-db63b4335d5f', '30070090', 'Sir Leonard', '30', 'La Paz'),
    ('24fe8e5d-e090-53b3-9627-a71af2145eec', '30077010', 'Aranguren', '30', 'Nogoyá'),
    ('5badd9f1-64d7-520b-a609-410fd05d6657', '30077020', 'Betbeder', '30', 'Nogoyá'),
    ('6a6c65f9-842f-5319-82a2-c50bbc4af87d', '30077030', 'Don Cristóbal', '30', 'Nogoyá'),
    ('1aad77ba-604c-5126-96b1-ca0bd039f069', '30077040', 'Febré', '30', 'Nogoyá'),
    ('3505a09c-57dd-58fa-9603-1cccc093c99d', '30077050', 'Hernández', '30', 'Nogoyá'),
    ('33aa9378-09d5-59df-a0c2-dcd154763e5b', '30077060', 'Lucas González', '30', 'Nogoyá'),
    ('ea396ba7-9096-5425-84a1-112e6e23df6e', '30077070', 'Nogoyá', '30', 'Nogoyá'),
    ('18fc2e6b-afab-50c2-9183-3e9d658678c1', '30077080', 'XX de Setiembre', '30', 'Nogoyá'),
    ('f11cb889-739f-53e6-97d3-44b4dafa6efa', '30084010', 'Aldea María Luisa', '30', 'Paraná'),
    ('f2b8acf5-5e57-5b48-b5a5-4d492610554d', '30084015', 'Aldea San Juan', '30', 'Paraná'),
    ('1b0df15c-e553-5806-a954-e9845210070f', '30084020', 'Aldea San Rafael', '30', 'Paraná')
ON CONFLICT (indec_id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('8a628df3-714b-5f0a-aca1-7011e2b0af61', '30084030', 'Aldea Santa María', '30', 'Paraná'),
    ('51cfd023-018a-5d8f-813a-878907ee79f8', '30084040', 'Aldea Santa Rosa', '30', 'Paraná'),
    ('3d86da45-3852-56ff-b6b9-464bc1e51a39', '30084050', 'Cerrito', '30', 'Paraná'),
    ('6c0e1632-4eb2-52e8-83c2-571ee986d3e6', '3008405001', 'Cerrito', '30', 'Paraná'),
    ('0231cc78-8c78-53a2-99c6-fbb85bdbee8b', '3008405002', 'Pueblo Moreno', '30', 'Paraná'),
    ('a9fcc5e0-903e-5dc8-b5f5-a34a347dda6f', '30084060', 'Colonia Avellaneda', '30', 'Paraná'),
    ('65d4edd7-8a09-55c9-9a37-57127de80755', '30084065', 'Colonia Crespo', '30', 'Paraná'),
    ('df9ad4ef-ed03-5c1a-a913-907b2399ce9c', '30084070', 'Crespo', '30', 'Paraná'),
    ('71e28ddd-4b24-5c8d-a45b-7945bc59b2b2', '30084080', 'El Palenque', '30', 'Paraná'),
    ('6e06b47b-781f-56fa-b471-bcced1b15f8a', '30084090', 'El Pingo', '30', 'Paraná'),
    ('05a3a5e8-f119-51fd-8da7-b9276dc51545', '30084095', 'El Ramblón', '30', 'Paraná'),
    ('92bc9539-3a4a-5cc8-880d-486b1ef40316', '30084100', 'Hasenkamp', '30', 'Paraná'),
    ('21a1c32d-38f7-5f22-aafb-5fb361bb8486', '30084110', 'Hernandarias', '30', 'Paraná'),
    ('03749c07-37f6-5cc1-bcf0-476d04d73c94', '30084120', 'La Picada', '30', 'Paraná'),
    ('0054bf04-12b3-52f2-943f-dc865f73579d', '30084130', 'Las Tunas', '30', 'Paraná'),
    ('be9ee533-1d19-569f-b3b0-d81c623710ba', '30084140', 'María Grande', '30', 'Paraná'),
    ('1ec0cdec-1dfd-5387-8ca0-1418d0ec31df', '30084150', 'Oro Verde', '30', 'Paraná'),
    ('2974fca6-e006-54b6-964d-3f1094f89048', '30084160', 'Paraná', '30', 'Paraná'),
    ('12eab529-e204-51fc-adc0-368ef8cddf0e', '30084170', 'Pueblo Bellocq (Las Garzas)', '30', 'Paraná'),
    ('89a701b1-5e43-5983-941e-b341813549a7', '30084180', 'Pueblo Brugo', '30', 'Paraná'),
    ('f4601994-0d29-50a2-af35-5314ce9cf0da', '30084190', 'Pueblo General San Martín', '30', 'Paraná'),
    ('f1073c1b-25dd-56df-a8e2-057eed515fa5', '30084200', 'San Benito', '30', 'Paraná'),
    ('ed45da4e-76a1-5fe8-ab09-78688ca349b1', '30084210', 'Sauce Montrull', '30', 'Paraná'),
    ('e5eba06c-abe1-5eba-a626-36b439b11481', '30084220', 'Sauce Pinto', '30', 'Paraná'),
    ('dbf797df-a61a-5ba8-8220-00cb1fb10f82', '30084230', 'Seguí', '30', 'Paraná'),
    ('9d367054-4878-5b62-9628-97ca91e70e28', '30084240', 'Sosa', '30', 'Paraná'),
    ('ffe79b0e-0501-557d-a8d3-0d02c48b6392', '30084250', 'Tabossi', '30', 'Paraná'),
    ('8a2ce9b3-9c2b-52d7-ad8d-b9de6ec6bd99', '30084260', 'Tezanos Pinto', '30', 'Paraná'),
    ('b50a0185-4d07-51f3-aeb8-c8426b58413b', '30084270', 'Viale', '30', 'Paraná'),
    ('de5db2a4-cb0e-5989-a984-6b7164333983', '30084280', 'Villa Fontana', '30', 'Paraná'),
    ('5058e870-0be3-5d9a-a70e-7a43dc0951db', '30084290', 'Villa Gdor. Luis F. Etchevehere', '30', 'Paraná'),
    ('ff50f53c-e80c-52ad-a209-e6565a44f711', '30084300', 'Villa Urquiza', '30', 'Paraná'),
    ('b8c6d6f7-f040-5d5d-81e9-5eeb7dd19610', '30088010', 'General Campos', '30', 'San Salvador'),
    ('26f30c3f-65a4-5802-b26a-866c9a31a048', '30088020', 'San Salvador', '30', 'San Salvador'),
    ('e681f9ee-9037-51d0-a4d6-ebdc50843d10', '30091010', 'Altamirano Sur', '30', 'Tala'),
    ('9bc89dc0-b4ac-51ab-816d-38310d42df84', '30091020', 'Durazno', '30', 'Tala'),
    ('e7d3b2cb-bb6b-57f0-a3bc-a7a0d8ddb647', '30091030', 'Estación Arroyo Clé', '30', 'Tala'),
    ('a32d4c8a-aafe-5479-aabe-7dac13f25111', '30091040', 'Gobernador Echagüe', '30', 'Tala'),
    ('1148b218-8b4d-5d19-8579-8d03f5afe7d3', '30091050', 'Gobernador Mansilla', '30', 'Tala'),
    ('2228f294-bdd9-5e7c-8e73-ae7e7540efaa', '30091060', 'Gobernador Solá', '30', 'Tala'),
    ('59e5b8fe-0673-55c7-868d-793497a866de', '30091070', 'Guardamonte', '30', 'Tala'),
    ('9c30ee9e-18ef-5f1f-af6e-0e7206a0f6cb', '30091080', 'Las Guachas', '30', 'Tala'),
    ('efdd93a0-ca77-5c38-9af6-6d9c54dac5a2', '30091090', 'Maciá', '30', 'Tala'),
    ('6b085aee-7055-5be4-898e-3a04dd73200d', '30091100', 'Rosario del Tala', '30', 'Tala'),
    ('ed246714-8199-52c7-9feb-4718569b7fe6', '30098010', 'Basavilbaso', '30', 'Uruguay'),
    ('0b7163c0-75aa-5d79-9b48-eabfd4dbbec8', '30098020', 'Caseros', '30', 'Uruguay'),
    ('388c6204-02fd-53af-9e88-0ddb631faa38', '30098030', 'Colonia Elía', '30', 'Uruguay'),
    ('a8fb4ce4-3949-5b32-aae6-de02352d511a', '30098040', 'Concepción del Uruguay', '30', 'Uruguay'),
    ('f457cba2-f4e4-5631-9b2d-dc10e37e807f', '30098060', 'Herrera', '30', 'Uruguay'),
    ('6efd7324-cd76-54e5-a9cc-f0c1f4fa4b69', '30098070', 'Las Moscas', '30', 'Uruguay'),
    ('b0cb184e-9a57-571e-834b-98982dea8cdf', '30098080', 'Líbaros', '30', 'Uruguay'),
    ('2a3c6177-7249-5398-9883-888683ea6c11', '30098090', '1º de Mayo', '30', 'Uruguay'),
    ('d919c3fc-2b6e-5cf7-b31d-ae657860f3e8', '30098100', 'Pronunciamiento', '30', 'Uruguay'),
    ('6bfd41d7-eec3-51c2-a9e3-fe147a5856f6', '30098110', 'Rocamora', '30', 'Uruguay'),
    ('f85ad3c7-3cfc-5973-a59f-c57d50c11339', '30098120', 'Santa Anita', '30', 'Uruguay'),
    ('34663ae8-f78f-5ab0-ba54-5871ef65e22c', '30098130', 'Villa Mantero', '30', 'Uruguay'),
    ('4afbfa64-48d8-520e-af12-8a82575793e7', '30098140', 'Villa San Justo', '30', 'Uruguay'),
    ('e94178f2-b6ec-58e0-b1b6-63fa9b5a04b7', '30098150', 'Villa San Marcial (Est. Gobernador Urquiza)', '30', 'Uruguay'),
    ('f08a0684-995a-5ee6-8a5b-dcac5896782e', '30105010', 'Antelo', '30', 'Victoria'),
    ('a8c1d0d9-a28a-534f-a5b3-886d4fa1160c', '30105040', 'Molino Doll', '30', 'Victoria'),
    ('739b8002-5780-5126-9944-9768595c13a7', '30105060', 'Victoria', '30', 'Victoria'),
    ('99e2e088-be19-52e1-9e2f-e92d660af34b', '30113010', 'Estación Raíces', '30', 'Villaguay'),
    ('80ecd007-b9da-57c5-a1b6-87fe25d06fbf', '30113020', 'Ingeniero Miguel Sajaroff', '30', 'Villaguay'),
    ('4a657066-f999-5d81-8dde-89d07f810d20', '30113030', 'Jubileo', '30', 'Villaguay'),
    ('2aa43a30-1497-5c41-b31b-ea8dcc4cb3e6', '30113050', 'Paso de la Laguna', '30', 'Villaguay'),
    ('bf80f2a9-30e2-50e9-8c05-47cea4cc9a95', '30113060', 'Villa Clara', '30', 'Villaguay'),
    ('31ebeeb4-164e-519b-a609-06a511b28075', '30113070', 'Villa Domínguez', '30', 'Villaguay'),
    ('dfd6d3bc-06db-5ea4-9a5a-7582cc6d5c8d', '30113080', 'Villaguay', '30', 'Villaguay'),
    ('9317abd9-045d-5574-845c-bfc83c1e8c15', '34007003', 'Fortín Soledad', '34', 'Bermejo'),
    ('32339984-2763-5874-b394-d37ac7ee87d2', '34007005', 'Guadalcazar', '34', 'Bermejo'),
    ('ec59b6ab-91e0-502c-8c01-97fa449191a7', '34007007', 'La Rinconada', '34', 'Bermejo'),
    ('93c057b0-794b-519d-9b42-b8f0437dab08', '34007010', 'Laguna Yema', '34', 'Bermejo'),
    ('5a2fc515-f409-5a2f-a6e5-43c82d311852', '34007015', 'Lamadrid', '34', 'Bermejo'),
    ('e924fc4f-6e9f-5e67-a850-09616f7d2e51', '34007020', 'Los Chiriguanos', '34', 'Bermejo'),
    ('ddace756-8946-5ff6-8aa3-5f09a44dfc5c', '34007030', 'Pozo de Maza', '34', 'Bermejo'),
    ('30c2918c-b115-5384-9773-5570bdece5a4', '34007040', 'Pozo del Mortero', '34', 'Bermejo'),
    ('1cefbfa5-7b5e-5b79-9915-9c7ea1e0e8b0', '34007050', 'Vaca Perdida', '34', 'Bermejo'),
    ('4ee76570-1741-531f-a0d4-f91d60a19b30', '34014010', 'Colonia Pastoril', '34', 'Formosa'),
    ('1bc33d31-9c6f-584a-ac4e-b121808ec3f0', '34014020', 'Formosa', '34', 'Formosa'),
    ('25204431-60ed-5976-ad87-7b01b43c52d1', '34014030', 'Gran Guardia', '34', 'Formosa'),
    ('96636486-ccb9-513b-a89e-f2cbffa33e8d', '34014040', 'Mariano Boedo', '34', 'Formosa'),
    ('2be524b4-4b39-5740-aeba-4bbf2e1d2cd1', '34014050', 'Mojón de Fierro', '34', 'Formosa'),
    ('e4e82130-5e52-57d5-9670-5b7c5e7ceb31', '34014060', 'San Hilario', '34', 'Formosa'),
    ('0e89afe2-c0cc-5759-8bcd-e0909dcc45eb', '34021010', 'Banco Payaguá', '34', 'Laishi'),
    ('d2338641-ce71-54b0-ae82-b1de6cbba79e', '34021020', 'General Lucio V. Mansilla', '34', 'Laishi'),
    ('525a4cba-f992-5c90-80b6-33629a31bb36', '34021030', 'Herradura', '34', 'Laishi'),
    ('6c697677-0f7c-558c-996b-3d9da5e9a68d', '34021040', 'San Francisco de Laishi', '34', 'Laishi'),
    ('4f7456c1-f9a3-575b-add1-44776c1db43f', '34021050', 'Tatané', '34', 'Laishi'),
    ('b75603fb-08ef-5a1f-a03e-5264d0422e96', '34021060', 'Villa Escolar', '34', 'Laishi'),
    ('1c3cea06-3583-5536-9354-efb5a8cb4692', '34028010', 'Ingeniero Guillermo N. Juárez', '34', 'Matacos'),
    ('3b106591-2d67-53c5-88d7-a92581d2198f', '34035010', 'Bartolomé de las Casas', '34', 'Patiño'),
    ('823e2aca-a3b4-5432-be0e-5db94434abf2', '3403501001', 'Bartolomé de las Casas', '34', 'Patiño'),
    ('ea65bfdd-1c19-511b-8096-3f0634bd3b53', '3403501002', 'Comunidad Aborigen Bartolomé de las Casas', '34', 'Patiño'),
    ('1e17b161-b1d1-5880-8514-1bf77133cde6', '34035020', 'Colonia Sarmiento', '34', 'Patiño'),
    ('1fdb37a0-40fc-50aa-88ee-b4b37dfdc1ef', '34035030', 'Comandante Fontana', '34', 'Patiño'),
    ('100bd547-91ed-51fd-8886-4046a4ce067f', '34035040', 'El Recreo', '34', 'Patiño'),
    ('b56ecff4-1b93-5303-8b1d-8598fb310e43', '34035050', 'Estanislao del Campo', '34', 'Patiño'),
    ('1a6d6dbd-8e0b-51ac-9507-e37edcb52c3c', '34035060', 'Fortín Cabo 1º Lugones', '34', 'Patiño'),
    ('f963830b-cf65-599c-b7c7-c64f3ff563ad', '34035070', 'Fortín Sargento 1º Leyes', '34', 'Patiño'),
    ('d46701dd-0020-5053-b16e-e01709648d10', '34035080', 'Ibarreta', '34', 'Patiño'),
    ('666058d5-269d-5732-8073-aafc86b8daad', '34035090', 'Juan G. Bazán', '34', 'Patiño'),
    ('fe171e28-4d72-57a7-be35-1750d44dd725', '34035100', 'Las Lomitas', '34', 'Patiño'),
    ('74ae4c7f-146e-500b-ab75-90900980131d', '34035110', 'Posta Cambio Zalazar', '34', 'Patiño'),
    ('0b1bb216-c2f0-5ce9-9927-64befa054b56', '34035120', 'Pozo del Tigre', '34', 'Patiño'),
    ('0f2932b5-b136-5354-bc19-0c139932eca8', '34035130', 'San Martín I', '34', 'Patiño'),
    ('4f02f2da-3da4-588b-9ce3-dcc66db6610f', '34035140', 'San Martín II', '34', 'Patiño'),
    ('94e1298b-f6dc-5653-ac36-64fdc184d6c3', '34035150', 'Subteniente Perín', '34', 'Patiño'),
    ('5a9363f3-21b5-56dd-8ed7-0dd4bb92389f', '34035160', 'Villa General Güemes', '34', 'Patiño'),
    ('36a55372-52e4-50e1-ac5a-3d4f8841ed76', '34035170', 'Villa General Manuel Belgrano', '34', 'Patiño'),
    ('3822986a-6063-537a-8698-aa18c9102033', '34042010', 'Buena Vista', '34', 'Pilagás'),
    ('a81fb62f-fb0f-5b68-b7ee-6ee05eafdc2f', '34042020', 'El Espinillo', '34', 'Pilagás'),
    ('d0d55269-d8eb-5e48-8687-7285fd54b473', '34042030', 'Laguna Gallo', '34', 'Pilagás'),
    ('27457844-91b1-5ab7-8fef-90e9a1466321', '34042040', 'Misión Tacaaglé', '34', 'Pilagás'),
    ('c8c10e78-842b-527b-9b7b-790bb3ea5175', '34042050', 'Portón Negro', '34', 'Pilagás'),
    ('476c18ef-11c6-5e2c-bbe9-8c7c7c587ff9', '34042060', 'Tres Lagunas', '34', 'Pilagás'),
    ('b66a5441-cc23-58a6-9582-9437a9438a7e', '34049010', 'Clorinda', '34', 'Pilcomayo'),
    ('e086d1f4-e91f-5ce0-91e4-cdeb55f6cd3d', '34049020', 'Laguna Blanca', '34', 'Pilcomayo'),
    ('b04f9f4c-9b6d-5d5e-bb7e-2fb8bea7d411', '34049030', 'Laguna Naick-Neck', '34', 'Pilcomayo'),
    ('e9a11410-9c2a-5d62-923a-7b6eecf6ce68', '34049040', 'Palma Sola', '34', 'Pilcomayo'),
    ('5add8941-3243-5a1e-bc62-9a3126492dd1', '34049050', 'Puerto Pilcomayo', '34', 'Pilcomayo'),
    ('9931f290-9aa2-5bdc-bfca-fd9f22657888', '34049060', 'Riacho He-He', '34', 'Pilcomayo'),
    ('b9e05802-d45f-56e7-b5ec-cae72d7f930c', '34049070', 'Riacho Negro', '34', 'Pilcomayo'),
    ('09dcd7e1-2a73-5c1c-9a1d-17361232084b', '34049080', 'Siete Palmas', '34', 'Pilcomayo'),
    ('15aba0b1-81ee-5ba5-b411-5ae0a61c65a0', '34056010', 'Colonia Campo Villafañe', '34', 'Pirané'),
    ('7b442234-fed6-5d4e-a38e-9ef299e25627', '34056020', 'El Colorado', '34', 'Pirané'),
    ('9e35be6a-fb33-5053-9253-7bab37841f48', '34056030', 'Palo Santo', '34', 'Pirané'),
    ('e5604374-8772-5181-adb1-f52548ab6ecd', '34056040', 'Pirané', '34', 'Pirané'),
    ('0fe5da87-7fa8-5cbd-9c35-5391a5f2b50e', '34056050', 'Villa Kilómetro 213', '34', 'Pirané'),
    ('b21037ff-7f92-5c59-917c-f0231d7255e2', '34063010', 'El Potrillo', '34', 'Ramón Lista'),
    ('75387638-ab52-57fa-9a93-be8c765b33b4', '34063020', 'General Mosconi', '34', 'Ramón Lista'),
    ('ed225e27-f01a-5571-bb32-2d822b9b7c78', '34063030', 'El Quebracho', '34', 'Ramón Lista'),
    ('8c3cf9a9-8902-523a-b2a8-21a6615d9e1d', '38007020', 'Abra Pampa', '38', 'Cochinoca'),
    ('09137209-aec1-5ee2-8436-59092a7205e9', '38007030', 'Abralaite', '38', 'Cochinoca'),
    ('73316280-46e1-598a-8d0c-35d5d8408455', '38007035', 'Agua de Castilla', '38', 'Cochinoca'),
    ('e483021b-901e-5f21-9e92-5b4fdc063759', '38007040', 'Casabindo', '38', 'Cochinoca'),
    ('6f65ddcc-0dda-574f-9eaa-89d93c7d2b30', '38007050', 'Cochinoca', '38', 'Cochinoca'),
    ('2cf257e4-258f-518d-872f-3d1bdfc86759', '38007055', 'La Redonda', '38', 'Cochinoca'),
    ('1fbb77c5-f309-5ffa-b414-6cdf55c65463', '38007060', 'Puesto del Marquéz', '38', 'Cochinoca'),
    ('44888e04-ea24-50e0-8952-c5016c4e0211', '38007063', 'Quebraleña', '38', 'Cochinoca'),
    ('e63381f9-f1a8-56f6-9f93-a0f92232aed9', '38007067', 'Quera', '38', 'Cochinoca'),
    ('9a44b24b-2d1a-5b50-89ef-7051e8cc29ee', '38007070', 'Rinconadillas', '38', 'Cochinoca'),
    ('97613d21-6e0c-5a81-9cef-af7719b355e2', '38007080', 'San Francisco de Alfarcito', '38', 'Cochinoca'),
    ('417bb2c4-eb9e-589b-b361-52d67b7dc9ba', '38007085', 'Santa Ana de la Puna', '38', 'Cochinoca'),
    ('1acd2616-5ca7-5fde-b0e0-0cb9d1696806', '38007090', 'Santuario de Tres Pozos', '38', 'Cochinoca'),
    ('9e0cd3e9-eb7c-58ad-a6da-f6465877111a', '38007095', 'Tambillos', '38', 'Cochinoca'),
    ('cb3b1643-0c9b-5ebc-bbba-b410eb749406', '38007100', 'Tusaquillas', '38', 'Cochinoca'),
    ('a36b1bca-5f92-5a4f-b1bf-bd8a7aadd57a', '38014010', 'Aguas Calientes', '38', 'El Carmen'),
    ('44340bf3-3192-5059-9dc1-015fd1949a7d', '3801401001', 'Aguas Calientes', '38', 'El Carmen'),
    ('02cdf9ec-f838-5070-b155-746cc6fe2c28', '3801401002', 'Fleming', '38', 'El Carmen'),
    ('abae0b2d-3e8d-5b36-8ea8-120cfac16968', '3801401003', 'Pila Pardo', '38', 'El Carmen'),
    ('8e3d33c2-4047-5795-9823-dccfb1fa426d', '38014020', 'Barrio El Milagro', '38', 'El Carmen'),
    ('c2120e70-b3bf-5d60-ada9-862b6428abbd', '38014030', 'Barrio La Unión', '38', 'El Carmen'),
    ('c432a832-7387-5326-ad90-10d6d34f5169', '38014040', 'El Carmen', '38', 'El Carmen'),
    ('eb373726-2478-5d53-acd2-9a9009e6113c', '38014050', 'Los Lapachos', '38', 'El Carmen'),
    ('f19103fc-06ae-5d55-b34b-e8356750d7f0', '38014060', 'Manantiales', '38', 'El Carmen'),
    ('67977c85-b1e1-5a3d-bf7f-bcf960ee017d', '38014070', 'Monterrico', '38', 'El Carmen'),
    ('1f966653-56a0-5b94-8d22-933711497ae3', '38014080', 'Pampa Blanca', '38', 'El Carmen'),
    ('86361b3b-6754-505e-9103-100c9213e25b', '38014090', 'Perico', '38', 'El Carmen'),
    ('25a0537a-b83f-55fa-bf55-995eb55c06f6', '38014100', 'Puesto Viejo', '38', 'El Carmen'),
    ('5a1d4bfa-7f21-531d-8a43-8fc72f0a1396', '38014110', 'San Isidro', '38', 'El Carmen'),
    ('caa0b07a-0349-5f2e-aebf-9b56ee3c68eb', '38014120', 'San Juancito', '38', 'El Carmen'),
    ('c84bbece-1b26-5525-9154-b1a005b8f1e9', '38021010', 'Guerrero', '38', 'Dr. Manuel Belgrano'),
    ('f3a063c5-3939-5abb-adb5-36a7c7a37da2', '38021020', 'La Almona', '38', 'Dr. Manuel Belgrano'),
    ('213d3278-e146-5d2e-a9c1-3460a5a9e6b5', '38021030', 'León', '38', 'Dr. Manuel Belgrano'),
    ('40914401-3b12-549e-95bb-f6ce5a76da26', '38021040', 'Lozano', '38', 'Dr. Manuel Belgrano'),
    ('9f53b6a0-d8e7-5e0e-80ec-be9bb25d8679', '38021050', 'Ocloyas', '38', 'Dr. Manuel Belgrano'),
    ('2c10110f-269b-5076-92db-7bfb2babd905', '38021060', 'San Salvador de Jujuy', '38', 'Dr. Manuel Belgrano'),
    ('072177aa-b61a-52d6-a9d2-0524d799cbb2', '38021065', 'Tesorero', '38', 'Dr. Manuel Belgrano'),
    ('913a2552-3016-582e-9379-85ce207c2e0c', '38021070', 'Yala', '38', 'Dr. Manuel Belgrano'),
    ('410464bc-7a6b-54b7-a97e-11c3887d6855', '3802107001', 'Los Nogales', '38', 'Dr. Manuel Belgrano'),
    ('eb2b0e97-4d73-5e7f-8a63-7da115cf7949', '3802107002', 'San Pablo de Reyes', '38', 'Dr. Manuel Belgrano'),
    ('ed6982d4-1611-5b98-9577-24bbe272af64', '3802107003', 'Yala', '38', 'Dr. Manuel Belgrano'),
    ('3c4dd37b-c23f-569b-8c5d-77c070212d65', '38028003', 'Aparzo', '38', 'Humahuaca'),
    ('9c096807-2efd-59fe-9c93-9469a77daaea', '38028007', 'Cianzo', '38', 'Humahuaca'),
    ('af6562c2-6e4f-5f22-aaf4-43381fbb9eb5', '38028010', 'Coctaca', '38', 'Humahuaca'),
    ('880ffa88-9ea6-5801-9abb-1f0accb2008a', '38028020', 'El Aguilar', '38', 'Humahuaca'),
    ('bdc3f23b-4179-54af-a235-93a15d1ae16c', '38028030', 'Hipólito Yrigoyen', '38', 'Humahuaca'),
    ('776925ab-3a11-5f00-98b4-7fc953ed3862', '38028040', 'Humahuaca', '38', 'Humahuaca'),
    ('50a8137b-058e-5c17-9eef-2da24086ce5e', '38028043', 'Palca de Aparzo', '38', 'Humahuaca'),
    ('4c0154b8-80c8-5e2d-b851-b218381397f3', '38028045', 'Palca de Varas', '38', 'Humahuaca'),
    ('51901aad-3291-586b-a706-d73119d20e41', '38028047', 'Rodero', '38', 'Humahuaca'),
    ('38169951-3143-5d47-9d69-a32cd5fa08b7', '38028050', 'Tres Cruces', '38', 'Humahuaca'),
    ('5fde97ff-f586-54c9-a13a-b9da1952582c', '38028060', 'Uquía', '38', 'Humahuaca'),
    ('84561704-41ad-567f-8b64-d4007105bab1', '38035010', 'Bananal', '38', 'Ledesma'),
    ('2edee17b-1470-5077-bbba-8bbf95c55bde', '38035020', 'Bermejito', '38', 'Ledesma'),
    ('a6b8d835-e024-55c1-8578-d74f27f65ac1', '38035030', 'Caimancito', '38', 'Ledesma'),
    ('71015b5a-dc37-5c6d-abbb-22771fae1694', '38035040', 'Calilegua', '38', 'Ledesma'),
    ('0296283a-576a-56ea-8a2e-8ab0d474d9f2', '38035050', 'Chalicán', '38', 'Ledesma'),
    ('e0554470-b4c9-532d-be29-18d1c7bad149', '38035060', 'Fraile Pintado', '38', 'Ledesma'),
    ('d7718615-a077-5324-9e46-bebb78346e4d', '38035070', 'Libertad', '38', 'Ledesma'),
    ('d9d713ba-931c-5704-b9ad-43642d7e73ec', '38035080', 'Libertador General San Martín', '38', 'Ledesma'),
    ('89afd5ee-8d7e-5b86-b2ce-0f8c6c078cb3', '3803508001', 'Libertador General San Martín', '38', 'Ledesma'),
    ('c9359837-5f4e-5414-b3af-c73b4cde3ad3', '3803508002', 'Pueblo Ledesma', '38', 'Ledesma'),
    ('6017e282-77c1-532f-a8a2-ee8c8edc86c6', '38035090', 'Maíz Negro', '38', 'Ledesma'),
    ('d1beb11d-28dc-522b-86d5-08f0f16fde59', '38035100', 'Paulina', '38', 'Ledesma'),
    ('ce5c224b-48aa-5e11-a558-eec80533e6df', '38035110', 'Yuto', '38', 'Ledesma'),
    ('0a18beaa-11cb-5d7f-9c88-820850f668a2', '38042010', 'Carahunco', '38', 'Palpalá'),
    ('fe5f5536-c012-5dc4-a961-7eedba0b0cb8', '38042020', 'Centro Forestal', '38', 'Palpalá'),
    ('c069510e-fb09-526c-be16-ef9ecb82f601', '38042040', 'Palpalá', '38', 'Palpalá'),
    ('1a956479-1c32-51a4-be56-faa0e0a1d432', '3804204001', 'Palpalá', '38', 'Palpalá'),
    ('6cb134e3-dcd3-5953-bb09-0dea2efa2da0', '3804204002', 'Río Blanco', '38', 'Palpalá'),
    ('7b3f3d14-94c0-59df-ba02-c2d9acad376e', '38049003', 'Casa Colorada', '38', 'Rinconada'),
    ('dec792ad-6442-5687-9f5e-63f8c4851fad', '38049007', 'Coyaguaima', '38', 'Rinconada'),
    ('b25f4a3d-688a-58cb-bf88-07a959fb4a4f', '38049010', 'Lagunillas de Farallón', '38', 'Rinconada'),
    ('d27d1775-ae8e-5e93-85b9-0f591f09c4f0', '38049020', 'Liviara', '38', 'Rinconada'),
    ('e71b2ed0-0e04-5fbe-b346-4c7768007937', '38049025', 'Loma Blanca', '38', 'Rinconada'),
    ('ad99d4b5-1707-52c8-bc86-1cbf148d6ec8', '38049030', 'Nuevo Pirquitas', '38', 'Rinconada'),
    ('5f9d3058-64d2-563e-ad03-964ac4d5805a', '38049035', 'Orosmayo', '38', 'Rinconada'),
    ('561408f1-f20d-5667-ba1e-a2eeb2bf60da', '38049040', 'Rinconada', '38', 'Rinconada'),
    ('c751b451-27c4-5b14-9ad0-5b319a602463', '38056010', 'El Ceibal', '38', 'San Antonio'),
    ('ab1ade79-05f0-5597-a486-844f0a606a48', '38056017', 'Los Alisos', '38', 'San Antonio'),
    ('5b02c98a-1be6-5510-9a10-87b915985d04', '38056020', 'Loteo Navea', '38', 'San Antonio'),
    ('e722e8ae-e26e-5e4f-80f5-f91df2c8240b', '38056025', 'Nuestra Señora del Rosario', '38', 'San Antonio'),
    ('184734c3-9b19-5bd0-b92d-b49ffe257cac', '38056030', 'San Antonio', '38', 'San Antonio'),
    ('7d2da730-0898-5f60-9ff2-0a455c6ba535', '38063010', 'Arrayanal', '38', 'San Pedro'),
    ('21a23ce8-b1a5-5c24-ae38-72e9cac239f9', '38063020', 'Arroyo Colorado', '38', 'San Pedro'),
    ('5b68f08d-d6e2-51ed-9176-eda35cfc34dd', '38063030', 'Don Emilio', '38', 'San Pedro'),
    ('6acaa3ac-06dc-5635-88b2-431ccd2aaa39', '38063040', 'El Acheral', '38', 'San Pedro'),
    ('abf37c17-4dcf-5c71-9704-a1b8010d7fed', '38063050', 'El Puesto', '38', 'San Pedro'),
    ('97fc60ef-af3c-5dcf-aa2a-9dd5f038e00e', '38063060', 'El Quemado', '38', 'San Pedro'),
    ('2b011683-8cf6-54f2-b95a-6516efe84881', '38063070', 'La Esperanza', '38', 'San Pedro'),
    ('3dae1ec9-ee2f-5510-a0c5-596743f91303', '38063080', 'La Manga', '38', 'San Pedro'),
    ('34e2ea05-c962-5149-b91a-680c3e3825b0', '38063090', 'La Mendieta', '38', 'San Pedro'),
    ('4326190a-d86d-52fb-ba21-478bf7030aee', '38063110', 'Palos Blancos', '38', 'San Pedro'),
    ('8cdf1036-10d8-5264-a2af-7a416286cdee', '38063130', 'Piedritas', '38', 'San Pedro'),
    ('7048bffa-7724-5d2d-832f-cb6c0ed21d77', '38063140', 'Rodeito', '38', 'San Pedro'),
    ('e835efc7-d79d-52af-aaa2-3abab1fb9268', '38063150', 'Rosario de Río Grande (ex Barro Negro)', '38', 'San Pedro'),
    ('e8a87ec9-17e1-5f90-a8ce-f6cffd200376', '38063160', 'San Antonio', '38', 'San Pedro'),
    ('876dfe92-7d06-508f-8d86-891a10864064', '38063170', 'San Lucas', '38', 'San Pedro'),
    ('55dac5f1-e503-506e-8281-99c1fcda08b4', '38063180', 'San Pedro', '38', 'San Pedro'),
    ('62defaa9-dc26-5754-9ee2-2e357ca900a8', '38070010', 'El Fuerte', '38', 'Santa Bárbara'),
    ('d5eb2d03-afcf-54df-ad95-d4306083c064', '38070020', 'El Piquete', '38', 'Santa Bárbara'),
    ('841b3381-8580-5cd8-bc3f-770cd86a635e', '38070030', 'El Talar', '38', 'Santa Bárbara'),
    ('5afab29b-a877-5e72-b95b-ca44849af83b', '38070040', 'Palma Sola', '38', 'Santa Bárbara'),
    ('71982252-747e-5061-b4f1-541f70fd3185', '38070050', 'Puente Lavayén', '38', 'Santa Bárbara'),
    ('3e17d2f6-e7ad-51d3-943a-f90ef0c10bab', '38070060', 'Santa Clara', '38', 'Santa Bárbara'),
    ('d3195336-bb5a-5034-88fa-451356c42d61', '38070070', 'Vinalito', '38', 'Santa Bárbara'),
    ('fedfede7-a09f-5ebd-8a25-8ed7e904527d', '38077010', 'Casira', '38', 'Santa Catalina'),
    ('c0c99c05-c8b0-5ead-96da-86060fa7c528', '38077020', 'Ciénega de Paicone', '38', 'Santa Catalina'),
    ('98aa2b0b-40ee-58f3-87c1-f10bc7a7d041', '38077030', 'Cieneguillas', '38', 'Santa Catalina'),
    ('d492a38f-4aab-5c07-93e1-87d81dd170d6', '38077040', 'Cusi Cusi', '38', 'Santa Catalina'),
    ('f5635dab-9e72-579c-9655-1563779edd1c', '38077045', 'El Angosto', '38', 'Santa Catalina'),
    ('1be54338-0323-5acf-83bd-f032b639ed35', '38077050', 'La Ciénega', '38', 'Santa Catalina'),
    ('808cc74b-00ed-5271-a759-bd4d98e139f6', '38077060', 'Misarrumi', '38', 'Santa Catalina'),
    ('7250c3bc-8e91-58b8-b55c-b1b8fc6a0425', '38077070', 'Oratorio', '38', 'Santa Catalina'),
    ('a3e8fbfe-792b-5b3e-be25-76bbf20bcf6d', '38077080', 'Paicone', '38', 'Santa Catalina'),
    ('227fc840-e441-596d-a685-2cd2b7980367', '38077090', 'San Juan de Oros', '38', 'Santa Catalina'),
    ('af1b2e45-a382-5f74-bd99-bba651f7ef60', '38077100', 'Santa Catalina', '38', 'Santa Catalina'),
    ('0b33371b-cc2f-5be6-a646-f1bc3179a295', '38077110', 'Yoscaba', '38', 'Santa Catalina'),
    ('7642d25a-8ecd-5f98-89b7-93df1cc24071', '38084010', 'Catua', '38', 'Susques'),
    ('101e476e-af81-584b-a2b1-8da47d3b9498', '38084020', 'Coranzuli', '38', 'Susques'),
    ('b9b08f38-4c16-55d6-9526-32e873293382', '38084030', 'El Toro', '38', 'Susques'),
    ('36b465c7-95c0-5b4a-962d-6330a007f2ba', '38084040', 'Huáncar', '38', 'Susques'),
    ('2edba2df-3e9c-556b-b679-108075bbe0d1', '38084045', 'Jama', '38', 'Susques'),
    ('2af89340-1125-5e1a-8f2f-bb7e8651c11a', '38084050', 'Mina Providencia', '38', 'Susques'),
    ('692a29c6-b003-586f-bb9b-b7c6ce336ffe', '38084055', 'Olacapato', '38', 'Susques'),
    ('54ed9082-c43b-5b0b-b927-7bd9c8c0b52c', '38084060', 'Olaroz Chico', '38', 'Susques'),
    ('b686a3f9-c90d-5243-bd98-52095c3b1254', '38084070', 'Pastos Chicos', '38', 'Susques'),
    ('3be532aa-c4cc-5df7-b849-bba87e2aeb62', '38084080', 'Puesto Sey', '38', 'Susques'),
    ('ca7d3e98-1909-55a8-8a81-26eb5d8c5950', '38084090', 'San Juan de Quillaqués', '38', 'Susques'),
    ('1efd395f-e022-5f13-be30-2c11d483418b', '38084100', 'Susques', '38', 'Susques'),
    ('3b8d04fa-12bd-55bc-ab64-d3fa6789cf5f', '38094010', 'Colonia San José', '38', 'Tilcara'),
    ('ece69dfb-6c34-5b3f-9064-0e2758366fe6', '3809401001', 'Colonia San José', '38', 'Tilcara'),
    ('1ff5355c-7b94-5d97-8e60-b9539a4176ae', '3809401002', 'Yacoraite', '38', 'Tilcara'),
    ('249493aa-3c57-57b2-81b0-797979baf1a0', '38094020', 'Huacalera', '38', 'Tilcara'),
    ('e615dfa6-0adb-53fe-8da2-a9d1eba8828f', '38094030', 'Juella', '38', 'Tilcara'),
    ('d6ac7c15-3f63-5e43-968f-aa70c106a588', '38094040', 'Maimará', '38', 'Tilcara'),
    ('1668022f-da68-544c-9360-c821867bad3e', '38094050', 'Tilcara', '38', 'Tilcara'),
    ('2979f6f0-0781-536f-a9ff-34bc3e729f4b', '38098010', 'Bárcena', '38', 'Tumbaya'),
    ('057c5b0c-1d47-5fc3-94d7-de3ee39f5b3b', '38098020', 'El Moreno', '38', 'Tumbaya'),
    ('410c2f23-6166-5ac2-9bbe-320cba62439b', '38098025', 'Puerta de Colorados', '38', 'Tumbaya'),
    ('e3ae2003-451f-5a34-b6a7-9aabed4fa23e', '38098030', 'Purmamarca', '38', 'Tumbaya'),
    ('62efce5d-6c90-5cd9-9a6d-5a743cabf403', '38098040', 'Tumbaya', '38', 'Tumbaya'),
    ('d9831aeb-bf7f-5515-890b-2c2268e67253', '38098050', 'Volcán', '38', 'Tumbaya'),
    ('6aec9513-50d9-5572-b13e-1575e7b8fd31', '38105010', 'Caspalá', '38', 'Valle Grande'),
    ('f2c48c84-df24-5347-b422-2cfe5e0e50d4', '38105020', 'Pampichuela', '38', 'Valle Grande'),
    ('61429836-731f-51f4-82ab-f99742837e8f', '38105030', 'San Francisco', '38', 'Valle Grande'),
    ('614ca32e-3767-5097-9ffb-0b8ae951d8d6', '38105040', 'Santa Ana', '38', 'Valle Grande'),
    ('295cc23e-672f-5cd4-9b62-b198c870171a', '38105050', 'Valle Colorado', '38', 'Valle Grande'),
    ('fdf5c3b5-2e4e-54e0-a6d1-16d78596a3f5', '38105060', 'Valle Grande', '38', 'Valle Grande'),
    ('c3d17614-485c-5ac1-9a2f-006e66169cb3', '38112010', 'Barrios', '38', 'Yavi'),
    ('46b42d9c-2f38-57c5-94da-03062fdd62ea', '38112020', 'Cangrejillos', '38', 'Yavi'),
    ('d55061bf-8a76-597a-b7c6-a2cb89b23b9c', '38112030', 'El Cóndor', '38', 'Yavi'),
    ('62cfa947-214c-5972-a64d-87193bc3cb1d', '38112040', 'La Intermedia', '38', 'Yavi'),
    ('6dc278d1-1662-58e5-aaa5-3a5781abf0d8', '38112050', 'La Quiaca', '38', 'Yavi'),
    ('bb418796-cae0-5c17-989f-6e33ed433820', '38112060', 'Llulluchayoc', '38', 'Yavi'),
    ('d7d6b421-25c2-530d-9dab-4885cc28af2b', '38112070', 'Pumahuasi', '38', 'Yavi'),
    ('d3d7c60f-62f2-5102-a0db-79ca7505f165', '38112080', 'Yavi', '38', 'Yavi'),
    ('ec13fafd-f301-5e36-89e2-f03b2d201025', '3811208001', 'San José', '38', 'Yavi'),
    ('6afe91d1-aa1d-5edf-820b-f5d1c303934c', '3811208002', 'Yavi', '38', 'Yavi'),
    ('7c0f6f85-e0a0-5e1d-8b03-76a7398a986d', '38112090', 'Yavi Chico', '38', 'Yavi'),
    ('b1bb3d37-6335-53a0-9b6c-15ecc53ad4ac', '42007010', 'Doblas', '42', 'Atreucó'),
    ('0556902e-3b13-5f88-a542-c8b4795f24c1', '42007020', 'Macachín', '42', 'Atreucó'),
    ('f0a00e93-afe8-562e-92b4-5d81454dcf72', '42007030', 'Miguel Riglos', '42', 'Atreucó'),
    ('20ac8b3c-cff8-5f23-b9b6-61b7ec65e47d', '42007040', 'Rolón', '42', 'Atreucó'),
    ('c694ed5c-4f89-56d9-af3b-7134d4a3d409', '42007050', 'Tomás M. Anchorena', '42', 'Atreucó'),
    ('b6616c14-5e6b-586e-91b3-98b165b48c75', '42014010', 'Anzoátegui', '42', 'Caleu Caleu'),
    ('3f02804c-eee8-5faf-b150-a5de53165a79', '42014020', 'La Adela', '42', 'Caleu Caleu'),
    ('1bd64cf7-0120-5df1-85ea-2aca09a2e634', '42021010', 'Anguil', '42', 'Capital'),
    ('6a38cbff-45a3-5775-bddf-7e160aebbdae', '42021020', 'Santa Rosa', '42', 'Capital'),
    ('f1fa4cde-fd6c-5467-8375-d4bcb1d7b314', '42028010', 'Catriló', '42', 'Catriló'),
    ('0184882f-4c63-5af3-8ea4-27211a9f7f83', '42028020', 'La Gloria', '42', 'Catriló'),
    ('3a35f05c-460b-5976-8f26-355570211dff', '42028030', 'Lonquimay', '42', 'Catriló'),
    ('2f5416bd-65cc-58f2-b9d0-ead7f7295422', '42028040', 'Uriburu', '42', 'Catriló'),
    ('83b212e8-ac5e-561c-888f-9bc5952065ed', '42035010', 'Conhelo', '42', 'Conhelo'),
    ('fa4c3641-966d-52e1-90d5-9807c84aeafc', '42035020', 'Eduardo Castex', '42', 'Conhelo'),
    ('9e0b0a25-385a-53f3-85d2-db384f8ab8aa', '42035030', 'Mauricio Mayer', '42', 'Conhelo'),
    ('3758d65a-3da9-50a7-aaf0-0e5e2fa8869a', '42035040', 'Monte Nievas', '42', 'Conhelo'),
    ('39ba0c83-02f7-5a1c-b1a6-de6dad4baed7', '42035050', 'Rucanelo', '42', 'Conhelo'),
    ('ff033892-d177-5ebb-bba3-8b78872646ea', '42035060', 'Winifreda', '42', 'Conhelo'),
    ('f5b54c73-48fb-5909-8cac-4455a3e8f758', '42042010', 'Gobernador Duval', '42', 'Curacó'),
    ('ae2998fa-e1d0-502f-a128-708540302184', '42042020', 'Puelches', '42', 'Curacó'),
    ('e5ab44ea-08a5-52e8-881a-007d205e8371', '42049010', 'Santa Isabel', '42', 'Chalileo'),
    ('6d54ff6e-bad6-5054-a706-327592d1edab', '42056010', 'Bernardo Larroude', '42', 'Chapaleufú'),
    ('27dc14e6-b9d4-5c47-9e4b-dfc76a56ae8d', '42056020', 'Ceballos', '42', 'Chapaleufú'),
    ('7833e7f9-75be-5d96-a873-acd24b92d99c', '42056030', 'Coronel Hilario Lagos', '42', 'Chapaleufú'),
    ('54610476-647d-5b7a-802f-8b65b6212225', '42056040', 'Intendente Alvear', '42', 'Chapaleufú'),
    ('932fb75d-9df2-5c41-8323-42becc0b1911', '42056050', 'Sarah', '42', 'Chapaleufú'),
    ('2c84c5ca-b5c2-57f4-ba37-377e3800ec19', '42056060', 'Vértiz', '42', 'Chapaleufú'),
    ('2dafb213-d3a8-59de-aa6c-1b43479f1172', '42063010', 'Algarrobo del Águila', '42', 'Chical Co'),
    ('09171e87-5e29-5bec-becb-b6adfa440603', '42063020', 'La Humada', '42', 'Chical Co'),
    ('b02ed3ea-a870-5ae7-95a0-12df43c8df0b', '42070010', 'Alpachiri', '42', 'Guatraché'),
    ('54abbcc5-8492-5610-896a-89f7e20a4a51', '42070020', 'General Manuel J. Campos', '42', 'Guatraché'),
    ('ab844e89-575d-58b7-a363-217d01b0e194', '42070030', 'Guatraché', '42', 'Guatraché'),
    ('d1f4a0e7-45fb-5955-949b-6d66055a67ca', '42070040', 'Perú', '42', 'Guatraché'),
    ('047beb91-0148-56b0-93ba-2d5079471a45', '42070050', 'Santa Teresa', '42', 'Guatraché'),
    ('acb5d955-d207-5d14-8907-eef8fe88fc07', '42077010', 'Abramo', '42', 'Hucal'),
    ('c371333f-1fc3-547a-bbf4-e5651989470f', '42077020', 'Bernasconi', '42', 'Hucal'),
    ('41a6c491-9d2d-5050-9a8d-71cab6af6029', '42077030', 'General San Martín', '42', 'Hucal'),
    ('5248f1a4-5a76-5de4-9d08-559e5192c5cf', '42077040', 'Hucal', '42', 'Hucal'),
    ('803835b8-340d-57f6-a3af-f7ab7b1187b0', '42077050', 'Jacinto Aráuz', '42', 'Hucal'),
    ('2aeedf32-a987-5e66-9989-4e94b9af6312', '42084010', 'Cuchillo Co', '42', 'Lihuel Calel'),
    ('70c0bea1-0ea2-5a2f-8b9e-73be7caa2364', '42091010', 'La Reforma', '42', 'Limay Mahuida'),
    ('f108ee2b-47a5-59c5-820e-101da3ee58af', '42091020', 'Limay Mahuida', '42', 'Limay Mahuida'),
    ('15d769f2-9fa5-5c76-9519-fcbcfb778049', '42098010', 'Carro Quemado', '42', 'Loventué'),
    ('a6ae4f3f-1e2d-59fe-be61-e176c1180722', '42098020', 'Loventué', '42', 'Loventué'),
    ('04eac06f-97d6-5481-81dc-72d331d37abd', '42098030', 'Luan Toro', '42', 'Loventué'),
    ('e0f9e533-3424-5ac1-aad6-bdd186947b0d', '42098040', 'Telén', '42', 'Loventué'),
    ('10ab37a6-f427-5841-8d24-72b9c2e179ce', '42098050', 'Victorica', '42', 'Loventué'),
    ('153c5abd-4992-500f-9564-2b0c48592e2a', '42105010', 'Agustoni', '42', 'Maracó'),
    ('b1f29166-8637-5071-b52c-599157acb0fc', '42105020', 'Dorila', '42', 'Maracó'),
    ('cbeb1f62-b47f-5afe-846f-80d0db935a0b', '42105030', 'General Pico', '42', 'Maracó'),
    ('1e1a1954-ae4c-5c8c-93e4-f7a155265e6a', '42105040', 'Speluzzi', '42', 'Maracó'),
    ('7b5d29e5-da27-50e7-9549-7f3e90f356a3', '42105050', 'Trebolares', '42', 'Maracó'),
    ('5b03d157-e5c0-519a-ae92-902da4162f5c', '42112005', 'Casa de Piedra', '42', 'Puelén'),
    ('38ab79d6-41d3-57eb-91cc-cdc5d11d8eb9', '42112010', 'Puelén', '42', 'Puelén'),
    ('61653f43-ffe3-53b9-9d8e-4a0bff533202', '42112020', '25 de Mayo', '42', 'Puelén'),
    ('8500abec-e68c-53d0-b095-6a0415845f92', '42119010', 'Colonia Barón', '42', 'Quemú Quemú'),
    ('49112904-76e7-5a7f-bddd-1c7737c8940c', '42119020', 'Colonia San José', '42', 'Quemú Quemú'),
    ('f14a1832-9ef9-5a2e-b223-9821fd867df6', '42119030', 'Miguel Cané', '42', 'Quemú Quemú'),
    ('30bf9103-dcd9-546e-b793-db10cb4d461b', '42119040', 'Quemú Quemú', '42', 'Quemú Quemú'),
    ('66bfb790-248b-5bdc-854f-bcd283787588', '42119050', 'Relmo', '42', 'Quemú Quemú'),
    ('0e99dae4-9f38-5583-8e88-7a2b15671c67', '42119060', 'Villa Mirasol', '42', 'Quemú Quemú'),
    ('a01bd8bd-a809-5ac1-9001-a7035c8b3cf1', '42126010', 'Caleufú', '42', 'Rancul'),
    ('4cf73b5b-1c9e-5e13-b9fc-f0c0a6d5ea8c', '42126020', 'Ingeniero Foster', '42', 'Rancul'),
    ('8e7967b6-96f2-5c79-a42f-64cc464c6290', '42126030', 'La Maruja', '42', 'Rancul'),
    ('86f96521-f4ff-5443-86c9-5aa4c791ce1f', '42126040', 'Parera', '42', 'Rancul'),
    ('fb10ff98-7889-55e8-80d2-0774b1d090f9', '42126050', 'Pichi Huinca', '42', 'Rancul'),
    ('85b3cb24-864b-50ff-ae38-e59ce1ecc3d0', '42126060', 'Quetrequén', '42', 'Rancul'),
    ('7777f14c-4b84-5a5d-ab46-d54ed13b7885', '42126070', 'Rancul', '42', 'Rancul'),
    ('5ab3f129-6063-578e-9f2c-596ca900d3cc', '42133010', 'Adolfo Van Praet', '42', 'Realicó'),
    ('26118e6a-7f3c-59f1-916a-fa3a2d9d419c', '42133020', 'Alta Italia', '42', 'Realicó'),
    ('1212f026-9d0d-5d33-a874-7c69156ccf59', '42133030', 'Damián Maisonave', '42', 'Realicó'),
    ('2e4a2efc-fa4e-5919-96c6-be07c2db59d7', '42133040', 'Embajador Martini', '42', 'Realicó'),
    ('0828a68c-7992-5e68-99a8-0159a112aef0', '42133050', 'Falucho', '42', 'Realicó'),
    ('e14e7ce3-04ca-53e1-b21a-8ffbec014104', '42133060', 'Ingeniero Luiggi', '42', 'Realicó'),
    ('4284af18-bfd7-58e6-8285-80ae8c5eab4f', '42133070', 'Ojeda', '42', 'Realicó'),
    ('e46627a2-8628-5e70-a2e8-eb0777f536a7', '42133080', 'Realicó', '42', 'Realicó'),
    ('ad1b7707-732f-51ee-a95c-d543d76a8040', '42140005', 'Cachirulo', '42', 'Toay'),
    ('7b9a21dc-3259-54d0-bc49-90fa27b6e93a', '42140010', 'Naicó', '42', 'Toay'),
    ('3c0f94b6-f28e-5500-b918-3bb9bc2d83b5', '42140020', 'Toay', '42', 'Toay'),
    ('7b57d847-3761-5fbc-a901-6e544bbbb4d4', '42147010', 'Arata', '42', 'Trenel'),
    ('57fac731-f932-58b0-beda-8d399a4263da', '42147020', 'Metileo', '42', 'Trenel'),
    ('1661b0d4-4461-522c-b047-2f5d9c119df3', '42147030', 'Trenel', '42', 'Trenel'),
    ('bd4c4d5f-df7f-5899-9e86-5322a7bdb152', '42154010', 'Ataliva Roca', '42', 'Utracán'),
    ('d3b7efc5-0fec-52bb-9988-915452930f18', '42154020', 'Chacharramendi', '42', 'Utracán'),
    ('aaf089e9-9fe8-5a95-8e9f-e4809b6efeab', '42154030', 'Colonia Santa María', '42', 'Utracán'),
    ('30ee2790-c3df-5be9-a6fd-9c8b95c2d51b', '42154040', 'General Acha', '42', 'Utracán'),
    ('7327885b-ef4a-5c5d-b063-192e45fd3023', '42154050', 'Quehué', '42', 'Utracán'),
    ('7c79e3d4-b954-5a97-92cb-80f6a637ed44', '42154060', 'Unanué', '42', 'Utracán'),
    ('4fb13f70-72a1-586a-8148-421f83ae142a', '46007010', 'Aimogasta', '46', 'Arauco'),
    ('8a0fd69d-cae3-519d-8972-b5e0603b24de', '4600701001', 'Aimogasta', '46', 'Arauco'),
    ('3fe029d6-494f-526b-a083-a3c62bcb794a', '4600701002', 'Machigasta', '46', 'Arauco'),
    ('8fbe2ae7-1992-5148-bcaa-ce39dead11b2', '4600701003', 'San Antonio', '46', 'Arauco'),
    ('61449ec8-7fd2-58c6-ac26-37ff3597f9f1', '46007030', 'Bañado de los Pantanos', '46', 'Arauco'),
    ('ca2ec475-f708-5d9d-9cf6-ee14dca6eaab', '46007040', 'Estación Mazán', '46', 'Arauco'),
    ('f3ee5080-33aa-5bc7-bfc2-498a58018464', '46007045', 'Termas de Santa Teresita', '46', 'Arauco'),
    ('b02e88f4-d2cf-5b15-be2c-a0f4505d20c1', '46007050', 'Villa Mazán', '46', 'Arauco'),
    ('f36f6b69-db7f-5c6c-8f69-ac9fcd44deef', '46014010', 'La Rioja', '46', 'Capital'),
    ('a1e90de0-0e20-5288-ac4b-17c11cbdbb04', '46021010', 'Aminga', '46', 'Castro Barros'),
    ('9f199a26-b2e2-55d8-a915-2551d2ead1ad', '46021020', 'Anillaco', '46', 'Castro Barros'),
    ('7052b55b-846f-56eb-b472-4908facab37f', '46021030', 'Anjullón', '46', 'Castro Barros'),
    ('8eea1f79-a04e-51b2-bc89-911ada5e0ff5', '46021040', 'Chuquis', '46', 'Castro Barros'),
    ('5c5363b4-888a-51f0-b2e0-0e9fe45c4219', '46021050', 'Los Molinos', '46', 'Castro Barros'),
    ('acd7d416-947e-5d6c-98b2-4e58c2c8f4e2', '46021060', 'Pinchas', '46', 'Castro Barros'),
    ('2f8ce7b8-2a4c-5dff-b513-0fa95e484454', '46021070', 'San Pedro', '46', 'Castro Barros'),
    ('e8719058-4fb2-54c1-a823-b4d2ec3a17fb', '46021080', 'Santa Vera Cruz', '46', 'Castro Barros'),
    ('37401cf2-bc93-5987-bc40-0e7e5664103f', '46028010', 'Aicuñá', '46', 'General Felipe Varela'),
    ('91047248-612c-5815-9fb2-3d778d402d28', '46028020', 'Guandacol', '46', 'General Felipe Varela'),
    ('3ea18d9c-625b-532f-b69b-5c2e64a77c9a', '4602802001', 'Guandacol', '46', 'General Felipe Varela')
ON CONFLICT (indec_id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('460fb272-54ab-5e25-b2aa-735f725d3004', '4602802002', 'Santa Clara', '46', 'General Felipe Varela'),
    ('e5d385fb-f38f-5ae6-ae70-c06a2629a61c', '46028030', 'Los Palacios', '46', 'General Felipe Varela'),
    ('717de3e5-efb2-55b0-abaa-cca0aff3636e', '46028040', 'Pagancillo', '46', 'General Felipe Varela'),
    ('e7f901f1-59ba-5b43-b91a-e16b4c6230b9', '46028050', 'Villa Unión', '46', 'General Felipe Varela'),
    ('705894fb-b5ba-53ce-bfc0-5727e8a2dbc9', '4602805001', 'Banda Florida', '46', 'General Felipe Varela'),
    ('459a33ee-f3a2-549d-b425-90367a3a3d4f', '4602805002', 'Villa Unión', '46', 'General Felipe Varela'),
    ('6f94324d-279f-5703-8aa2-d119e02bd8ca', '46035010', 'Chamical', '46', 'Chamical'),
    ('ebc21a92-2c89-527a-b00c-36c761e6e1cc', '46035020', 'Polco', '46', 'Chamical'),
    ('e76ac752-8fae-50e0-a516-b92c57a71ad1', '46042010', 'Chilecito', '46', 'Chilecito'),
    ('9be71487-78f7-557d-854f-465efd4ac6cc', '4604201001', 'Anguinán', '46', 'Chilecito'),
    ('358cfbca-ea66-55eb-8bd7-3c52daaac452', '4604201002', 'Chilecito', '46', 'Chilecito'),
    ('117326f3-bca4-5b30-97ec-b835c8639adf', '4604201003', 'La Puntilla', '46', 'Chilecito'),
    ('e960aec7-1b01-5f4e-8ea3-02b824817a41', '4604201004', 'Los Sarmientos', '46', 'Chilecito'),
    ('d11749b3-5ea0-58df-b28a-72fabe2bf2f5', '4604201005', 'San Miguel', '46', 'Chilecito'),
    ('51491a1a-33bd-5e3e-831b-173e611e2dc7', '46042020', 'Colonia Anguinán', '46', 'Chilecito'),
    ('9dcc1436-c4a4-5a8d-8e4b-1a272a26d638', '46042040', 'Colonia Malligasta', '46', 'Chilecito'),
    ('6a9972f9-003f-50a4-bf15-5abc464fb1b6', '46042050', 'Colonia Vichigasta', '46', 'Chilecito'),
    ('38c6ece7-6e28-592e-b148-2d5746c24432', '46042060', 'Guanchín', '46', 'Chilecito'),
    ('574aee87-5520-5243-82fc-c2ac47a6319b', '46042070', 'Malligasta', '46', 'Chilecito'),
    ('ab658799-7107-5ba8-8e2a-99b8603696e2', '46042080', 'Miranda', '46', 'Chilecito'),
    ('155161ff-947f-5dec-b277-52b41b22018b', '46042090', 'Nonogasta', '46', 'Chilecito'),
    ('87ce0f06-404d-581f-b7b4-b2fc37363a47', '46042100', 'San Nicolás', '46', 'Chilecito'),
    ('bdaaeeb7-1304-5769-ba83-5d0b7afb9ca9', '46042110', 'Santa Florentina', '46', 'Chilecito'),
    ('9944f3a8-3752-57f2-89bc-ff219adfbb20', '46042120', 'Sañogasta', '46', 'Chilecito'),
    ('e9c66923-7bae-5064-8dec-394c72f2d3db', '46042130', 'Tilimuqui', '46', 'Chilecito'),
    ('5047e945-8c46-5d78-ac2d-bbd3dd2e95cb', '46042140', 'Vichigasta', '46', 'Chilecito'),
    ('f4ebb84e-0919-527c-b8db-fc8d2204d1a4', '46049010', 'Alto Carrizal', '46', 'Famatina'),
    ('b3f86269-c591-58a9-ab16-768af2e6e7e6', '46049020', 'Angulos', '46', 'Famatina'),
    ('61f6a7e8-0363-5eb1-a81f-860c5cc4d678', '46049030', 'Antinaco', '46', 'Famatina'),
    ('95e37d33-2f67-5b37-9985-64d4759b6d5d', '46049040', 'Bajo Carrizal', '46', 'Famatina'),
    ('4a636cbe-e5f7-5566-a14c-e6af104a3766', '46049050', 'Campanas', '46', 'Famatina'),
    ('26c05d0e-01fa-5db8-aa11-200ae6674d98', '46049060', 'Chañarmuyo', '46', 'Famatina'),
    ('8612870e-ba85-5348-b286-1748afb30cb0', '46049070', 'Famatina', '46', 'Famatina'),
    ('10f1778c-f667-571c-87fc-2b37ac9f8a84', '46049080', 'La Cuadra', '46', 'Famatina'),
    ('58452c1a-e1e9-5410-ab15-5b4553e48c83', '46049090', 'Pituil', '46', 'Famatina'),
    ('83affffc-5135-5615-b5eb-76785ddc544c', '46049100', 'Plaza Vieja', '46', 'Famatina'),
    ('e6504746-77be-5cf1-a4f3-00cceb367724', '46049110', 'Santa Cruz', '46', 'Famatina'),
    ('783975be-5e1d-5807-a566-c2b47f94e89d', '46049120', 'Santo Domingo', '46', 'Famatina'),
    ('bf2c9948-f243-5008-8e60-de64d64faec3', '46056010', 'Punta de los Llanos', '46', 'Ángel Vicente Peñaloza'),
    ('3e7a4e56-d95b-5baa-8630-e021e971f24b', '46056020', 'Tama', '46', 'Ángel Vicente Peñaloza'),
    ('ff8f51e8-8ee5-5505-a5a7-10a44062432a', '46063010', 'Castro Barros', '46', 'General Belgrano'),
    ('25975653-67fa-52c6-a59d-bc544d167bc0', '46063020', 'Chañar', '46', 'General Belgrano'),
    ('2db70c0e-0f48-5b5b-bc9c-eb13cac5b09b', '46063030', 'Loma Blanca', '46', 'General Belgrano'),
    ('9bd156d7-0c64-592f-8efa-ceee72468558', '46063040', 'Olta', '46', 'General Belgrano'),
    ('43cb1769-0766-5395-8cb9-962467b34e77', '46070010', 'Malanzán', '46', 'General Juan Facundo Quiroga'),
    ('48833dcd-99a6-5f23-926f-75a34769b706', '46070020', 'Nácate', '46', 'General Juan Facundo Quiroga'),
    ('b44478c6-5500-537e-8655-a751ba695aeb', '46070030', 'Portezuelo', '46', 'General Juan Facundo Quiroga'),
    ('6ed8b837-f55a-538f-811b-c527fa4a7f54', '46070040', 'San Antonio', '46', 'General Juan Facundo Quiroga'),
    ('1a26c939-e986-5d4d-a1cd-2d276c7f0b9d', '46077010', 'Villa Castelli', '46', 'General Lamadrid'),
    ('4af0631b-778c-542b-a0f6-de49840f9e7b', '46084010', 'Ambil', '46', 'General Ortiz de Ocampo'),
    ('677f706a-d949-5233-9658-0f4e3434e59f', '46084020', 'Colonia Ortiz de Ocampo', '46', 'General Ortiz de Ocampo'),
    ('23ee48de-1d32-5d49-baf1-495ac5625e01', '46084030', 'Milagro', '46', 'General Ortiz de Ocampo'),
    ('5505208c-48f3-5720-9288-5253c8ad908a', '46084040', 'Olpas', '46', 'General Ortiz de Ocampo'),
    ('add01217-0ded-5783-ab17-9d006b3280a3', '46084050', 'Santa Rita de Catuna', '46', 'General Ortiz de Ocampo'),
    ('e89fcef3-bc15-543e-aef3-03408ed48787', '46091010', 'Ulapes', '46', 'General San Martín'),
    ('c318e636-18a3-55f0-9319-c6286ea0795a', '46098010', 'Jagüé', '46', 'Vinchina'),
    ('4d6b2986-508f-5cae-9456-d284581fdb08', '46098020', 'Villa San José de Vinchina', '46', 'Vinchina'),
    ('4bf6890b-b749-59fb-8c79-a2ba4ff21c22', '46105010', 'Amaná', '46', 'Independencia'),
    ('d01e1adc-2e85-5903-bed0-674aa40a7698', '46105020', 'Patquía', '46', 'Independencia'),
    ('5cbaac91-5e57-53ad-9314-2b6aaceeeb71', '46112010', 'Chepes', '46', 'Rosario Vera Peñaloza'),
    ('dbf2a9f8-a9c0-59c6-89fc-0c4ac802f0cd', '46112020', 'Desiderio Tello', '46', 'Rosario Vera Peñaloza'),
    ('6e153673-0c9b-58ed-9f92-d163ef37a891', '46119010', 'Salicas - San Blas', '46', 'San Blas de Los Sauces'),
    ('b2277f06-a039-52a3-a3f1-2012c1d4e63a', '4611901001', 'Alpasinche', '46', 'San Blas de Los Sauces'),
    ('eece5d45-cd1d-595e-b510-0f593a3db7c2', '4611901002', 'Amuschina', '46', 'San Blas de Los Sauces'),
    ('31dbd12f-584f-571d-a95a-a6f832b785d2', '4611901003', 'Andolucas', '46', 'San Blas de Los Sauces'),
    ('a29f4469-1b5b-53f0-9254-cf073b35f568', '4611901004', 'Chaupihuasi', '46', 'San Blas de Los Sauces'),
    ('afc7d052-7296-55a4-95a9-f9b8992a57d0', '4611901005', 'Cuipán', '46', 'San Blas de Los Sauces'),
    ('7426ac14-f935-5aa1-bd41-a136e0a44bd8', '4611901006', 'Las Talas', '46', 'San Blas de Los Sauces'),
    ('f0729c8a-c661-50d1-aa80-2d1b0ac0bc51', '4611901007', 'Los Robles', '46', 'San Blas de Los Sauces'),
    ('6ea966db-10aa-5d6d-b736-c8aed8ecc9e5', '4611901008', 'Salicas', '46', 'San Blas de Los Sauces'),
    ('29e328f3-08c2-5391-84d4-6d8944da8a83', '4611901009', 'San Blas', '46', 'San Blas de Los Sauces'),
    ('1abda251-7d36-51ca-bc65-4130e8f90f50', '4611901010', 'Shaqui', '46', 'San Blas de Los Sauces'),
    ('3201c683-e67b-5906-9060-765f4fa1dace', '4611901011', 'Suriyaco', '46', 'San Blas de Los Sauces'),
    ('bbf95d9b-466a-520d-b0d2-2e1e37827df9', '4611901012', 'Tuyubil', '46', 'San Blas de Los Sauces'),
    ('262db4eb-9f1a-51a1-a624-9126507c924c', '46126010', 'Villa Sanagasta', '46', 'Sanagasta'),
    ('94fdb2f5-e254-5830-af12-5e75b1215921', '50007010', 'Mendoza', '50', 'Capital'),
    ('83378294-997e-5208-a626-481b4eca2b22', '5000701001', '1ra Sección', '50', 'Capital'),
    ('e6536df5-9e2a-567c-8b65-7a046f27429f', '5000701002', '2da Sección', '50', 'Capital'),
    ('804df58b-b6bc-54e8-a634-66a359d9a549', '5000701003', '3ra Sección', '50', 'Capital'),
    ('0cf5bf44-9364-59c7-9e28-62c5d9629062', '5000701004', '4ta Sección', '50', 'Capital'),
    ('1f09cbfb-82e3-5355-94d1-91678bc1149c', '5000701005', '5ta Sección', '50', 'Capital'),
    ('c443a806-0a3d-58ed-a2d5-89dc7089a404', '5000701006', '6ta Sección', '50', 'Capital'),
    ('5d6ef225-93a9-5fef-b662-b7fb954d745d', '5000701007', '7ma Sección', '50', 'Capital'),
    ('183b6685-f2bd-5f92-8676-ec57a8a16ac7', '5000701008', '8va Sección', '50', 'Capital'),
    ('413682ed-24c7-5230-8649-ff222ca99a01', '5000701009', '9na Sección', '50', 'Capital'),
    ('7638367d-5904-5f39-9f10-3084c5d4c3b0', '5000701010', '10ma Sección', '50', 'Capital'),
    ('5fb367cf-1031-5422-946f-96b179cbe5e0', '5000701011', '11va Sección', '50', 'Capital'),
    ('38c68310-5f5e-525b-96aa-b67418d42e1b', '50014010', 'Bowen', '50', 'General Alvear'),
    ('8ae1519a-4e00-5c6b-8fcc-622ad33980ec', '50014020', 'Carmensa', '50', 'General Alvear'),
    ('ff6518b2-a6e5-57c6-ad52-d1e2d1aa60bc', '50014030', 'General Alvear', '50', 'General Alvear'),
    ('b374a767-c3b7-5fb6-8b94-1be592dd2312', '50014040', 'Los Compartos', '50', 'General Alvear'),
    ('caff3872-3bf0-588b-9a37-22145ccfcc10', '50021010', 'Godoy Cruz', '50', 'Godoy Cruz'),
    ('7df82740-134c-502f-9668-d0dc0f4681bd', '5002101001', 'Ciudad de Godoy Cruz', '50', 'Godoy Cruz'),
    ('a3a2e4a1-f2e9-5dfe-b1b4-1a3961138b1f', '5002101002', 'Gobernador Benegas', '50', 'Godoy Cruz'),
    ('5208d890-d82c-5713-bc80-e5ee962247d5', '5002101003', 'Las Tortugas', '50', 'Godoy Cruz'),
    ('c3453f3d-52dd-5adf-afbe-4d2596ac821a', '5002101004', 'Presidente Sarmiento', '50', 'Godoy Cruz'),
    ('3e9fe818-cdbe-5ec4-a829-e37a43d8814e', '5002101005', 'San Francisco del Monte', '50', 'Godoy Cruz'),
    ('d53ec873-b1b8-5154-8569-23613b3db037', '50028010', 'Colonia Segovia', '50', 'Guaymallén'),
    ('70a95ef3-f9d9-5fcf-94e3-3fca83c633fd', '50028020', 'Guaymallén', '50', 'Guaymallén'),
    ('c2d60b8c-578e-5a1b-98b5-6c63afbb5e84', '5002802001', 'Bermejo', '50', 'Guaymallén'),
    ('5128de5a-114c-51db-a05c-be0fa16fe57c', '5002802002', 'Buena Nueva', '50', 'Guaymallén'),
    ('45b71f0f-409e-547a-aa12-6aac4c49f429', '5002802003', 'Capilla del Rosario', '50', 'Guaymallén'),
    ('f01210c9-9be4-5327-95fd-92b70046c327', '5002802004', 'Dorrego', '50', 'Guaymallén'),
    ('d566c08f-485c-5df6-a6fa-a8cc6f0ace56', '5002802005', 'El Sauce', '50', 'Guaymallén'),
    ('f6d2d913-ea07-5619-afcd-894fdf459c98', '5002802006', 'General Belgrano', '50', 'Guaymallén'),
    ('da2edba6-3171-5f53-b19f-3a8b860c4c2b', '5002802007', 'Jesús Nazareno', '50', 'Guaymallén'),
    ('253bd5ef-5aaf-5339-a654-28d986ed0e53', '5002802008', 'Las Cañas', '50', 'Guaymallén'),
    ('b51308f2-b12c-506a-81ce-51c2f8580d04', '5002802009', 'Nueva Ciudad', '50', 'Guaymallén'),
    ('f7418fe3-ab0c-58d3-bcc3-e0409dda97c4', '5002802010', 'Pedro Molina', '50', 'Guaymallén'),
    ('402c8f09-e875-5146-89fc-73ebac6670b2', '5002802011', 'Rodeo de la Cruz', '50', 'Guaymallén'),
    ('ea317bdb-a76d-50b9-be89-65d5c6683f4f', '5002802012', 'San Francisco del Monte', '50', 'Guaymallén'),
    ('37bd25ed-f201-5222-b175-2bde22cb142a', '5002802013', 'San José', '50', 'Guaymallén'),
    ('9bbe1e8b-6401-5c9c-ad40-0926d91ca07e', '5002802014', 'Villa Nueva', '50', 'Guaymallén'),
    ('ab265cfd-4085-5a68-ae9c-35a3ac21b454', '50028030', 'La Primavera', '50', 'Guaymallén'),
    ('1889a030-085e-5876-ae37-64ba871f22e3', '50028040', 'Los Corralitos', '50', 'Guaymallén'),
    ('04daf857-3c71-5b6b-90ca-9246c1c00799', '50028050', 'Puente de Hierro', '50', 'Guaymallén'),
    ('58cae2e2-93d2-572b-b4d8-ebc1021c3349', '50035010', 'Ingeniero Giagnoni', '50', 'Junín'),
    ('fb598f8a-8fa0-5d20-8515-05661195047c', '50035020', 'Junín', '50', 'Junín'),
    ('5ff42daa-8024-5c26-bc07-66a8f02dc47b', '50035030', 'La Colonia', '50', 'Junín'),
    ('a8104f14-ff9a-546f-b83d-353e60bfa378', '50035040', 'Los Barriales', '50', 'Junín'),
    ('1f7f688b-c9de-5231-8f83-231e8d4f1df6', '50035050', 'Medrano', '50', 'Junín'),
    ('c9c3ba5e-178b-500e-a438-f533ad45585a', '50035060', 'Phillips', '50', 'Junín'),
    ('2976ddc4-f5b9-5c90-a4f2-375c37a05837', '50035070', 'Rodríguez Peña', '50', 'Junín'),
    ('e6b3a015-a06b-5321-ab42-28cdc8fe84c8', '50042010', 'Desaguadero', '50', 'La Paz'),
    ('fd19506d-47c9-50ac-b0ee-6b1a60951288', '50042020', 'La Paz', '50', 'La Paz'),
    ('c92ec561-3f91-5129-81eb-8d92a2e2f9fe', '50042030', 'Villa Antigua', '50', 'La Paz'),
    ('486fe374-6a55-5cdf-bf30-080defe49d5e', '50049010', 'Blanco Encalada', '50', 'Las Heras'),
    ('d9953278-dbfc-5775-85c0-344320b19770', '50049030', 'Jocolí', '50', 'Las Heras'),
    ('35efe8b3-52d9-56b8-919a-cd29cdbbf965', '50049040', 'Las Cuevas', '50', 'Las Heras'),
    ('f7a056a8-3e7c-5c0d-b69a-df9e556e95f2', '50049050', 'Las Heras', '50', 'Las Heras'),
    ('7516afaa-fe4e-5f27-a57d-9ddc88b3e482', '5004905001', 'Capdevila', '50', 'Las Heras'),
    ('5958853f-a898-5c7c-8d5d-4527b4d601cb', '5004905002', 'Ciudad de Las Heras', '50', 'Las Heras'),
    ('90a3fb38-399e-5085-a1f4-696b56e41e9b', '5004905003', 'El Algarrobal', '50', 'Las Heras'),
    ('f4775e3f-4b90-533f-885b-ec9b07e176a2', '5004905004', 'El Borbollón', '50', 'Las Heras'),
    ('e2b90582-ff8b-5efb-bb3e-436b5728d769', '5004905005', 'El Challao', '50', 'Las Heras'),
    ('15b5f669-c44e-54cb-9c17-083975e03037', '5004905007', 'El Plumerillo', '50', 'Las Heras'),
    ('8086c3e9-d55f-5da1-91c8-13914f29d7f7', '5004905008', 'El Resguardo', '50', 'Las Heras'),
    ('d6e52962-4346-5da6-861e-8122bd2374cf', '5004905009', 'El Zapallar', '50', 'Las Heras'),
    ('ac396b57-75a3-57bc-bc27-1a4005999fe9', '5004905010', 'La Cieneguita', '50', 'Las Heras'),
    ('c36879ea-a2f3-51a5-8385-35cf45949d93', '5004905011', 'Panquehuá', '50', 'Las Heras'),
    ('681b14ed-8b2b-583c-b777-820564c6c9c3', '5004905012', 'Sierras de Encalada', '50', 'Las Heras'),
    ('0c243ff3-a22e-5104-996e-2d7f799a7b32', '50049060', 'Los Penitentes', '50', 'Las Heras'),
    ('0e9bf9c1-04dd-5b18-8c07-72d071f36cd5', '50049080', 'Polvaredas', '50', 'Las Heras'),
    ('6919ce62-516f-54fc-91c4-614d62a15f2f', '50049090', 'Puente del Inca', '50', 'Las Heras'),
    ('7c46cbd6-61dc-5734-b077-0f6cf2182f08', '50049100', 'Punta de Vacas', '50', 'Las Heras'),
    ('e32e9e0f-ef83-532b-b35b-e8ecd5fcd57e', '50049110', 'Uspallata', '50', 'Las Heras'),
    ('10de39b4-e84c-5fdd-a2aa-14242b427891', '50056010', 'Barrio Alto del Olvido', '50', 'Lavalle'),
    ('2a5dc2c4-4825-502f-a853-c13e67c8ba99', '50056020', 'Barrio Jocolí II', '50', 'Lavalle'),
    ('07d8b903-3bc6-52fb-b5d0-8d7d408cbf83', '50056030', 'Barrio La Palmera', '50', 'Lavalle'),
    ('5aeea7b0-d073-5b0a-aed7-2e7a005c3191', '50056040', 'Barrio La Pega', '50', 'Lavalle'),
    ('93d56c0b-42e3-5445-8225-61d08de7a43f', '50056050', 'Barrio Lagunas de Bartoluzzi', '50', 'Lavalle'),
    ('ee299fea-53e3-5560-aec5-649878f1dfa8', '50056060', 'Barrio Los Jarilleros', '50', 'Lavalle'),
    ('8c6e5945-3ffc-5f01-80c4-37a3094ff80f', '50056070', 'Barrio Los Olivos', '50', 'Lavalle'),
    ('6af19648-46c2-5537-b3a7-d9f955ba17c6', '50056075', 'Barrio Virgen del Rosario', '50', 'Lavalle'),
    ('4fdeed66-9445-53d7-9b95-e3056f6c0168', '50056080', 'Costa de Araujo', '50', 'Lavalle'),
    ('4ac5995a-b5f2-5bfc-91e9-573fa027e400', '50056090', 'El Paramillo', '50', 'Lavalle'),
    ('8260db3c-f85e-59b2-a2b8-e636f1db21ce', '50056100', 'El Vergel', '50', 'Lavalle'),
    ('14526878-d10d-511c-872a-37fee7218db9', '50056110', 'Ingeniero Gustavo André', '50', 'Lavalle'),
    ('8fb1148b-234f-5e22-945d-cbd0752b761b', '50056120', 'Jocolí', '50', 'Lavalle'),
    ('57f6e89a-dc27-5919-a40f-e603e25892dc', '50056130', 'Jocolí Viejo', '50', 'Lavalle'),
    ('0c0b68a9-44a1-5d77-abff-e327f0f88da1', '50056140', 'Las Violetas', '50', 'Lavalle'),
    ('0ab0e9cb-a4bd-5bdf-b80b-0e6cd5209fbc', '50056150', '3 de Mayo', '50', 'Lavalle'),
    ('d3e538a1-9bb3-5025-a165-955a8191e15d', '50056160', 'Villa Tulumaya', '50', 'Lavalle'),
    ('12ecf5c8-2709-51a0-bdf5-07d067bb51da', '50063010', 'Agrelo', '50', 'Luján de Cuyo'),
    ('ce786053-b101-5e74-b713-86c09c111e48', '50063020', 'Barrio Perdriel IV', '50', 'Luján de Cuyo'),
    ('612be292-3009-58f8-b9f3-e1bd8859992f', '50063030', 'Cacheuta', '50', 'Luján de Cuyo'),
    ('dc4520db-eb5f-5ebc-b169-708ec3039048', '50063040', 'Costa Flores', '50', 'Luján de Cuyo'),
    ('34f548e2-cc1f-5aca-a7d3-c9b11e74a8fc', '50063050', 'El Carrizal', '50', 'Luján de Cuyo'),
    ('f893e2d6-f02a-5c66-a96d-47e7d966e473', '50063060', 'El Salto', '50', 'Luján de Cuyo'),
    ('d5271668-c54f-591e-9387-ba8a999bfcd3', '50063070', 'Las Compuertas', '50', 'Luján de Cuyo'),
    ('85329cf8-77b3-5a7b-b851-f4694a812d09', '50063080', 'Las Vegas', '50', 'Luján de Cuyo'),
    ('93acb425-8552-5a88-a693-8d025a018585', '50063090', 'Luján de Cuyo', '50', 'Luján de Cuyo'),
    ('d6aed3e8-9052-52b3-b4aa-91547e5a953d', '5006309001', 'Carrodilla', '50', 'Luján de Cuyo'),
    ('cd9054d3-c3f6-592d-942b-adae770827c1', '5006309002', 'Chacras de Coria', '50', 'Luján de Cuyo'),
    ('2277228a-f4c4-57a6-a9b1-a48755ce3ff5', '5006309003', 'Ciudad Luján de Cuyo', '50', 'Luján de Cuyo'),
    ('5d925fa5-4337-535f-b590-1d315ac33716', '5006309004', 'La Puntilla', '50', 'Luján de Cuyo'),
    ('8bc7d50b-bef5-51ab-ad46-7dfae3042864', '5006309005', 'Mayor Drummond', '50', 'Luján de Cuyo'),
    ('249f00ef-9b34-5c78-91a4-0f3601c24af8', '5006309006', 'Vistalba', '50', 'Luján de Cuyo'),
    ('6e787863-c278-5bad-9012-3668b6d4bf93', '50063100', 'Perdriel', '50', 'Luján de Cuyo'),
    ('aef535fd-9e50-559c-bc83-d8f2a272d984', '50063110', 'Potrerillos', '50', 'Luján de Cuyo'),
    ('7838b6fc-c23c-515d-a53f-18523705f5d4', '50063120', 'Ugarteche', '50', 'Luján de Cuyo'),
    ('d0ebee29-3fb0-5ded-8935-e04974dd2317', '50070010', 'Barrancas', '50', 'Maipú'),
    ('762d9a4c-ee20-5175-9b00-b30adc2c5760', '50070020', 'Barrio Jesús de Nazaret', '50', 'Maipú'),
    ('b70f4863-5acc-5e3f-8cb3-069f85b78c4d', '50070030', 'Cruz de Piedra', '50', 'Maipú'),
    ('2f54eea7-bfd2-517c-bb9e-1e02ed96e207', '50070040', 'El Pedregal', '50', 'Maipú'),
    ('d578efb4-d7ca-5543-9247-1123db514574', '50070050', 'Fray Luis Beltrán', '50', 'Maipú'),
    ('b0d2fe70-b441-55aa-aea8-96c2beaf34de', '50070060', 'Maipú', '50', 'Maipú'),
    ('a7b59923-da4a-585e-909d-b41d37e8ab8d', '5007006001', 'Ciudad de Maipú', '50', 'Maipú'),
    ('5b80dd1c-67a3-58cb-86b3-d300ba51e95d', '5007006002', 'Coquimbito', '50', 'Maipú'),
    ('6e8f8b0e-0fbc-5e52-ad38-1c821f7b71b9', '5007006003', 'General Gutiérrez', '50', 'Maipú'),
    ('143eb2e4-9c62-599c-be66-7fa121749c4c', '5007006004', 'Luzuriaga', '50', 'Maipú'),
    ('c329791b-3248-546b-9815-9093131e6593', '50070070', 'Rodeo del Medio', '50', 'Maipú'),
    ('bb52d5a7-dda9-5021-ac06-3823870a80f6', '50070090', 'San Roque', '50', 'Maipú'),
    ('2274fa00-4889-5bd2-8f20-5d3abb4570e8', '50070100', 'Villa Teresa', '50', 'Maipú'),
    ('237aadb3-9d85-52dd-9aec-ebc807ea3722', '50077010', 'Agua Escondida', '50', 'Malargüe'),
    ('8a1d60f6-51c4-50e1-9231-b8fb4a8b0c43', '50077030', 'Las Leñas', '50', 'Malargüe'),
    ('21c8e5d9-c350-56c1-8c65-23fb9ff808a9', '50077040', 'Malargüe', '50', 'Malargüe'),
    ('c4eef55b-1ce4-5281-9eec-6e624bc2af4a', '50084010', 'Andrade', '50', 'Rivadavia'),
    ('674228f5-f23e-5911-9cea-2cc0fb436d92', '50084020', 'Barrio Cooperativa Los Campamentos', '50', 'Rivadavia'),
    ('999fb13c-4a48-52db-8c9d-e8244ec67b65', '50084030', 'Barrio Rivadavia', '50', 'Rivadavia'),
    ('4051e4ec-bcc0-5628-b1ee-136039d73e1a', '50084040', 'El Mirador', '50', 'Rivadavia'),
    ('01c01ad3-6ea4-5e55-a583-d6d259105d5a', '50084050', 'La Central', '50', 'Rivadavia'),
    ('1e31dd2f-b908-52cd-8f41-fe6020cd6e65', '50084060', 'La Esperanza', '50', 'Rivadavia'),
    ('d938889b-3c90-5027-8ef2-78eb9ef48ee9', '50084070', 'La Florida', '50', 'Rivadavia'),
    ('0effb73f-5563-566c-8462-2117e95530df', '5008407001', 'Cuadro Ortega', '50', 'Rivadavia'),
    ('ebe9c9c1-f5a0-55cd-a469-1e80517e3d06', '5008407002', 'La Florida', '50', 'Rivadavia'),
    ('ef23b022-76d1-5592-9270-2584c3720aa0', '50084080', 'La Libertad', '50', 'Rivadavia'),
    ('b0ff07e9-7869-5e7f-9558-cea17e0371a0', '50084090', 'Los Árboles', '50', 'Rivadavia'),
    ('599fdacb-bd12-55d7-a87e-56ba099d9b9b', '50084100', 'Los Campamentos', '50', 'Rivadavia'),
    ('6377136b-2f15-59b3-bb07-28ef4d2622ec', '50084110', 'Medrano', '50', 'Rivadavia'),
    ('9bca80fd-5b8f-5747-8ced-8ef1f18e7af9', '50084120', 'Mundo Nuevo', '50', 'Rivadavia'),
    ('6eb0f0b4-bfba-569d-91f5-1629679e7150', '50084130', 'Reducción de Abajo', '50', 'Rivadavia'),
    ('c6b92791-efb9-5e41-9d18-85f18a381e63', '50084140', 'Rivadavia', '50', 'Rivadavia'),
    ('7d32753c-f146-5092-8998-3ac95850a1cc', '50084150', 'Santa María de Oro', '50', 'Rivadavia'),
    ('3c4cfb81-4c97-5b76-b7c0-a51be0c21a02', '50091005', 'Barrio Carrasco', '50', 'San Carlos'),
    ('13fcc19c-bf60-5097-975c-05bd2ef76e92', '50091010', 'Barrio El Cepillo', '50', 'San Carlos'),
    ('db59887f-8757-5af8-ab7e-b65772d42b32', '50091020', 'Chilecito', '50', 'San Carlos'),
    ('82f6d0f9-6e90-5c47-a52d-32a597a06499', '50091030', 'Eugenio Bustos', '50', 'San Carlos'),
    ('43004ffa-2821-57e1-9d8a-734ca26fde17', '50091040', 'La Consulta', '50', 'San Carlos'),
    ('5893a56c-bafe-508a-b92b-34f7cfa2e98b', '50091050', 'Pareditas', '50', 'San Carlos'),
    ('1cc5b263-c3ea-536a-a8ea-b559503c3b5d', '50091060', 'San Carlos', '50', 'San Carlos'),
    ('3600670f-ec47-5d7a-884f-0147dd40a6ea', '50098020', 'Alto Verde', '50', 'San Martín'),
    ('f7795507-8c53-5e06-abf4-25e04659f097', '50098030', 'Barrio Chivilcoy', '50', 'San Martín'),
    ('5fe8db05-7f8d-5414-88df-5c47707fbd84', '50098040', 'Barrio Emanuel', '50', 'San Martín'),
    ('47a6a9a3-3d31-506e-a6bb-90c535d0f918', '50098045', 'Barrio La Estación', '50', 'San Martín'),
    ('f44c0989-1eac-5822-9588-cbf2938506a4', '50098050', 'Barrio Los Charabones', '50', 'San Martín'),
    ('c2035a87-f12f-5e92-a7e7-f5c4a552e169', '50098055', 'Barrio Ntra. Sra. De Fátima', '50', 'San Martín'),
    ('3dadf8d2-f921-50c6-90a7-1470e86a7d27', '50098060', 'Chapanay', '50', 'San Martín'),
    ('a6cbbd99-8aa6-56b2-9512-008261b6e26d', '50098070', 'Chivilcoy', '50', 'San Martín'),
    ('1d2bd01b-72ef-55fc-8d56-244d7b857a9d', '50098073', 'El Espino', '50', 'San Martín'),
    ('8975dd19-5c82-5a8f-a6c3-8bd734226827', '50098077', 'El Ramblón', '50', 'San Martín'),
    ('b72df6fa-516e-5abc-9192-36b82052b6b0', '50098080', 'Montecaseros', '50', 'San Martín'),
    ('f5578c48-5704-504d-aa20-c405838db67e', '50098090', 'Nueva California', '50', 'San Martín'),
    ('e802a61c-aadb-51e5-a821-eabb313ca141', '50098100', 'San Martín', '50', 'San Martín'),
    ('8edff551-5ad4-521c-98bb-e183af8fdc4f', '5009810001', 'Palmira', '50', 'San Martín'),
    ('ee85503a-5050-55eb-b40d-966753f84008', '5009810002', 'San Martín', '50', 'San Martín'),
    ('e42bc768-503a-5118-8428-d5044435c1e5', '50098110', 'Tres Porteñas', '50', 'San Martín'),
    ('f8c8a9b8-0012-57fa-94e7-48db49a9d5f0', '50105020', 'Barrio El Nevado', '50', 'San Rafael'),
    ('845ccf36-1d6e-5f03-968b-cd8635fec026', '50105030', 'Barrio Empleados de Comercio', '50', 'San Rafael'),
    ('0c2469e4-cd47-5fc3-bea0-f21829682337', '50105040', 'Barrio Intendencia', '50', 'San Rafael'),
    ('7549e8e5-cc10-5623-958d-abb7ffeb90f1', '50105050', 'Capitán Montoya', '50', 'San Rafael'),
    ('e7ce06bb-10e6-5e9e-96d2-6592cef2193f', '50105060', 'Cuadro Benegas', '50', 'San Rafael'),
    ('47199a3e-9dd7-57e4-91f0-626adbea9f77', '50105070', 'El Nihuil', '50', 'San Rafael'),
    ('2a6494ec-b9c2-59fc-a4cc-4be6851111ef', '50105080', 'El Sosneado', '50', 'San Rafael'),
    ('097a0b88-aef1-5242-8e4a-333523cb4e7f', '50105090', 'El Tropezón', '50', 'San Rafael'),
    ('d0afeabd-b405-5fe3-8c0a-a834531c2bbf', '50105100', 'Goudge', '50', 'San Rafael'),
    ('4fd3245c-dbee-5386-ab47-b21edc5e6128', '50105110', 'Jaime Prats', '50', 'San Rafael'),
    ('ee60b711-45ae-5be3-a4b7-40cb0c3be84d', '50105120', 'La Llave Nueva', '50', 'San Rafael'),
    ('108f70d4-692f-5157-b0c1-8b82a7b0b134', '50105130', 'Las Malvinas', '50', 'San Rafael'),
    ('10ba69c6-e4d1-5d72-a1ca-ac3d7c1eb672', '50105140', 'Los Reyunos', '50', 'San Rafael'),
    ('83dea32d-824f-5142-8163-40fa041c15bb', '50105150', 'Monte Comán', '50', 'San Rafael'),
    ('0602e9ba-975f-5a3b-a133-7b13e9a18af0', '50105160', 'Pobre Diablo', '50', 'San Rafael'),
    ('74cedffb-9cbb-51ff-b23f-38686e1623d6', '50105170', 'Punta del Agua', '50', 'San Rafael'),
    ('7eeff4d5-a5d4-5cdf-975f-22480896eb19', '50105180', 'Rama Caída', '50', 'San Rafael'),
    ('100c673d-8a3b-5347-9361-a5b2975c0706', '50105190', 'Real del Padre', '50', 'San Rafael'),
    ('877fe41d-5b30-5ad5-a112-eebf73ac60a2', '50105200', 'Salto de las Rosas', '50', 'San Rafael'),
    ('e037f7a0-45db-5813-b599-1b596197189a', '50105210', 'San Rafael', '50', 'San Rafael'),
    ('59fe5753-046f-56e0-a818-c96735d637f2', '5010521001', 'Cuadro Nacional', '50', 'San Rafael'),
    ('8fcd645a-b4cf-5bdb-a5ae-38b350189473', '5010521002', 'San Rafael', '50', 'San Rafael'),
    ('110fd813-8226-5d91-bf2d-9fb4d216f12e', '50105220', '25 de Mayo', '50', 'San Rafael'),
    ('60a896ee-0391-5575-aee7-49806ab52840', '50105230', 'Villa Atuel', '50', 'San Rafael'),
    ('a4d05973-f469-5741-8d8b-86de4e0dbafa', '50105240', 'Villa Atuel Norte', '50', 'San Rafael'),
    ('2fd1eaf9-aef1-576e-9aed-33c3c8520822', '50112010', 'Barrio 12 de Octubre', '50', 'Santa Rosa'),
    ('93964685-5da0-5357-8a6a-2056687a98ec', '50112020', 'Barrio María Auxiliadora', '50', 'Santa Rosa'),
    ('b43dea6b-865f-5408-98bc-96f85bdeec95', '50112030', 'Barrio Molina Cabrera', '50', 'Santa Rosa'),
    ('cf66c9a8-f2eb-55e6-abe7-3aa16fbb0007', '50112040', 'La Dormida', '50', 'Santa Rosa'),
    ('5408aa60-a936-5c6e-9a1b-cf7c51544449', '50112050', 'Las Catitas', '50', 'Santa Rosa'),
    ('93c07e81-b957-528c-ba40-3960e2a03e45', '50112060', 'Santa Rosa', '50', 'Santa Rosa'),
    ('3f9e79f5-e272-5900-b405-35c2a9c4ea2b', '50119010', 'Barrio San Cayetano', '50', 'Tunuyán'),
    ('e8b5eb2b-e3a3-587f-bfe2-51e545890adf', '50119020', 'Campo Los Andes', '50', 'Tunuyán'),
    ('02170f24-bcc7-51a7-a1a4-aae666cdee2c', '50119030', 'Colonia Las Rosas', '50', 'Tunuyán'),
    ('15f540e4-28e6-5f05-995c-d49946ed7567', '50119040', 'El Manzano', '50', 'Tunuyán'),
    ('9ede3a93-911d-5f4e-8ce6-de30e3941e5b', '50119050', 'Los Sauces', '50', 'Tunuyán'),
    ('10889c53-712c-589d-a292-4926c2c7a2b4', '50119060', 'Tunuyán', '50', 'Tunuyán'),
    ('4f3e6ec2-cab4-58a7-9a8d-26789e076a50', '50119070', 'Vista Flores', '50', 'Tunuyán'),
    ('61e46f09-8397-5458-bf4c-96fd9814c107', '50126010', 'Barrio Belgrano Norte', '50', 'Tupungato'),
    ('3b1e6706-1df3-5e9a-a348-1da9b8d27e1d', '50126020', 'Cordón del Plata', '50', 'Tupungato'),
    ('01fdb4ed-5778-5355-b6a9-f1aaaa4a3909', '50126030', 'El Peral', '50', 'Tupungato'),
    ('50d93131-d062-54dc-83d4-63bf91aa9c44', '50126035', 'El Zampal', '50', 'Tupungato'),
    ('06007e61-4a0c-5d19-b207-2c1574f16a96', '50126040', 'La Arboleda', '50', 'Tupungato'),
    ('d7fae9cc-bfef-5ec8-b2ca-045ef399dd83', '50126050', 'San José', '50', 'Tupungato'),
    ('0ff7f5cb-4525-5d19-a364-213831a486e1', '50126060', 'Tupungato', '50', 'Tupungato'),
    ('12a4ee7d-fdf0-5b3d-ab1c-d8ec3bc04961', '5012606001', 'Tupungato', '50', 'Tupungato'),
    ('772d1d94-7ed7-5c00-8e67-13c351a4397a', '5012606002', 'Villa Bastias', '50', 'Tupungato'),
    ('e99692bc-40bb-5550-82ec-a3244edbe020', '54007010', 'Apóstoles', '54', 'Apóstoles'),
    ('2e321077-2943-5d03-8c69-c581b5ad274c', '54007020', 'Azara', '54', 'Apóstoles'),
    ('08300888-763f-5d43-b3b3-4438f743b6ae', '54007025', 'Barrio Rural', '54', 'Apóstoles'),
    ('e73a3e4c-4f1b-5d3a-9b30-4c862c1f2b55', '54007030', 'Estación Apóstoles', '54', 'Apóstoles'),
    ('18ee51d8-8524-56b3-ba91-9f76e15a75b0', '54007040', 'Pindapoy', '54', 'Apóstoles'),
    ('e952bf5f-f000-53af-893e-802b8079421c', '54007050', 'Rincón de Azara', '54', 'Apóstoles'),
    ('65a7957c-3971-59bf-b1ba-03af5f9b9ad5', '54007060', 'San José', '54', 'Apóstoles'),
    ('71fdce87-c3d2-5510-83e4-a99b232f4207', '54007070', 'Tres Capones', '54', 'Apóstoles'),
    ('99b7a22b-7e7b-54ca-9f39-4a5f2edd2502', '54014010', 'Aristóbulo del Valle', '54', 'Cainguás'),
    ('c69573e8-262a-5b2f-b79a-04e9b9adf803', '54014020', 'Campo Grande', '54', 'Cainguás'),
    ('a7b77943-5140-50a3-a699-f64b368aade1', '54014030', 'Dos de Mayo', '54', 'Cainguás'),
    ('ce1df386-b060-5e93-8e9e-c1f3f88e54b4', '5401403001', 'Dos de Mayo Núcleo I', '54', 'Cainguás'),
    ('2f0e46e8-bbfd-5d90-b433-d91eb5564695', '5401403002', 'Dos de Mayo Núcleo II', '54', 'Cainguás'),
    ('bd88683e-5637-5528-b727-f4ce8985a26b', '54014050', 'Dos de Mayo Nucleo III (Bº Bernardino Rivadavia)', '54', 'Cainguás'),
    ('bbaad8a7-c201-5461-99e0-71dfeb3adfa0', '54014055', 'Kilómetro 17', '54', 'Cainguás'),
    ('bc838a39-5a1b-5094-8858-16b184c99b30', '54014060', '1º de Mayo', '54', 'Cainguás'),
    ('0b396d78-2b69-57a1-954d-4b20aba1f411', '54014070', 'Pueblo Illia', '54', 'Cainguás'),
    ('b30d0fcd-1682-5cd4-af9d-14187853f20e', '54014080', 'Salto Encantado', '54', 'Cainguás'),
    ('14d5f3b4-6d95-5133-a81f-e8c71b42f083', '54021005', 'Barrio del Lago', '54', 'Candelaria'),
    ('033b0fb1-db39-5942-8417-a46dbc5f6ea1', '54021010', 'Bonpland', '54', 'Candelaria'),
    ('b498cd5b-4537-519e-aa9a-7a56f5197bf6', '54021020', 'Candelaria', '54', 'Candelaria'),
    ('6756203a-de3f-5c74-ab7d-007a30eb3194', '54021030', 'Cerro Corá', '54', 'Candelaria'),
    ('577f74c0-29d9-5aa5-af78-7bb2492021d4', '54021040', 'Loreto', '54', 'Candelaria'),
    ('884aa2df-4ab3-579c-af38-a826d08c15ec', '54021050', 'Mártires', '54', 'Candelaria'),
    ('c9a920f5-28c8-5904-914b-e325caed7bf4', '54021060', 'Profundidad', '54', 'Candelaria'),
    ('efdeed14-6c08-5913-b7e8-314811ec7fb7', '54021070', 'Puerto Santa Ana', '54', 'Candelaria'),
    ('0b12373d-a29f-5ca2-971d-ae44df9aafd0', '54021080', 'Santa Ana', '54', 'Candelaria'),
    ('705fd296-7559-58bd-a0b1-8662d9dc048a', '54028010', 'Garupá', '54', 'Capital'),
    ('f7bcd78c-80c7-54d0-b7a1-1fea550f17ad', '54028020', 'Nemesio Parma', '54', 'Capital'),
    ('f4bb0af2-43f7-51b6-8481-257e0579e836', '54028030', 'Posadas', '54', 'Capital'),
    ('5f02a815-c70b-537a-aa18-a4d7648f2394', '54028040', 'Posadas (Extensión)', '54', 'Capital'),
    ('2058e490-d638-573d-9337-ca9facd7b6bc', '54035010', 'Barra Concepción', '54', 'Concepción'),
    ('bc279c77-0dba-510a-8809-12254978a241', '54035020', 'Concepción de la Sierra', '54', 'Concepción'),
    ('59b44b61-c8ec-5776-adfb-c261ab69533a', '54035030', 'La Corita', '54', 'Concepción'),
    ('79c95c4b-da0e-5462-b854-bffe6eb6f87e', '54035040', 'Santa María', '54', 'Concepción'),
    ('9feda6bf-8dd5-55ef-9d6d-2878c281a865', '54042010', 'Colonia Victoria', '54', 'Eldorado'),
    ('589f757d-9e33-5522-b47f-102fdd25e554', '54042020', 'Eldorado', '54', 'Eldorado'),
    ('e814b8e8-3201-52ac-82df-98cda08e9098', '54042030', 'María Magdalena', '54', 'Eldorado'),
    ('c2f76ad0-39d2-58c1-a0a8-77c83ff59b03', '54042035', 'Nueva Delicia', '54', 'Eldorado'),
    ('2a5d1613-0bdf-5a36-ac06-df2efb4f7161', '54042040', '9 de Julio Kilómetro 28', '54', 'Eldorado'),
    ('7cbf32d4-9ca2-5f38-8870-b1dd4258af42', '54042050', '9 de Julio Kilómetro 20', '54', 'Eldorado'),
    ('a3f16d41-758e-5a57-b69b-e8a4b061f7c8', '54042055', 'Pueblo Nuevo', '54', 'Eldorado'),
    ('3d380026-1e07-519e-916c-772566fc75a4', '54042060', 'Puerto Mado', '54', 'Eldorado'),
    ('8e3a5a49-a341-5090-a630-88caf1a24bf0', '54042070', 'Puerto Pinares', '54', 'Eldorado'),
    ('36b3e38f-6d38-5b33-abab-b2c719b9c2f5', '54042080', 'Santiago de Liniers', '54', 'Eldorado'),
    ('761ce83f-9c58-508c-a0d0-515a581de89d', '54042090', 'Valle Hermoso', '54', 'Eldorado'),
    ('c8dcdd6d-4575-5748-ba8a-766c865be438', '54042100', 'Villa Roulet', '54', 'Eldorado'),
    ('0099c689-d082-5e8d-a7b0-04a373f45cea', '54049010', 'Comandante Andresito', '54', 'General Manuel Belgrano'),
    ('236c365e-5ceb-5856-97ab-7c3b0749761e', '54049020', 'Bernardo de Irigoyen', '54', 'General Manuel Belgrano'),
    ('49d474b1-64a1-5500-953b-5907c8733e3a', '54049025', 'Caburei', '54', 'General Manuel Belgrano'),
    ('5169742a-c1d6-5efd-8ea5-c5ee2c1275b7', '54049030', 'Dos Hermanas', '54', 'General Manuel Belgrano'),
    ('5bfd7459-5708-53c6-85d5-c3f145d3b4d6', '54049040', 'Integración', '54', 'General Manuel Belgrano'),
    ('adb373f8-9753-560b-81fb-ddeec9e04eac', '54049043', 'Piñalito Norte', '54', 'General Manuel Belgrano'),
    ('c91ec414-08c5-549a-b199-d582d2ff6cb0', '54049045', 'Puerto Andresito', '54', 'General Manuel Belgrano'),
    ('e3269501-0097-5066-9287-d359cc750745', '54049047', 'Puerto Deseado', '54', 'General Manuel Belgrano'),
    ('a175ddc8-3f74-5dbf-8712-f19d4bdbc6bb', '54049050', 'San Antonio', '54', 'General Manuel Belgrano'),
    ('bda9ff5d-3813-50ab-8c86-f50e6307d751', '54056010', 'El Soberbio', '54', 'Guaraní'),
    ('da865c5d-f3b3-5e86-9e1b-d4bab99e3f64', '54056020', 'Fracrán', '54', 'Guaraní'),
    ('7762daa5-51a2-56d1-be79-83165c18bb81', '54056030', 'San Vicente', '54', 'Guaraní'),
    ('f2e4a2ce-427e-5a0c-9d9e-f1a1733c8007', '54063010', 'Puerto Esperanza', '54', 'Iguazú'),
    ('cf4f07d6-b35c-51f9-a721-34fe7dac6e6b', '54063020', 'Puerto Libertad', '54', 'Iguazú'),
    ('2415bc9f-2a1b-5fd7-9489-1cdc71aa1799', '54063030', 'Puerto Iguazú', '54', 'Iguazú'),
    ('5152bf1d-e926-58ff-9f41-a9c45f169659', '54063035', 'Villa Cooperativa', '54', 'Iguazú'),
    ('e111bb46-93b0-510b-8cae-f67136d64c66', '54063040', 'Colonia Wanda', '54', 'Iguazú'),
    ('88557a48-f387-56b1-bf91-bc72280d390d', '54070010', 'Almafuerte', '54', 'Leandro N. Alem'),
    ('2ecf02de-1f0f-5adb-ae17-a156197c25f3', '54070020', 'Arroyo del Medio', '54', 'Leandro N. Alem'),
    ('5fa2fcf6-e247-517c-b512-b33db366b89e', '54070030', 'Caá - Yarí', '54', 'Leandro N. Alem'),
    ('a611ca13-d5ce-5493-bd32-b44a1165f612', '54070040', 'Cerro Azul', '54', 'Leandro N. Alem'),
    ('bb6b009e-d27b-5c3a-991b-2cc64b31a4c3', '54070050', 'Dos Arroyos', '54', 'Leandro N. Alem'),
    ('e3cd6714-73dc-528b-81d5-5f92f6d861d5', '54070060', 'Gobernador López', '54', 'Leandro N. Alem'),
    ('cb65595a-634c-5848-8217-70786a4920b5', '54070070', 'Leandro N. Alem', '54', 'Leandro N. Alem'),
    ('6206bbe5-f9d2-5d6c-8bee-5262e5e8b9f4', '54070080', 'Olegario V. Andrade', '54', 'Leandro N. Alem'),
    ('c5045ace-0073-519b-842d-c4af79482c0f', '54070090', 'Villa Libertad', '54', 'Leandro N. Alem'),
    ('05771cbd-1bcc-5935-ba68-aa236f195201', '54077010', 'Capioví', '54', 'Libertador General San Martín'),
    ('b9a08634-1588-59cb-879a-d73938bab3fd', '54077015', 'Capioviciño', '54', 'Libertador General San Martín'),
    ('8c9469c7-0549-572c-8122-a6b0739e2288', '54077020', 'El Alcázar', '54', 'Libertador General San Martín'),
    ('336eab1e-8940-551b-856c-4b0a3649153e', '54077030', 'Garuhapé', '54', 'Libertador General San Martín'),
    ('43067dc0-7f77-52dc-a413-4de0eb4dee9c', '54077040', 'Mbopicuá', '54', 'Libertador General San Martín'),
    ('5446f815-71e0-5772-adc9-f50ef3acc901', '54077050', 'Puerto Leoni', '54', 'Libertador General San Martín'),
    ('053caab6-2626-505a-957c-e24380b852b2', '54077060', 'Puerto Rico', '54', 'Libertador General San Martín'),
    ('ba93e087-2000-5cbb-90ac-b0f80ec8c89a', '54077070', 'Ruiz de Montoya', '54', 'Libertador General San Martín'),
    ('8d556ef5-4ed9-520b-8105-f53e0ff0ff0e', '54077080', 'San Alberto', '54', 'Libertador General San Martín'),
    ('6aaebb54-2ea5-50d6-a64f-6920775cc940', '54077090', 'San Gotardo', '54', 'Libertador General San Martín'),
    ('47cb3c58-01ad-5f57-9a2e-dc6a78ba43d5', '54077100', 'San Miguel', '54', 'Libertador General San Martín'),
    ('ab51be65-48c8-530e-8fc5-939e604d412e', '54077110', 'Villa Akerman', '54', 'Libertador General San Martín'),
    ('d75db896-4d58-538c-b389-a08b8f9a3cf0', '54077120', 'Villa Urrutia', '54', 'Libertador General San Martín'),
    ('ff83d877-9526-5c9d-958a-f635d6e03333', '54084003', 'Barrio Cuatro Bocas', '54', 'Montecarlo'),
    ('e8d6c538-f610-575d-b3ec-469b8d16c253', '54084005', 'Barrio Guatambu', '54', 'Montecarlo'),
    ('360ba104-7cc1-50c4-a616-dd38c7de079f', '54084007', 'Bario Ita', '54', 'Montecarlo'),
    ('0ae2aea6-61c9-52e7-82e9-7927d8939fe8', '54084010', 'Caraguatay', '54', 'Montecarlo'),
    ('1134a725-30d1-5a23-babb-2faf8489950e', '54084020', 'Laharrague', '54', 'Montecarlo'),
    ('3e4705dd-ec49-5451-8b81-8eeeccf54b40', '54084030', 'Montecarlo', '54', 'Montecarlo'),
    ('3e780f25-0d08-53ae-8e3f-483814ae7df5', '54084040', 'Piray Kilómetro 18', '54', 'Montecarlo'),
    ('b5dd8535-90af-5a68-9a3a-75c3e0e1b6a8', '54084050', 'Puerto Piray', '54', 'Montecarlo'),
    ('aad4774f-fa53-5fde-ae57-0e94d26f1b44', '54084060', 'Tarumá', '54', 'Montecarlo'),
    ('e1ca9e63-cbe6-5c1b-b905-e23f6462bfe3', '54084070', 'Villa Parodi', '54', 'Montecarlo'),
    ('97360dbe-6d89-53ed-a0c4-18dd012f0d12', '54091010', 'Colonia Alberdi', '54', 'Oberá'),
    ('58914a2c-415c-5bad-a605-57faf50f6f55', '54091013', 'Barrio Escuela 461', '54', 'Oberá'),
    ('ed3a5615-75b2-53e1-bc27-cc41dec7cbd0', '54091017', 'Barrio Escuela 633', '54', 'Oberá'),
    ('3c88c00f-3956-5a83-be6a-5445732d6efc', '54091020', 'Campo Ramón', '54', 'Oberá'),
    ('324fb2a2-8061-55b9-a102-4e6d0c003559', '54091030', 'Campo Viera', '54', 'Oberá'),
    ('c0388cb0-155f-53c4-8e08-1095a87b3f3f', '54091040', 'El Salto', '54', 'Oberá'),
    ('e6eed685-140b-50fa-9b8e-f00aad2665a1', '54091050', 'General Alvear', '54', 'Oberá'),
    ('ffefb354-6cee-5874-9e06-45435f2a1c87', '54091060', 'Guaraní', '54', 'Oberá'),
    ('ffe440e4-2506-58ca-82ee-2c1f26d10c87', '54091070', 'Los Helechos', '54', 'Oberá'),
    ('caf2b534-6a87-5772-8394-4adbe3028690', '54091080', 'Oberá', '54', 'Oberá'),
    ('c337a543-a793-5032-b854-63d243696cd3', '54091090', 'Panambí', '54', 'Oberá'),
    ('dab59289-30fe-54be-b26b-dc84472fe4c5', '54091100', 'Panambí Kilómetro 8', '54', 'Oberá'),
    ('90c6d97e-02e2-550c-969d-f420e5ccb878', '54091105', 'Panambi Kilómetro 15', '54', 'Oberá'),
    ('ea520362-6c2a-52ae-93c2-5a22d25b6ab5', '54091110', 'San Martín', '54', 'Oberá'),
    ('09a82b85-3837-5fea-ab84-2445baee87e9', '54091120', 'Villa Bonita', '54', 'Oberá'),
    ('b54d9c4a-d1c1-5aa0-bf9f-d0cc806d67ad', '54098005', 'Barrio Tungoil', '54', 'San Ignacio'),
    ('326d7843-d57d-5b67-878e-8d12809ace9a', '54098010', 'Colonia Polana', '54', 'San Ignacio'),
    ('f5960995-3508-5391-9a4e-bc32900cfc57', '54098020', 'Corpus', '54', 'San Ignacio'),
    ('c2b7dd4c-357f-57fe-919b-4fbb2fa38a8d', '54098030', 'Domingo Savio', '54', 'San Ignacio'),
    ('bdc5bd92-8492-503b-b41c-83006ef04511', '54098040', 'General Urquiza', '54', 'San Ignacio')
ON CONFLICT (indec_id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('8d7a9158-862a-5eb4-ae63-71824c8592a8', '54098050', 'Gobernador Roca', '54', 'San Ignacio'),
    ('81621ea2-311a-5371-82ee-aba70edcfe86', '54098060', 'Helvecia', '54', 'San Ignacio'),
    ('3b55b67b-d6a0-5c0d-8fb1-180e5abd8649', '54098070', 'Hipólito Yrigoyen', '54', 'San Ignacio'),
    ('838b1f49-a502-5adb-a4fb-a7a49668af30', '54098080', 'Jardín América', '54', 'San Ignacio'),
    ('185a49dd-2598-59c6-83a0-408efaedd3bc', '54098090', 'Oasis', '54', 'San Ignacio'),
    ('cbea5973-a0c6-5bec-9f43-a78ddf4f671e', '54098100', 'Roca Chica', '54', 'San Ignacio'),
    ('87f71650-bcfb-5cd9-8efc-213b1b30b115', '54098110', 'San Ignacio', '54', 'San Ignacio'),
    ('a4e76a00-d901-5988-aae0-58908dea1c80', '54098120', 'Santo Pipó', '54', 'San Ignacio'),
    ('f12bfb69-69b7-59ab-8c3d-a360b30f469d', '54105010', 'Florentino Ameghino', '54', 'San Javier'),
    ('690c372a-b66c-5af2-9434-298150f196cd', '54105020', 'Itacaruaré', '54', 'San Javier'),
    ('57a8a7fa-0684-5842-a386-4b082c0e373a', '54105030', 'Mojón Grande', '54', 'San Javier'),
    ('2e42a3b5-8b95-575f-9529-d85708675d55', '54105040', 'San Javier', '54', 'San Javier'),
    ('ff3d59fa-46c1-5c0e-87da-29b12599d2f9', '54112010', 'Cruce Caballero', '54', 'San Pedro'),
    ('ca05f10b-3ca9-5e0a-953c-0faed0d19829', '54112020', 'Paraíso', '54', 'San Pedro'),
    ('22105f47-c80a-5a76-a8a3-2e77117f2b49', '54112030', 'Piñalito Sur', '54', 'San Pedro'),
    ('08dbad0f-f3e9-5b4b-92ed-bcaf66a0f42f', '54112040', 'San Pedro', '54', 'San Pedro'),
    ('3c52a186-fcf6-5dfd-9d5f-358bbb74538e', '54112050', 'Tobuna', '54', 'San Pedro'),
    ('7693b26a-0f31-529f-8593-d22a08d4bdab', '54119010', 'Alba Posse', '54', '25 de Mayo'),
    ('69fb6ee8-03ec-5a33-b7a0-67efcce09818', '54119020', 'Alicia Alta', '54', '25 de Mayo'),
    ('a47f7cf9-c3a9-5ff6-a044-aa9d33506de5', '54119025', 'Alicia Baja', '54', '25 de Mayo'),
    ('eb381bc0-733b-55de-ba38-1a936e9ecfc2', '54119030', 'Colonia Aurora', '54', '25 de Mayo'),
    ('189fecc0-d870-59cf-98c8-a0ff7a71ecff', '54119040', 'San Francisco de Asís', '54', '25 de Mayo'),
    ('38f97f26-1b3f-5467-a304-91df450741df', '54119050', 'Santa Rita', '54', '25 de Mayo'),
    ('1ba8e0c7-9bd5-5002-865e-3412a4023021', '54119060', '25 de Mayo', '54', '25 de Mayo'),
    ('d30ef6de-a257-5980-ba2b-6886ebfec511', '58007010', 'Aluminé', '58', 'Aluminé'),
    ('ef4aeba8-a796-555b-9317-e87b68a24b1d', '58007015', 'Moquehue', '58', 'Aluminé'),
    ('984fc4c4-7f0c-5ba1-9e10-6fec585688e6', '58007020', 'Villa Pehuenia', '58', 'Aluminé'),
    ('555fec0b-b075-51e8-868d-c6b2cf603e95', '58014005', 'Aguada San Roque', '58', 'Añelo'),
    ('c5067892-6ef9-5a61-80a6-3ea742b688ac', '58014010', 'Añelo', '58', 'Añelo'),
    ('a19a16b4-b1a7-5557-9735-de30d92c67bb', '58014020', 'San Patricio del Chañar', '58', 'Añelo'),
    ('78f77e60-54d0-5300-8e53-22eaee8916c0', '58021010', 'Las Coloradas', '58', 'Catán Lil'),
    ('6545d554-e7a2-51d7-a20d-51836d07ff92', '58028010', 'Piedra del Águila', '58', 'Collón Curá'),
    ('0119d376-a1cf-5b3d-892a-cea41cabf70a', '58028020', 'Santo Tomás', '58', 'Collón Curá'),
    ('a06524c8-6f63-51ee-b5b7-9215fe56c6a3', '58035010', 'Arroyito', '58', 'Confluencia'),
    ('ff26eff6-363f-512a-a5ae-fe21bf7402f5', '58035030', 'Centenario', '58', 'Confluencia'),
    ('a1b1d66e-5ad1-5d06-84fc-8488de145be5', '58035040', 'Cutral Có', '58', 'Confluencia'),
    ('2d3e8c19-a36c-585f-b488-7715dfd547c9', '58035060', 'Mari Menuco', '58', 'Confluencia'),
    ('5ea94962-f89f-5d70-9c4f-4cfd8676cfff', '58035070', 'Neuquén', '58', 'Confluencia'),
    ('ab84d6fd-7b37-56de-9173-f28198f7b8ba', '58035090', 'Plaza Huincul', '58', 'Confluencia'),
    ('01b27084-4116-581f-88e2-ffb60740ba62', '58035100', 'Plottier', '58', 'Confluencia'),
    ('77db8450-99db-5b66-b2a9-a1f00cf359af', '58035110', 'Senillosa', '58', 'Confluencia'),
    ('efbd96a3-b863-5381-bfc9-b330930ebbfb', '58035120', 'Villa El Chocón', '58', 'Confluencia'),
    ('18b12fe1-29d1-5567-83b2-78ebebef7a2c', '58035130', 'Vista Alegre Norte', '58', 'Confluencia'),
    ('b4951056-8de1-5537-97ab-0c54a0826534', '58035140', 'Vista Alegre Sur', '58', 'Confluencia'),
    ('8a3f7778-8946-5583-a3d9-8959b0cbd629', '58042010', 'Chos Malal', '58', 'Chos Malal'),
    ('92eb8449-bfd4-5825-8731-59d7345a6496', '58042020', 'Tricao Malal', '58', 'Chos Malal'),
    ('a5ad4f21-842f-51f3-bf5f-37860b82c11a', '58042030', 'Villa del Curi Leuvú', '58', 'Chos Malal'),
    ('3f722d69-0b88-5f04-b8b3-6a5af7bb724e', '58049010', 'Junín de los Andes', '58', 'Huiliches'),
    ('5ddbe4b2-e7e5-5862-8032-84c2a49c5ec0', '58056010', 'San Martín de los Andes', '58', 'Lácar'),
    ('3ab01b78-3bd2-59a0-bb59-b4aaedf245db', '58056020', 'Villa Lago Meliquina', '58', 'Lácar'),
    ('d6bf566c-b1e9-5ca3-8dc6-494b171b1fed', '58063010', 'Chorriaca', '58', 'Loncopué'),
    ('17962634-0d5c-5973-831c-8cce9ab80caf', '58063020', 'Loncopué', '58', 'Loncopué'),
    ('79340262-570d-572b-ac44-79abc2982a1d', '58070010', 'Villa La Angostura', '58', 'Los Lagos'),
    ('3baedd64-fbcc-58f9-9dbd-bacea1e8046e', '58070020', 'Villa Traful', '58', 'Los Lagos'),
    ('e01d7e07-df14-5c32-8457-e1a8504f4940', '58077010', 'Andacollo', '58', 'Minas'),
    ('588fe418-fc74-5eb2-a98e-6114635f3b66', '58077020', 'Huinganco', '58', 'Minas'),
    ('df34bd48-8214-5b23-956a-2930239a85e5', '58077030', 'Las Ovejas', '58', 'Minas'),
    ('fa46b76e-db85-5533-8c6c-ed7f5eea476e', '58077040', 'Los Miches', '58', 'Minas'),
    ('5c5f7b9e-37e8-51e4-a3f6-0d82ee2bc2bb', '58077050', 'Manzano Amargo', '58', 'Minas'),
    ('866bd147-dafd-5664-b09b-b7f0d964a473', '58077060', 'Varvarco', '58', 'Minas'),
    ('ec8e5724-d62b-5df3-9574-1b7b69828eb8', '58077070', 'Villa del Nahueve', '58', 'Minas'),
    ('0211c28c-8365-5e2a-ab60-67233e598605', '58084010', 'Caviahue', '58', 'Ñorquín'),
    ('0aeb56cd-acbe-5ae2-a95c-ecefffff63a2', '58084020', 'Copahue', '58', 'Ñorquín'),
    ('278d3894-89c2-5ca5-a8f8-1b609877fd97', '58084030', 'El Cholar', '58', 'Ñorquín'),
    ('3aa151c0-7fef-5a26-a57d-6e3873167af5', '58084040', 'El Huecú', '58', 'Ñorquín'),
    ('c0cbe1c8-2c46-59d9-bce8-3bae291bd878', '58084050', 'Taquimilán', '58', 'Ñorquín'),
    ('bd316774-0efe-53f8-925d-cde8d45278ea', '58091010', 'Barrancas', '58', 'Pehuenches'),
    ('b217076c-b123-560b-9abd-8395bb29b498', '58091020', 'Buta Ranquil', '58', 'Pehuenches'),
    ('6bfeced1-89e3-587f-b105-649c7108328e', '58091030', 'Octavio Pico', '58', 'Pehuenches'),
    ('015f5785-6e45-57cb-b2ee-eab71197c225', '58091040', 'Rincón de los Sauces', '58', 'Pehuenches'),
    ('23d07c69-9a2d-5437-ae17-aec825032dde', '58098005', 'El Sauce', '58', 'Picún Leufú'),
    ('b1568c64-5d12-5ebf-bf70-5bb2e8a89000', '58098010', 'Paso Aguerre', '58', 'Picún Leufú'),
    ('95c53e95-997c-5077-b4b4-9ad433d4ab60', '58098020', 'Picún Leufú', '58', 'Picún Leufú'),
    ('802d51a4-4344-5673-9226-731f0ee23300', '58105010', 'Bajada del Agrio', '58', 'Picunches'),
    ('da3f5ed8-5abf-5c27-80ee-8423a6d745f8', '58105020', 'La Buitrera', '58', 'Picunches'),
    ('af348d4f-de06-5959-8958-0d69904dd3c8', '58105030', 'Las Lajas', '58', 'Picunches'),
    ('1568a917-7bae-5f42-8dfd-b1e046bfce30', '58105040', 'Quili Malal', '58', 'Picunches'),
    ('2ba52c0c-527c-51f9-8e42-6f48cc711458', '58112010', 'Los Catutos', '58', 'Zapala'),
    ('3f948c7d-7893-54e4-92bd-a8b3cfc1ce7a', '58112020', 'Mariano Moreno', '58', 'Zapala'),
    ('b387aee0-a8e4-5bfe-b0ce-b17041b3e885', '5811202001', 'Covunco Centro', '58', 'Zapala'),
    ('bbda1f9a-5859-538d-aadb-ea196a31dd56', '5811202002', 'Mariano Moreno', '58', 'Zapala'),
    ('7e48bcd0-4e1a-5672-961f-67d95475d75d', '58112030', 'Ramón M. Castro', '58', 'Zapala'),
    ('2ec83f71-f428-59dc-8914-5742792635c4', '58112040', 'Zapala', '58', 'Zapala'),
    ('d8cd60a2-f24b-5ff8-b930-473061e29fb0', '62007010', 'Bahía Creek', '62', 'Adolfo Alsina'),
    ('ce5a9c87-10b8-58dc-b3ea-f899719f5f7d', '62007020', 'El Cóndor', '62', 'Adolfo Alsina'),
    ('ea0b846a-2c2e-581c-ab59-225dd083ad70', '62007030', 'El Juncal', '62', 'Adolfo Alsina'),
    ('3a7be07e-f83d-579f-abab-c006c0a81cb5', '62007040', 'Guardia Mitre', '62', 'Adolfo Alsina'),
    ('50698dd5-99d7-5188-9232-8fbf76cf6a3d', '62007050', 'La Lobería', '62', 'Adolfo Alsina'),
    ('348e879a-2137-5f7a-8ada-cdcb1b431367', '62007060', 'Loteo Costa de Río', '62', 'Adolfo Alsina'),
    ('6eef55d6-9ddf-5d38-adff-ff9e3a75fb76', '62007070', 'Pozo Salado', '62', 'Adolfo Alsina'),
    ('53bc100f-757f-5a65-986b-5d332af3fc1c', '62007080', 'San Javier', '62', 'Adolfo Alsina'),
    ('c870b197-e560-56fd-b44c-f265bea8a229', '62007090', 'Viedma', '62', 'Adolfo Alsina'),
    ('59ee016b-a451-50aa-ae57-79b339e4b02a', '62014010', 'Barrio Unión', '62', 'Avellaneda'),
    ('7e303f0a-a71e-540e-a5c4-04b8381a97ff', '62014020', 'Chelforó', '62', 'Avellaneda'),
    ('4a6e7651-52fb-55eb-be91-cd05064617aa', '62014030', 'Chimpay', '62', 'Avellaneda'),
    ('2009fc8f-166a-5054-b215-93fbc71f2a06', '62014040', 'Choele Choel', '62', 'Avellaneda'),
    ('53619b11-b647-5769-9492-ad81010e0928', '62014050', 'Coronel Belisle', '62', 'Avellaneda'),
    ('6e22ce04-4a2a-5970-beae-ac44a5294f6d', '62014060', 'Darwin', '62', 'Avellaneda'),
    ('68aa299b-1e6d-525e-a868-7127828c6028', '62014070', 'Lamarque', '62', 'Avellaneda'),
    ('fc24c7d2-559a-5145-afdf-d97bf994f965', '62014080', 'Luis Beltrán', '62', 'Avellaneda'),
    ('9c475f23-038e-5861-894a-fb26301818b6', '62014090', 'Pomona', '62', 'Avellaneda'),
    ('3b4155d6-ebf0-5346-8e07-9e100e7ccbc7', '62021020', 'Colonia Suiza', '62', 'Bariloche'),
    ('ce1e8bed-0177-5a1b-a414-d0e718922a59', '62021030', 'El Bolsón', '62', 'Bariloche'),
    ('b62e9c69-b73b-5e92-a1bb-8d325788322f', '62021040', 'El Foyel', '62', 'Bariloche'),
    ('cd2a6490-8ce1-51c7-9703-1f1776851cdb', '62021047', 'Mallín Ahogado', '62', 'Bariloche'),
    ('f6e478e6-cb18-57ae-8b22-de14dbaf21a6', '62021050', 'Río Villegas', '62', 'Bariloche'),
    ('9b6e5a38-3cf2-5815-9e86-e7d981e05da4', '62021060', 'San Carlos de Bariloche', '62', 'Bariloche'),
    ('3e43338e-d262-533e-b55d-6dc5303720d2', '6202106001', 'San Carlos de Bariloche', '62', 'Bariloche'),
    ('1ef1b837-cd8b-5d35-90c3-84adb5281c77', '6202106002', 'Villa Campanario', '62', 'Bariloche'),
    ('5c51685b-a36a-58e9-9a71-50e10901f0e2', '6202106003', 'Villa Llao Llao', '62', 'Bariloche'),
    ('68dbb100-e59f-51fa-b280-2f4ef54a5d3b', '62021080', 'Villa Catedral', '62', 'Bariloche'),
    ('2f0ba461-40f1-532d-a061-08cc80598a11', '62021110', 'Villa Mascardi', '62', 'Bariloche'),
    ('9f79a2eb-78c9-58fa-9470-0fcfed9687b6', '62028010', 'Barrio Colonia Conesa', '62', 'Conesa'),
    ('b2835903-0bad-5303-959a-e49d2a09cba8', '62028020', 'General Conesa', '62', 'Conesa'),
    ('683ab0d5-f158-5eb8-8429-0b653637b7ff', '62028030', 'Barrio Planta Compresora de Gas', '62', 'Conesa'),
    ('a03ada06-555a-5c3f-88fb-0016a4aba486', '62035010', 'Aguada Guzmán', '62', 'El Cuy'),
    ('7a40d926-31e2-511a-81cd-991d43f78496', '62035020', 'Cerro Policía', '62', 'El Cuy'),
    ('8d9df5b9-a648-5e9b-adb4-eae99c6b6820', '62035030', 'El Cuy', '62', 'El Cuy'),
    ('1d46ec4c-75ac-5134-b06d-903bd9c5aa14', '62035040', 'Las Perlas', '62', 'El Cuy'),
    ('1d58aa29-8643-50ad-b325-c3b801e407e1', '62035050', 'Mencué', '62', 'El Cuy'),
    ('0a5f494e-3a18-5e9d-a192-2f1ac0f66785', '62035060', 'Naupa Huen', '62', 'El Cuy'),
    ('49020e31-bc51-5cfd-a996-8d2eba4f1c9a', '62035070', 'Paso Córdova', '62', 'El Cuy'),
    ('0bca0faa-e8d4-5868-9130-db32389b99ce', '62035080', 'Valle Azul', '62', 'El Cuy'),
    ('578e3695-d67d-5aed-846b-23ecdfec76d0', '62042010', 'Allen', '62', 'General Roca'),
    ('f9998079-e85e-5bc7-82b1-c8fb4ab8f93d', '62042020', 'Paraje Arroyón (Bajo San Cayetano)', '62', 'General Roca'),
    ('0b9e9895-f73b-5737-90db-c7734df8bad1', '62042030', 'Barda del Medio', '62', 'General Roca'),
    ('d53e8783-7a96-538a-8a7d-b90d2e18b8bd', '62042040', 'Barrio Blanco', '62', 'General Roca'),
    ('6225698b-7c3d-5f19-9230-57c9d78aa6cc', '62042050', 'Barrio Calle Ciega Nº 10', '62', 'General Roca'),
    ('036aad90-c171-5c20-a68a-73a4f702ea6c', '62042060', 'Barrio Calle Ciega Nº 6', '62', 'General Roca'),
    ('5dcacc68-7306-5bcf-9153-12e7b086d5a0', '62042070', 'Barrio Canale', '62', 'General Roca'),
    ('a1b8ca51-1845-5a14-ad3f-293bf7647a86', '62042080', 'Barrio Chacra Monte', '62', 'General Roca'),
    ('e36f469e-b258-5e3f-b619-7047d89c5c4a', '62042090', 'Barrio Costa Este', '62', 'General Roca'),
    ('7314e24f-d53f-5cba-8237-9753c50e886a', '62042110', 'Barrio Costa Oeste', '62', 'General Roca'),
    ('60b1b9c3-4857-528a-9003-9ab446aacee9', '62042115', 'Barrio Destacamento', '62', 'General Roca'),
    ('54533771-f906-50e1-9df8-2aa076eced0c', '62042120', 'Barrio El Labrador', '62', 'General Roca'),
    ('12e334b1-319d-57b5-accd-8c38bfdad46a', '62042130', 'Barrio El Maruchito', '62', 'General Roca'),
    ('7ae09c46-08f1-5c98-8e92-ae174f469cc7', '62042140', 'Barrio El Petróleo', '62', 'General Roca'),
    ('e9571220-af74-5a2b-b748-d71d0b67b457', '62042143', 'Barrio Emergente', '62', 'General Roca'),
    ('7825954b-7ec6-50f6-bcda-c983c2d9b455', '62042147', 'Barrio Fátima', '62', 'General Roca'),
    ('ea4c41d6-e5c7-597a-8125-e8922aec3dae', '62042150', 'Barrio Frontera', '62', 'General Roca'),
    ('b5d86fff-e158-5ffe-a526-000b917c8812', '62042160', 'Barrio Guerrico', '62', 'General Roca'),
    ('4bd659e4-0cf7-5e7c-8a7b-a139937e15ed', '62042170', 'Barrio Isla 10', '62', 'General Roca'),
    ('6509a2c6-d625-5efa-8539-bde4bec050c9', '62042180', 'Barrio La Barda', '62', 'General Roca'),
    ('28815551-8bab-5152-9bf0-5423d75bba0c', '62042200', 'Barrio La Costa', '62', 'General Roca'),
    ('d3d0f7d2-f2c5-52c1-a0bb-1228efcce3d3', '62042210', 'Barrio La Defensa', '62', 'General Roca'),
    ('142137ba-274c-5937-8cc1-dec97892e007', '62042215', 'Barrio La Herradura', '62', 'General Roca'),
    ('373363c0-77af-5de5-9637-0341bb9bc751', '62042240', 'Puente Cero', '62', 'General Roca'),
    ('d54bcbbc-fc34-5e96-bcef-8a28b4be59bb', '62042245', 'Barrio Luisillo', '62', 'General Roca'),
    ('c4f5da2b-c3da-5ffb-b0a6-1e796033abf6', '62042250', 'Barrio Mar del Plata', '62', 'General Roca'),
    ('7280cf9e-fee5-5215-9424-2f921bfc2efb', '62042260', 'Barrio María Elvira', '62', 'General Roca'),
    ('0d666246-dd31-55f2-b4de-bc6dcb66d97f', '62042265', 'Barrio Moño Azul', '62', 'General Roca'),
    ('6ef426f8-3412-5527-9807-07957e9676cb', '62042280', 'Barrio Norte', '62', 'General Roca'),
    ('7d73d657-8bc3-5624-8071-fbd51399bc08', '62042297', 'Barrio Pinar', '62', 'General Roca'),
    ('f9c87716-ffe5-5a10-81b1-824aecedc821', '62042310', 'Barrio Porvenir', '62', 'General Roca'),
    ('8d2a48fe-0c6f-59bc-b692-9ef28e25cc30', '62042335', 'Barrio Santa Lucia', '62', 'General Roca'),
    ('1214a91a-dc5c-52fb-aa32-027e3d9b7ead', '62042340', 'Barrio Santa Rita', '62', 'General Roca'),
    ('781021bb-4316-5b2e-9782-7958eb6f28df', '62042350', 'Barrio Unión', '62', 'General Roca'),
    ('0abcbb96-05e8-552e-a527-72de8d752dbf', '62042360', 'Catriel', '62', 'General Roca'),
    ('b3ebea8d-e2fa-5c6a-809d-e70ba2778353', '62042370', 'Cervantes', '62', 'General Roca'),
    ('afc2324a-d65a-53d1-bc3a-fd8ae8ff3294', '62042380', 'Chichinales', '62', 'General Roca'),
    ('04760805-ad98-582e-9d86-61297091bb2d', '62042390', 'Cinco Saltos', '62', 'General Roca'),
    ('7e30c579-68a6-5a2a-84a5-cd58fb22f5b8', '6204239001', 'Barrio Presidente Perón', '62', 'General Roca'),
    ('ae91aeeb-d09b-5bdf-9ccb-97ecf925588a', '6204239002', 'Cinco Saltos', '62', 'General Roca'),
    ('52babe7d-957e-5664-a5c6-2dd823d8b202', '62042400', 'Cipolletti', '62', 'General Roca'),
    ('1caaad8b-0f5a-5f8b-9e48-4ed79ebe287d', '6204240001', 'Barrio La Lor', '62', 'General Roca'),
    ('2a683f04-c2c6-5a8d-90e9-275e85618336', '6204240002', 'Cipolletti', '62', 'General Roca'),
    ('b9b58310-ef05-57d2-a77d-4f66f055d2c8', '62042410', 'Contralmirante Cordero', '62', 'General Roca'),
    ('c344920d-3adc-5cc4-a6d1-2eef6231318d', '62042420', 'Ferri', '62', 'General Roca'),
    ('0d934955-60cf-50cf-8d76-ad86f498e137', '62042430', 'General Enrique Godoy', '62', 'General Roca'),
    ('3afff3b3-b374-58e6-bdb0-848c603cf59f', '62042440', 'General Fernández Oro', '62', 'General Roca'),
    ('8b707ec2-be0c-5b90-accc-fe52c9394694', '62042450', 'General Roca', '62', 'General Roca'),
    ('6ab097dd-ede5-5ec1-89c5-a297d48e6029', '6204245001', 'Barrio Pino Azul', '62', 'General Roca'),
    ('e6ec714b-94e5-5ea0-8ba6-89ab9a189afe', '6204245002', 'General Roca', '62', 'General Roca'),
    ('e064201a-23dd-51b9-b275-e68b483359e3', '62042460', 'Ingeniero Luis A. Huergo', '62', 'General Roca'),
    ('987bd090-2be8-5ce3-948a-de40b77d34f4', '62042470', 'Ingeniero Otto Krause', '62', 'General Roca'),
    ('339ee43d-c885-5b57-800b-0e1dd804b49e', '62042480', 'Mainqué', '62', 'General Roca'),
    ('13ffdff3-31b5-5f99-8d48-86018be57b46', '62042490', 'Paso Córdova', '62', 'General Roca'),
    ('5b075959-c46d-57c3-a151-5bd9531f2a7d', '62042500', 'Península Ruca Co', '62', 'General Roca'),
    ('90bb04db-a60b-5a4f-8321-c9ab8a2fb6c0', '62042520', 'Sargento Vidal', '62', 'General Roca'),
    ('1493e565-3be6-5723-9554-2c3c7e7130e4', '62042530', 'Villa Alberdi', '62', 'General Roca'),
    ('b5ec9043-b52b-560e-810f-b9d7deeacc62', '62042540', 'Villa del Parque', '62', 'General Roca'),
    ('7a791a27-28a8-504e-ad91-00365537720b', '62042550', 'Villa Manzano', '62', 'General Roca'),
    ('b8c810f7-e162-536f-bec0-509385f3f75f', '62042560', 'Villa Regina', '62', 'General Roca'),
    ('749906dd-207e-57e5-a809-2487438ccc8f', '62042570', 'Villa San Isidro', '62', 'General Roca'),
    ('3e75aa18-7a01-53aa-96f4-998eb5ee4649', '62049010', 'Comicó', '62', '9 de Julio'),
    ('fa653bc9-9cb6-539e-8720-701a185dea27', '62049020', 'Cona Niyeu', '62', '9 de Julio'),
    ('05667ad5-508f-5f02-8829-e76eb80fbf54', '62049030', 'Ministro Ramos Mexía', '62', '9 de Julio'),
    ('6c883ed7-7dd7-53e3-9e75-a59b5472aee6', '62049040', 'Prahuaniyeu', '62', '9 de Julio'),
    ('bda22a7a-8c8a-5e6e-84a2-a09c31eadca2', '62049050', 'Sierra Colorada', '62', '9 de Julio'),
    ('081d87d4-f03d-525b-9449-6a72676ee624', '62049060', 'Treneta', '62', '9 de Julio'),
    ('04cb93c7-55ae-57b6-a08f-17b65d585552', '62049070', 'Yaminué', '62', '9 de Julio'),
    ('63790297-cd7c-5533-8075-a7659ad02c24', '62056010', 'Las Bayas', '62', 'Ñorquinco'),
    ('cf434c69-5aab-5a7d-8c35-a1e45364ffe2', '62056020', 'Mamuel Choique', '62', 'Ñorquinco'),
    ('8027b7b8-5d26-5dce-bafc-ffc4aa44437a', '62056030', 'Ñorquincó', '62', 'Ñorquinco'),
    ('89f7ccfc-0e57-52f1-8898-e07c4bf7542c', '62056040', 'Ojos de Agua', '62', 'Ñorquinco'),
    ('f71922cf-4183-5d3b-8695-242dff534421', '62056050', 'Río Chico', '62', 'Ñorquinco'),
    ('eba5c11a-2179-5592-a7b1-e7b0a9404087', '62063005', 'Barrio Esperanza', '62', 'Pichi Mahuida'),
    ('45bd5286-4123-544e-ac2e-6053bfea4f47', '62063010', 'Colonia Juliá y Echarren', '62', 'Pichi Mahuida'),
    ('a858ff67-48cf-5020-b8da-4c22693172bb', '62063013', 'Juventud Unida', '62', 'Pichi Mahuida'),
    ('e4c4e809-9208-50e8-bc05-5ee23278f839', '62063017', 'Pichi Mahuida', '62', 'Pichi Mahuida'),
    ('efaaa6f5-da92-567b-9543-b6f14810739d', '62063020', 'Río Colorado', '62', 'Pichi Mahuida'),
    ('87ee2ed6-c84f-55d0-a863-97b45ceb7d91', '62063060', 'Salto Andersen', '62', 'Pichi Mahuida'),
    ('5cd9b7ac-4f84-5e75-b4f8-3fb86fefc7fd', '62070005', 'Cañadón Chileno', '62', 'Pilcaniyeu'),
    ('4cf0373f-4963-5085-bfb7-3cbe57b264c8', '62070010', 'Comallo', '62', 'Pilcaniyeu'),
    ('cdbe82f2-6fa0-52fa-885e-c9ce2580e627', '62070020', 'Dina Huapi', '62', 'Pilcaniyeu'),
    ('680f2e48-b5e5-55ac-96e9-0b1db08c9b76', '62070030', 'Laguna Blanca', '62', 'Pilcaniyeu'),
    ('9056d357-9c18-598c-89dd-26afa8ff30bb', '62070040', 'Ñirihuau', '62', 'Pilcaniyeu'),
    ('907c62bf-b273-596a-ab00-9be7ea1ae305', '62070060', 'Pilcaniyeu', '62', 'Pilcaniyeu'),
    ('785b884c-e6a6-5a35-b4a8-8eb240d35d0f', '62070070', 'Pilquiniyeu del Limay', '62', 'Pilcaniyeu'),
    ('f867a312-049e-5850-98be-63b5fc2c8b36', '62070080', 'Villa Llanquín', '62', 'Pilcaniyeu'),
    ('c7d4e4d0-b9c9-575d-b0a2-6f387bb39817', '62077005', 'El Empalme', '62', 'San Antonio'),
    ('8ad7d906-595d-51a0-8d0b-363072ae6506', '62077010', 'Las Grutas', '62', 'San Antonio'),
    ('b7ad60bb-208f-573c-8a75-f5c7d1662fd7', '62077020', 'Playas Doradas', '62', 'San Antonio'),
    ('069c9255-1528-576b-abd2-8a6b3af73716', '62077030', 'Puerto San Antonio Este', '62', 'San Antonio'),
    ('873176e4-f378-5344-8ed4-c0e6809266df', '62077040', 'Punta Colorada', '62', 'San Antonio'),
    ('a0888c60-2862-52ab-838e-0e310ad7abb5', '62077045', 'Saco Viejo', '62', 'San Antonio'),
    ('7e647352-79d5-564a-90e1-33450a52af47', '62077050', 'San Antonio Oeste', '62', 'San Antonio'),
    ('adc0c0f6-4bef-57d4-9bef-138103842a8a', '62077060', 'Sierra Grande', '62', 'San Antonio'),
    ('9e7d7c84-5585-5869-ba6e-6bef55d543d3', '62084010', 'Aguada Cecilio', '62', 'Valcheta'),
    ('8c954c17-e4fe-5330-bc19-8fd9897a63b1', '62084020', 'Arroyo Los Berros', '62', 'Valcheta'),
    ('e3562310-e39a-533b-ae4c-5eaf4639a0ad', '62084030', 'Arroyo Ventana', '62', 'Valcheta'),
    ('eb848bb8-f9ec-57ae-857f-991d80a51884', '62084040', 'Nahuel Niyeu', '62', 'Valcheta'),
    ('35f5e4aa-3ee6-5780-9723-813dd2dc220d', '62084050', 'Sierra Pailemán', '62', 'Valcheta'),
    ('41a25365-a674-5a7f-92f8-d107948860d2', '62084060', 'Valcheta', '62', 'Valcheta'),
    ('923cd205-d85c-5d20-9b40-c59f1422de7b', '62091010', 'Aguada de Guerra', '62', '25 de Mayo'),
    ('a1727bc3-d5da-50d7-82e7-8e24cab328e5', '62091020', 'Clemente Onelli', '62', '25 de Mayo'),
    ('6f082837-10eb-5890-b8c7-2de7bf173128', '62091030', 'Colan Conhue', '62', '25 de Mayo'),
    ('88da0825-aaf2-55f4-ba0e-6997c9013e00', '62091040', 'El Caín', '62', '25 de Mayo'),
    ('41e36b92-c2d0-5262-81f9-283dc7a0cd96', '62091050', 'Ingeniero Jacobacci', '62', '25 de Mayo'),
    ('2d675c5b-c5a4-57fb-80b6-088a46ea27dd', '62091060', 'Los Menucos', '62', '25 de Mayo'),
    ('4e51f103-1c67-5c15-b7cf-2d8c161413c4', '62091070', 'Maquinchao', '62', '25 de Mayo'),
    ('c45b0ac2-ef5f-58eb-b663-ff5372fd71c3', '62091090', 'Pilquiniyeu', '62', '25 de Mayo'),
    ('41c3ece0-a8ab-579e-b8ef-cb340b16219c', '66007010', 'Apolinario Saravia', '66', 'Anta'),
    ('bc16aba1-5ee9-57b2-8354-25fe3ba88f80', '66007020', 'Ceibalito', '66', 'Anta'),
    ('8a0ca036-0d65-5d02-a07a-7dca00c9b17e', '66007030', 'Centro 25 de Junio', '66', 'Anta'),
    ('b9594b9d-c292-5c73-b14f-7adb2d78062a', '66007040', 'Coronel Mollinedo', '66', 'Anta'),
    ('3cabe200-adb5-5fb1-9ca2-d45de95e4dcc', '66007050', 'Coronel Olleros', '66', 'Anta'),
    ('0baff989-621c-57c6-bf4c-40424a88a743', '66007060', 'El Quebrachal', '66', 'Anta'),
    ('7b290106-2ec4-57d7-b445-9f3c15215d62', '66007070', 'Gaona', '66', 'Anta'),
    ('653d8473-b0ca-52fd-b49a-da29f14c6660', '66007080', 'General Pizarro', '66', 'Anta'),
    ('46b37292-aa01-5dcc-b4c3-b4e5b07d4e7e', '66007090', 'Joaquín V. González', '66', 'Anta'),
    ('c9c62a90-b97f-52c1-aea2-bce1a03679e0', '66007100', 'Las Lajitas', '66', 'Anta'),
    ('5d296732-5bff-5ebf-b40f-0b7cec939eb4', '66007110', 'Luis Burela', '66', 'Anta'),
    ('37fa25db-3a67-5b1c-b00d-0f8b5478ef3c', '66007120', 'Macapillo', '66', 'Anta'),
    ('4fa3313f-a4f7-569c-866b-8588ad3719d7', '66007130', 'Nuestra Señora de Talavera', '66', 'Anta'),
    ('aa2eff30-e88b-59cc-b718-b923bb9c5951', '66007140', 'Piquete Cabado', '66', 'Anta'),
    ('5eb3bbbf-062e-5c83-8fbc-5b00814db73e', '66007150', 'Río del Valle', '66', 'Anta'),
    ('e9802e21-8e2e-5e5e-9771-c8f0e7853dc4', '66007160', 'Tolloche', '66', 'Anta'),
    ('c08b51e0-ceb9-5ea7-8c30-2a800f2b4337', '66014010', 'Cachi', '66', 'Cachi'),
    ('67f6c7d6-b5da-5f15-bec1-bebabc9d5b2d', '66014020', 'Payogasta', '66', 'Cachi'),
    ('c398d592-b55b-5a7e-ab43-d3e600022e11', '66021010', 'Cafayate', '66', 'Cafayate'),
    ('c95912d3-58b5-506c-a3c8-56120778f5ee', '66021020', 'Tolombón', '66', 'Cafayate'),
    ('cca2230b-56a3-58b2-a1c3-62a969682049', '66028050', 'Salta', '66', 'Capital'),
    ('ea6b6923-a3f8-5f7c-b008-35fe038219ce', '6602805001', 'Country Club El Tipal', '66', 'Capital'),
    ('efb5840a-d05a-5603-924a-171dd5e8adfc', '6602805002', 'Country Club La Almudena', '66', 'Capital'),
    ('f9968f80-5f48-5deb-a23d-f18e421205d4', '6602805003', 'Salta', '66', 'Capital'),
    ('f76c7547-b6d0-5a2a-b3f7-71bfe4a13f6a', '66028060', 'Villa San Lorenzo', '66', 'Capital'),
    ('960c97f6-3d90-521d-b290-1169e0a7374c', '66035010', 'Cerrillos', '66', 'Cerrillos'),
    ('cbd0f24c-a629-5769-ba59-d168e72aefee', '66035020', 'La Merced', '66', 'Cerrillos'),
    ('670abd17-214c-5cf0-bd5e-874b6a782019', '66035030', 'San Agustín', '66', 'Cerrillos'),
    ('dcfbec7f-a4fc-58b9-be66-e82d647b0f0f', '66042003', 'Barrio Finca La Maroma', '66', 'Chicoana'),
    ('eb3d2990-f19e-5b01-bf1d-e2a7768ce5ae', '66042005', 'Barrio La Rotonda', '66', 'Chicoana'),
    ('94038520-ce53-58b9-b514-8fa4f340bc73', '66042007', 'Barrio Santa Teresita', '66', 'Chicoana'),
    ('09a52ce8-23b5-5574-905d-dbb6464b73e4', '66042010', 'Chicoana', '66', 'Chicoana'),
    ('fccba3a4-d0e0-55a3-b817-9fb2bbbfede4', '66042020', 'El Carril', '66', 'Chicoana'),
    ('46ba5f00-9ec1-5bbd-b5eb-d5f66134284b', '66049010', 'Campo Santo', '66', 'General Güemes'),
    ('37244230-e7fe-5045-ad04-fe30659fe684', '66049020', 'Cobos', '66', 'General Güemes'),
    ('bab61fa4-5134-5319-a634-b1666a27bab9', '66049030', 'El Bordo', '66', 'General Güemes'),
    ('b9397ae6-4690-5257-bdb6-4aa911ffb95c', '66049040', 'General Güemes', '66', 'General Güemes'),
    ('b90ca831-e36f-58e8-9d47-293b6b4d6009', '66056010', 'Aguaray', '66', 'General José de San Martín'),
    ('1d9c225f-06c6-5061-9a3c-e00b84ff637a', '66056030', 'Campichuelo', '66', 'General José de San Martín'),
    ('1887424d-e2f4-5559-a437-a870298033d8', '66056040', 'Campo Durán', '66', 'General José de San Martín'),
    ('d6302a39-29ef-5ed3-8bc3-c35808d4daf8', '66056050', 'Capiazuti', '66', 'General José de San Martín'),
    ('4190c349-69be-522c-bfe1-b0f35af59398', '66056060', 'Carboncito', '66', 'General José de San Martín'),
    ('8abbbdc3-8de8-5355-8433-dbb3da0c3b26', '66056070', 'Coronel Cornejo', '66', 'General José de San Martín'),
    ('15c8a281-84d5-5313-931c-b071c86ecb88', '66056080', 'Dragones', '66', 'General José de San Martín'),
    ('2954ca1d-f323-5eb0-81c9-b16aec3f2a3c', '66056090', 'Embarcación', '66', 'General José de San Martín'),
    ('8c4dc94b-d9c6-5047-a786-9b07b9ee6c5f', '6605609001', 'Embarcación', '66', 'General José de San Martín'),
    ('8653a214-de7d-5f67-935f-2162975377a3', '6605609002', 'Misión Tierras Fiscales', '66', 'General José de San Martín'),
    ('e8ac1851-2854-587c-8e63-ce2357b0af37', '66056100', 'General Ballivián', '66', 'General José de San Martín'),
    ('77224d21-2947-50b7-a330-c8f9bb492f34', '66056110', 'General Mosconi', '66', 'General José de San Martín'),
    ('937bd0f4-3dae-5241-9beb-1284be030c58', '6605611001', 'General Mosconi', '66', 'General José de San Martín'),
    ('e69133c8-85c4-58d8-b88e-c34a3c4ef2d6', '6605611002', 'Recaredo', '66', 'General José de San Martín'),
    ('d8213377-7fae-5d2f-b264-50512118180b', '66056120', 'Hickman', '66', 'General José de San Martín'),
    ('f6f03a28-1497-5355-9f11-081b7d816486', '66056130', 'Misión Chaqueña', '66', 'General José de San Martín'),
    ('deeebf05-f647-5385-80e3-a763af56bb2e', '66056150', 'Misión Kilómetro 6', '66', 'General José de San Martín'),
    ('a1d9023a-f0e2-5bb2-b03e-5a474a6d775e', '66056170', 'Pacará', '66', 'General José de San Martín'),
    ('27cc345c-15af-5425-b6f4-556f54938069', '66056180', 'Padre Lozano', '66', 'General José de San Martín'),
    ('684e06c5-6b51-5a8d-8b9f-4c07c0412d0a', '66056190', 'Piquirenda', '66', 'General José de San Martín'),
    ('2a27428f-f3fb-5581-9586-ef8287da556d', '66056200', 'Profesor Salvador Mazza', '66', 'General José de San Martín'),
    ('b59a3cbf-f30a-5b30-971e-def75bc6c8ee', '66056220', 'Tartagal', '66', 'General José de San Martín'),
    ('f6ba7f23-710e-5c40-a618-96cf5436d3eb', '66056230', 'Tobantirenda', '66', 'General José de San Martín'),
    ('7d761763-8238-52d3-a5a0-3f3946d4a533', '66056240', 'Tranquitas', '66', 'General José de San Martín'),
    ('dc4ab040-1054-56a4-bf39-1a85afcb6be0', '66056250', 'Yacuy', '66', 'General José de San Martín'),
    ('1141abeb-b21c-54c1-bc84-1f005dc67718', '66063010', 'Guachipas', '66', 'Guachipas'),
    ('e512e544-f7bc-542f-b039-197382f5700c', '66070010', 'Iruya', '66', 'Iruya'),
    ('9bd41159-1527-5504-9714-8ed811a65144', '66070020', 'Isla de Cañas', '66', 'Iruya'),
    ('87d37a2a-a482-5d8e-803e-8d1a7bd592f7', '66070030', 'Pueblo Viejo', '66', 'Iruya'),
    ('23a22d7b-5899-58be-af8d-273c09692583', '66077010', 'La Caldera', '66', 'La Caldera'),
    ('584ed225-9f1e-564b-b2fa-0caf427ff649', '66077020', 'Vaqueros', '66', 'La Caldera'),
    ('0dd52d3b-cdc5-5c6b-8a7d-c038cbee1c26', '66084010', 'El Jardín', '66', 'La Candelaria'),
    ('57f05381-a743-5f0f-b73a-968910ae337a', '66084020', 'El Tala', '66', 'La Candelaria'),
    ('b2922ecf-0c03-57d8-a1df-cafae5b7692c', '66084030', 'La Candelaria', '66', 'La Candelaria'),
    ('f8ccd3cb-909a-531c-83ee-a63d3f4e0b5b', '66091010', 'Cobres', '66', 'La Poma'),
    ('6cb09441-45dc-52b6-aa2a-c8fbea212050', '66091020', 'La Poma', '66', 'La Poma'),
    ('ab46282e-d7c9-53eb-b22b-5ce47cd9481d', '66098010', 'Ampascachi', '66', 'La Viña'),
    ('8f15e36f-a1e8-570a-840a-621d5b98fc1c', '66098020', 'Cabra Corral', '66', 'La Viña'),
    ('58832f20-fab3-513b-9e00-ebf5d8a56636', '66098030', 'Coronel Moldes', '66', 'La Viña'),
    ('f22bbfe3-1ece-573e-8a2b-25d535f9ddbf', '66098040', 'La Viña', '66', 'La Viña'),
    ('d05b16f3-8ed8-50fd-989d-8cf51faa9aa8', '66098050', 'Talapampa', '66', 'La Viña'),
    ('e27b85f1-d5c7-5935-bd60-35382fc017ed', '66105010', 'Olacapato', '66', 'Los Andes'),
    ('ea5b3658-71cb-5667-9549-188bf30f12d1', '66105020', 'San Antonio de los Cobres', '66', 'Los Andes'),
    ('e8f5098e-edfb-5826-831f-f5c1a06c17d9', '66105030', 'Santa Rosa de los Pastos Grandes', '66', 'Los Andes'),
    ('5bd01a1c-e549-51df-b659-a2ad563193e6', '66105040', 'Tolar Grande', '66', 'Los Andes'),
    ('7904ac2e-61f1-53d1-b807-8cd216711f9b', '66112010', 'El Galpón', '66', 'Metán'),
    ('05b99b88-5e32-5067-9654-855e20e359d7', '66112020', 'El Tunal', '66', 'Metán'),
    ('2d7f1515-2a59-549f-a145-ba0747aed6fe', '66112030', 'Lumbreras', '66', 'Metán'),
    ('809d4c83-5111-548d-a890-534c742c379a', '66112040', 'San José de Metán (Est. Metán)', '66', 'Metán'),
    ('8aedca85-b53d-5e02-8d57-094759124218', '66112070', 'Río Piedras', '66', 'Metán'),
    ('fa75520e-3c9e-57be-a4cb-0d46e0c8abfd', '66112080', 'San José de Orquera', '66', 'Metán'),
    ('78f33781-676b-511a-9464-ec8da089646e', '66119010', 'La Puerta', '66', 'Molinos'),
    ('e6888b9c-91a9-517a-a0dd-e2918bc8a112', '66119020', 'Molinos', '66', 'Molinos'),
    ('527b0321-16a8-5c86-b3db-c5bfbb919a2a', '66119030', 'Seclantás', '66', 'Molinos'),
    ('6146bcc3-d00c-5711-ba00-571808f9e088', '66126010', 'Aguas Blancas', '66', 'Orán'),
    ('f60c4e05-acd3-5f0d-aaa9-06ef4f89640d', '66126020', 'Colonia Santa Rosa', '66', 'Orán'),
    ('6bff6b08-761e-59df-9aaf-d1e05a020580', '6612602001', 'Colonia Santa Rosa', '66', 'Orán'),
    ('9e879830-41dd-56ef-8974-6c57d8d6a6d3', '6612602002', 'La Misión', '66', 'Orán'),
    ('5cc1bffd-36f9-5dc5-a534-e43a4bf709c4', '66126030', 'El Tabacal', '66', 'Orán'),
    ('ae05f36f-e2a1-52cf-a1c4-4d277ded7e8a', '66126040', 'Hipólito Yrigoyen', '66', 'Orán'),
    ('2324a41f-1b5c-5e8e-9fff-452c466ceca7', '66126060', 'Pichanal', '66', 'Orán'),
    ('30bce608-5c04-5486-af1b-ffae1e53dda4', '66126070', 'San Ramón de la Nueva Orán', '66', 'Orán'),
    ('5f19d807-edab-5d38-98f8-d70d0726425b', '66126080', 'Urundel', '66', 'Orán'),
    ('eb5dc286-35a5-5b79-a1af-17c4a587d5a1', '66133010', 'Alto de la Sierra', '66', 'Rivadavia'),
    ('bc143da3-9a5f-577c-8db7-377b43999af4', '66133020', 'Capitán Juan Pagé', '66', 'Rivadavia'),
    ('cd720c95-7266-5dac-aef4-1727aac35b47', '66133030', 'Coronel Juan Solá', '66', 'Rivadavia'),
    ('6f1b1969-d7f2-568b-b44b-df360f696a27', '66133035', 'Hito 1', '66', 'Rivadavia'),
    ('683f4954-2caf-5e47-94f8-c7ec73a2105f', '66133040', 'La Unión', '66', 'Rivadavia'),
    ('22f637d5-9ef4-5b3e-9e2a-19127770f6c3', '66133050', 'Los Blancos', '66', 'Rivadavia'),
    ('ccbf5ec2-8ebc-5655-9391-6580791d364e', '66133060', 'Pluma de Pato', '66', 'Rivadavia'),
    ('23cae5ad-72fe-54e5-babf-72505fd6f41c', '66133070', 'Rivadavia', '66', 'Rivadavia'),
    ('9063ee1a-e82f-5683-ba4c-72248614a85e', '66133080', 'Santa María', '66', 'Rivadavia'),
    ('44c5b407-4c70-519b-a92e-24a66d8a3e59', '66133090', 'Santa Rosa', '66', 'Rivadavia'),
    ('b581d442-a9e8-553b-97f3-b58337b928d6', '66133100', 'Santa Victoria Este', '66', 'Rivadavia'),
    ('45798af7-e422-58e8-82ef-c9bc33bba25d', '66140010', 'Antillá', '66', 'Rosario de la Frontera'),
    ('55f1220f-2a3d-5b04-a9f1-665826d5c963', '66140020', 'Copo Quile', '66', 'Rosario de la Frontera'),
    ('d0049c91-8c33-5858-96bf-6650bada1d70', '66140030', 'El Naranjo', '66', 'Rosario de la Frontera'),
    ('025d25df-f9d2-5e90-a258-50373b3aba3b', '66140040', 'El Potrero', '66', 'Rosario de la Frontera'),
    ('1a86f3b9-9343-581c-a8b9-c7a7a5bf1bed', '66140050', 'Rosario de la Frontera', '66', 'Rosario de la Frontera'),
    ('681206f6-19cd-5df5-9e8f-46694bbfaaa3', '66140060', 'San Felipe', '66', 'Rosario de la Frontera'),
    ('269b8e93-1241-5970-8dee-66637c9adfbf', '66147010', 'Campo Quijano', '66', 'Rosario de Lerma'),
    ('e24bf9a3-805f-5bcc-a9ee-0b24e9e483f1', '66147015', 'La Merced del Encón', '66', 'Rosario de Lerma'),
    ('55b93b7d-710e-5e19-b256-981f287e30a0', '66147020', 'La Silleta', '66', 'Rosario de Lerma'),
    ('ee8d0779-09c3-587b-8fad-5f05c538cc70', '66147030', 'Rosario de Lerma', '66', 'Rosario de Lerma'),
    ('c1c8950b-3a1f-56cc-965e-42faac2527a2', '66154010', 'Angastaco', '66', 'San Carlos'),
    ('599cbce5-e74f-50c3-9e3d-9acb091fd64c', '66154020', 'Animaná', '66', 'San Carlos'),
    ('be068151-1a62-5a42-9d53-dc71d4da864a', '66154040', 'San Carlos', '66', 'San Carlos'),
    ('498bdb2e-d257-56c0-96e0-f6453ea2fca8', '66161010', 'Acoyte', '66', 'Santa Victoria'),
    ('782c9d42-df52-5ffc-98b3-2073dfb6ac66', '66161020', 'Campo La Cruz', '66', 'Santa Victoria'),
    ('e3f3e707-7728-5926-94a4-f0f636800e7e', '66161030', 'Los Toldos', '66', 'Santa Victoria'),
    ('af3ee53d-a22f-5780-9047-68760a55e4ed', '66161040', 'Nazareno', '66', 'Santa Victoria'),
    ('3a712434-4cb9-5c26-9a19-173d45e3d314', '66161050', 'Poscaya', '66', 'Santa Victoria'),
    ('13932688-edd7-5696-9fed-4578092511df', '66161060', 'San Marcos', '66', 'Santa Victoria'),
    ('c5eef933-9373-5967-8c9b-d42422157ddd', '66161070', 'Santa Victoria', '66', 'Santa Victoria'),
    ('5d75edbb-6af8-57c6-9116-042f22036d0d', '70007010', 'El Rincón', '70', 'Albardón'),
    ('30bdb649-7a33-51af-9a7c-7c78f6fd7802', '70007020', 'Villa General San Martín - Campo Afuera', '70', 'Albardón'),
    ('55e804f8-394d-54de-8d3b-bd6d7d741c83', '70014010', 'Las Tapias', '70', 'Angaco'),
    ('4a9d0748-1a36-58aa-9640-77fde372cb39', '70014020', 'Villa El Salvador - Villa Sefair', '70', 'Angaco'),
    ('37888e0c-d45e-5e51-8eee-b95e66b09af7', '7001402001', 'Villa El Salvador', '70', 'Angaco'),
    ('388c6e48-70c6-582b-895d-52559c74e71f', '7001402002', 'Villa Sefair', '70', 'Angaco'),
    ('82ccf0e0-8b60-5f0a-885d-95eff82c2d3b', '70021010', 'Barreal - Villa Pituil', '70', 'Calingasta'),
    ('c69d4bf1-34dc-5c4e-9318-0049a5d120c9', '70021020', 'Calingasta', '70', 'Calingasta'),
    ('f3c09de4-8978-549f-a4cb-90b6cdfbd8de', '70021030', 'Tamberías', '70', 'Calingasta'),
    ('2d501312-ae5b-51e7-820f-8a99c5cd34de', '70028010', 'San Juan', '70', 'Capital'),
    ('90010922-9536-5f7d-8751-305fa62bd6c2', '70035010', 'Bermejo', '70', 'Caucete'),
    ('9af46400-0723-59f7-b374-3ca13bc117b4', '70035020', 'Caucete', '70', 'Caucete'),
    ('4d0ef8f1-309f-58f4-8909-8e22fa0aaffd', '70035030', 'El Rincón', '70', 'Caucete'),
    ('e5674e5e-c285-562c-a9a2-e4d639869ccf', '70035040', 'Las Talas - Los Médanos', '70', 'Caucete'),
    ('ff4188f2-a8c0-5861-8d42-cf39e9e91179', '7003504001', 'Las Talas', '70', 'Caucete'),
    ('75a73020-694a-5d1a-94e5-79a16e908708', '7003504002', 'Los Médanos', '70', 'Caucete'),
    ('684f1be3-3469-522b-a69c-fb98eafdaa22', '70035050', 'Marayes', '70', 'Caucete'),
    ('726653cb-cc1d-51fa-b47e-839a3b0c6fef', '70035060', 'Pie de Palo', '70', 'Caucete'),
    ('c9c7788f-f421-544e-836e-c186dad24fbf', '70035070', 'Vallecito', '70', 'Caucete'),
    ('49530f5f-eebb-57fc-90f7-324cf64dc7cf', '70035080', 'Villa Independencia', '70', 'Caucete'),
    ('3894859c-7a5e-525a-b078-c3b884bbf16d', '70042010', 'Chimbas', '70', 'Chimbas'),
    ('96c24639-2255-549d-b7a3-8de7c1a64c4a', '70049010', 'Angualasto', '70', 'Iglesia'),
    ('1dfdb8b3-5e27-5551-be50-8ef68f7db9e4', '70049030', 'Iglesia', '70', 'Iglesia'),
    ('c5b69ea5-9ca2-5d41-aed4-02b393fc1886', '70049040', 'Las Flores', '70', 'Iglesia'),
    ('4f388d60-ed16-5ee8-865d-ab56ab30d71a', '70049050', 'Pismanta', '70', 'Iglesia'),
    ('157c0644-045c-5d27-9b4f-e4cfb10266dd', '70049060', 'Rodeo', '70', 'Iglesia'),
    ('6b0ddcfb-6791-5229-a8ba-169d68ae014d', '70049070', 'Tudcum', '70', 'Iglesia'),
    ('b4d852da-5695-53d7-8a4a-43002726f0d6', '70056010', 'El Médano', '70', 'Jáchal'),
    ('1b2aff99-a788-5b40-ae91-ed1446512838', '70056020', 'Gran China', '70', 'Jáchal'),
    ('64cec676-d568-5ba2-9157-6b707fd4e4e6', '70056030', 'Huaco', '70', 'Jáchal'),
    ('16d5c255-84f5-516e-94a6-27d74828df18', '70056040', 'Mogna', '70', 'Jáchal'),
    ('59d48b8c-4a8c-58fa-abdd-730d8b2bce82', '70056050', 'Niquivil', '70', 'Jáchal'),
    ('c3a28151-43cc-5222-b4ee-c8b5978bd9a2', '70056060', 'Pampa Vieja', '70', 'Jáchal'),
    ('f9a7d98d-cc58-5395-bc84-a6e8d89c51b3', '7005606002', 'La Falda', '70', 'Jáchal'),
    ('e10f0e41-3353-5875-bd0d-abc1fed04e15', '7005606003', 'Pampa Vieja', '70', 'Jáchal'),
    ('1c6963d0-963a-5851-b62c-18c42e32d715', '70056070', 'San Isidro', '70', 'Jáchal'),
    ('1a86ecea-e926-5c36-baaf-3ae55e10641d', '70056080', 'San José de Jáchal', '70', 'Jáchal')
ON CONFLICT (indec_id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('f71a9da3-e20a-5dc0-a556-aaae3f80ee1b', '70056090', 'Tamberías', '70', 'Jáchal'),
    ('54a4d5d9-3598-501d-a8c0-b62c1cf15d73', '70056100', 'Villa Malvinas Argentinas', '70', 'Jáchal'),
    ('27ca982a-4d25-56cb-8032-1f8f79c91945', '70056110', 'Villa Mercedes', '70', 'Jáchal'),
    ('bf567e57-6a78-5fe1-aad0-5faefb3bb9d6', '70063010', 'Alto de Sierra', '70', '9 de Julio'),
    ('29bb4917-b02c-569e-9795-872fbd8cc648', '70063030', 'Las Chacritas', '70', '9 de Julio'),
    ('09961152-e962-5aa4-9831-a36a8a7cce21', '70063040', '9 de Julio', '70', '9 de Julio'),
    ('603afae7-8618-5a1a-85ac-ed1f311b16fb', '70070005', 'Barrio Municipal', '70', 'Pocito'),
    ('26f66705-6566-5440-a770-06997369b43b', '70070010', 'Barrio Ruta 40', '70', 'Pocito'),
    ('5f1b5bfb-8315-5e78-bcde-988ff93684ca', '70070020', 'Carpintería', '70', 'Pocito'),
    ('b87f0656-2a55-56cc-8a6e-61a9f7311c47', '70070030', 'Quinto Cuartel', '70', 'Pocito'),
    ('045827de-cbe3-56be-a772-2d2a3a257c54', '70070040', 'Villa Aberastain - La Rinconada', '70', 'Pocito'),
    ('08544c83-1fc3-5c74-89b0-c4d9f67e55dc', '7007004001', 'La Rinconada', '70', 'Pocito'),
    ('a2810b60-94f6-5200-8467-f0cbb7b6b103', '7007004002', 'Villa Aberastain', '70', 'Pocito'),
    ('4be5b68e-c3d7-5494-8c15-fa0e8d10b344', '70070050', 'Villa Barboza - Villa Nacusi', '70', 'Pocito'),
    ('a240a326-804a-52b9-a591-085be8e7422a', '7007005001', 'Villa Barboza', '70', 'Pocito'),
    ('f62c95b3-87ba-5dfa-aebd-d683f4cdb3e3', '7007005002', 'Villa Nacusi', '70', 'Pocito'),
    ('858232e9-87e0-5256-bd0e-d66ae8b69111', '70070060', 'Villa Centenario', '70', 'Pocito'),
    ('1c32f091-5a08-5bfc-9024-4b81a53e5aa3', '70077010', 'Rawson', '70', 'Rawson'),
    ('e4ee4a44-43af-5082-8fbf-22bc7bce1eee', '7007701001', 'El Medanito', '70', 'Rawson'),
    ('20599b3f-4ec5-5cbd-9910-dd88e85d4069', '7007701002', 'Rawson', '70', 'Rawson'),
    ('19c16b2b-edb3-546b-b992-59ea0c32e632', '70077020', 'Villa Bolaños (Médano de Oro)', '70', 'Rawson'),
    ('bd0fb417-32a1-5719-aac2-4b36741fccce', '70084010', 'Rivadavia', '70', 'Rivadavia'),
    ('d1a78a7d-ebb6-59f3-ad27-501251ca2f1b', '70091010', 'Barrio Sadop - Bella Vista', '70', 'San Martín'),
    ('2f32334f-9cf2-5091-83e2-a9d2d0f159ef', '70091020', 'Dos Acequias', '70', 'San Martín'),
    ('589601cc-1ffb-5d10-938a-bcea57542be7', '70091030', 'San Isidro', '70', 'San Martín'),
    ('f26f308c-c234-5d8a-86d7-6c83e56d8576', '70091040', 'Villa del Salvador', '70', 'San Martín'),
    ('5502c82c-a183-57eb-b26e-1e6a8fac8843', '70091050', 'Villa Dominguito', '70', 'San Martín'),
    ('861bc1dd-ba9e-5ca1-bf34-3f8b5a993390', '70091060', 'Villa Don Bosco', '70', 'San Martín'),
    ('8fe9b3c9-e678-5786-91a2-25e3336c2a9e', '70091070', 'Villa San Martín', '70', 'San Martín'),
    ('335f0e86-54f3-5c07-bd31-bb06d55fe339', '70098010', 'Santa Lucía', '70', 'Santa Lucía'),
    ('4c4484bd-994f-53dc-b23f-024b82e28ff4', '7009801001', 'Colonia Gutiérrez', '70', 'Santa Lucía'),
    ('162b200c-be57-5d31-9d14-8f85089f6876', '7009801002', 'Santa Lucía', '70', 'Santa Lucía'),
    ('7ba10dc2-cc9a-5fb7-8b66-9abee52527d2', '70105010', 'Cañada Honda', '70', 'Sarmiento'),
    ('c091469e-ff27-5b6e-83fa-bd8af8204ff6', '70105020', 'Cienaguita', '70', 'Sarmiento'),
    ('a506e831-f24a-546f-8256-cfa04a510fdb', '70105030', 'Colonia Fiscal', '70', 'Sarmiento'),
    ('52597daa-d7c6-5a32-8c8a-be25c7afa51a', '70105040', 'Divisadero', '70', 'Sarmiento'),
    ('1b89ed34-1265-54f3-bdfc-8820c0baf056', '70105060', 'Las Lagunas', '70', 'Sarmiento'),
    ('2d34dfa3-c9aa-5268-af48-f34935f75a3f', '70105070', 'Los Berros', '70', 'Sarmiento'),
    ('17f05cef-dd1c-5f63-a4d2-b8a80bf18d2f', '70105080', 'Pedernal', '70', 'Sarmiento'),
    ('18e1ff53-8ac0-5012-b9e8-48bc422c1ddf', '70105090', 'Punta del Médano', '70', 'Sarmiento'),
    ('8ac4a4b0-7f70-5250-9fcc-7c0653aaa093', '70105100', 'Villa Media Agua', '70', 'Sarmiento'),
    ('7a2373e0-7610-5ee8-82c4-740f515f9a51', '70112010', 'Villa Ibáñez', '70', 'Ullum'),
    ('c868162c-9fe2-559e-8caa-268698c5de01', '70119010', 'Astica', '70', 'Valle Fértil'),
    ('2822b256-4f2e-5249-9686-1b96d5bf6ab2', '70119020', 'Balde del Rosario', '70', 'Valle Fértil'),
    ('f882ef72-158d-5eec-b439-5717458c956f', '70119030', 'Chucuma', '70', 'Valle Fértil'),
    ('8a9536c8-6ec4-5e45-a767-8f3bfcfb2e21', '70119040', 'Los Baldecitos', '70', 'Valle Fértil'),
    ('e44fc140-9dd2-58e8-b028-f5818c60df36', '70119050', 'Usno', '70', 'Valle Fértil'),
    ('a3cedfee-aeaa-57b8-baeb-a5267b2bca91', '70119060', 'Villa San Agustín', '70', 'Valle Fértil'),
    ('4a259015-5920-539d-bdc9-fe389f8ad98a', '70126010', 'El Encón', '70', '25 de Mayo'),
    ('ececbd7e-2f39-54d1-9f2c-d3e11895c817', '70126020', 'Tupelí', '70', '25 de Mayo'),
    ('0257747d-ea7f-5693-a658-fe7077a3e699', '70126030', 'Villa Borjas - La Chimbera', '70', '25 de Mayo'),
    ('19a57b29-8f4c-5057-9337-26abfaab96ba', '7012603001', 'La Chimbera', '70', '25 de Mayo'),
    ('e1046dbb-6d12-5cdf-8c94-5da76b0502f1', '7012603002', 'Villa Borjas', '70', '25 de Mayo'),
    ('a44520b1-adb2-5055-818e-4d70b0cbdfa5', '70126040', 'Villa El Tango', '70', '25 de Mayo'),
    ('88a80575-71b2-56ca-b47a-e78981fe6f39', '70126050', 'Villa Santa Rosa', '70', '25 de Mayo'),
    ('7110e6d9-f11c-5667-9a8c-c838c2df0ce4', '70133010', 'Villa Basilio Nievas', '70', 'Zonda'),
    ('77c9c8ab-43e9-51f2-ac59-c498d190e560', '7013301001', 'Villa Basilio Nievas', '70', 'Zonda'),
    ('ba4c4297-e361-5932-8b92-d93a7a184221', '7013301002', 'Villa Tacú', '70', 'Zonda'),
    ('c4c415c2-f29e-525a-9bc0-275e9ad03c9b', '74007010', 'Candelaria', '74', 'Ayacucho'),
    ('8820ebd4-da3e-5d48-ba95-ad794e882a02', '74007030', 'Leandro N. Alem', '74', 'Ayacucho'),
    ('c28ea318-1029-5d33-8f16-835aba4c6e67', '74007040', 'Luján', '74', 'Ayacucho'),
    ('a1b29078-0023-513c-8dcf-f79e744aa96a', '74007050', 'Quines', '74', 'Ayacucho'),
    ('faec9132-a915-5e6a-8678-e53dce30f871', '74007070', 'San Francisco del Monte de Oro', '74', 'Ayacucho'),
    ('07ba7d8c-6609-5301-ae41-502bf7fa6b3a', '74014010', 'La Calera', '74', 'Belgrano'),
    ('debc8584-a604-53cb-893d-e932ae2057d3', '74014020', 'Nogolí', '74', 'Belgrano'),
    ('22ff2be4-3a9a-5bc0-8b75-2f6cd1daa112', '74014030', 'Villa de la Quebrada', '74', 'Belgrano'),
    ('665dd438-f765-52d7-b5b9-b5851bec7934', '74014040', 'Villa General Roca', '74', 'Belgrano'),
    ('55565aa3-1efc-52f8-846a-f6f86e1f3ca8', '74021010', 'Carolina', '74', 'Coronel Pringles'),
    ('833b8f2b-f40c-5030-97bd-a7f4561b3c13', '74021020', 'El Trapiche', '74', 'Coronel Pringles'),
    ('67e8ff3d-3ff8-5a6f-8010-3c6ae2f3a553', '74021025', 'Estancia Grande', '74', 'Coronel Pringles'),
    ('89ef27df-aa53-524f-ac32-da9c6b12b334', '74021030', 'Fraga', '74', 'Coronel Pringles'),
    ('80086d85-911d-51df-be04-fe3768e431dc', '74021050', 'La Florida', '74', 'Coronel Pringles'),
    ('b4ab7091-e595-587b-b4ee-02c7d5533b9c', '74021060', 'La Toma', '74', 'Coronel Pringles'),
    ('cf714197-8121-50d9-97d1-6076dbb78706', '74021070', 'Riocito', '74', 'Coronel Pringles'),
    ('6d965108-6ab8-5a6a-8c7f-59111f7c69e2', '74021090', 'Saladillo', '74', 'Coronel Pringles'),
    ('b809e583-c84b-564e-8702-ea88cb706cf0', '74028010', 'Concarán', '74', 'Chacabuco'),
    ('ac3521f1-b9b0-535b-8fe0-8f1fbe60d33d', '74028020', 'Cortaderas', '74', 'Chacabuco'),
    ('944f089c-1919-5637-946e-0b1d3072607b', '74028030', 'Naschel', '74', 'Chacabuco'),
    ('92216b3c-8f34-5992-84a6-3c03acc4f68a', '74028040', 'Papagayos', '74', 'Chacabuco'),
    ('bbce1304-33e4-582f-a530-a764e92577cf', '74028050', 'Renca', '74', 'Chacabuco'),
    ('698ecb9c-1501-5ac2-b2d8-77af876b90e4', '74028060', 'San Pablo', '74', 'Chacabuco'),
    ('5207a145-7a8b-5a08-8ce4-aabe937705f7', '74028070', 'Tilisarao', '74', 'Chacabuco'),
    ('82414679-ae18-52e7-8589-ca555d68c921', '74028080', 'Villa del Carmen', '74', 'Chacabuco'),
    ('19104a4e-6c9d-5343-b479-c5150bad4edd', '74028090', 'Villa Larca', '74', 'Chacabuco'),
    ('46182cc7-c940-5784-85ca-2cfe3670658a', '74035010', 'Juan Jorba', '74', 'General Pedernera'),
    ('1b6864b8-bfbf-5d58-bf27-dd23cb9745be', '74035020', 'Juan Llerena', '74', 'General Pedernera'),
    ('bd49cf59-4585-5ebd-b758-772d3436446b', '74035030', 'Justo Daract', '74', 'General Pedernera'),
    ('f27c3ff4-8732-5005-bd8f-52c956d0a027', '74035040', 'La Punilla', '74', 'General Pedernera'),
    ('0a66b712-5acb-5cd7-a8fa-b01fb3bad65d', '74035050', 'Lavaisse', '74', 'General Pedernera'),
    ('2fd08301-cde5-572e-9d8a-433d2ff386c7', '74035055', 'Nación Ranquel', '74', 'General Pedernera'),
    ('14f1b88e-7174-5e41-b98b-ab3460511e1c', '74035060', 'San José del Morro', '74', 'General Pedernera'),
    ('71ca2642-7cf7-500e-91b6-c8005030a845', '74035070', 'Villa Mercedes', '74', 'General Pedernera'),
    ('573b8557-c88e-50d5-b9f7-dc194d3cf930', '7403507001', 'La Ribera', '74', 'General Pedernera'),
    ('9843f2b5-e2fb-5eb2-83dc-9fbc84ca27c8', '7403507002', 'Villa Mercedes', '74', 'General Pedernera'),
    ('0945b3b8-25a4-5f98-98f8-9629563096b1', '74035080', 'Villa Reynolds', '74', 'General Pedernera'),
    ('3e9d18a9-5dc5-55d6-a298-45510df08337', '7403508001', 'Country Club Los Caldenes', '74', 'General Pedernera'),
    ('4fc7c7e4-3fad-5504-853c-d9764bf4dc0a', '7403508002', '5ta Brigada', '74', 'General Pedernera'),
    ('2945352b-30fb-5065-89e4-1884777f5a4e', '74035090', 'Villa Salles', '74', 'General Pedernera'),
    ('4ac4ec85-1993-5edc-a08d-3ec07913c8bc', '74042010', 'Anchorena', '74', 'Gobernador Dupuy'),
    ('bf7e16fa-4d5d-5794-8321-530b532950cf', '74042020', 'Arizona', '74', 'Gobernador Dupuy'),
    ('ebe1b9fd-b466-571e-b37f-b0e83fcc92e6', '74042030', 'Bagual', '74', 'Gobernador Dupuy'),
    ('b6fbdb17-573b-5c12-b939-a97c13ac2d70', '74042040', 'Batavia', '74', 'Gobernador Dupuy'),
    ('f34271f4-f59f-508f-8a5e-d8e2ac3a29a8', '74042050', 'Buena Esperanza', '74', 'Gobernador Dupuy'),
    ('0c01ccd9-2e74-50ec-bc8d-4ec157c21a9e', '74042060', 'Fortín El Patria', '74', 'Gobernador Dupuy'),
    ('1dd4f8c8-6b0d-564e-b660-dfe375afd6e9', '74042070', 'Fortuna', '74', 'Gobernador Dupuy'),
    ('113eccef-45f8-5a21-8c1f-41c63bfcc1c3', '74042080', 'La Maroma', '74', 'Gobernador Dupuy'),
    ('0213881a-f75c-50ff-a5af-0b5e1524fdbc', '74042090', 'Los Overos', '74', 'Gobernador Dupuy'),
    ('dd9709aa-bb58-5360-8413-bbc1ad66b764', '74042100', 'Martín de Loyola', '74', 'Gobernador Dupuy'),
    ('d654455d-d43b-5b11-83e1-85c18037428d', '74042110', 'Nahuel Mapá', '74', 'Gobernador Dupuy'),
    ('325041c1-a90d-5d58-b6a4-5ee34730ae03', '74042120', 'Navia', '74', 'Gobernador Dupuy'),
    ('122a6ccd-5911-539b-b702-8f9a59b9c534', '74042130', 'Nueva Galia', '74', 'Gobernador Dupuy'),
    ('3abf5a40-af26-5948-b777-cf0b9d5b2215', '74042140', 'Unión', '74', 'Gobernador Dupuy'),
    ('b2a6b432-d71d-5f7b-a928-9683b249aa84', '74049010', 'Carpintería', '74', 'Junín'),
    ('fb35e2a6-ae16-5526-8a71-5c6d930607f7', '74049030', 'Lafinur', '74', 'Junín'),
    ('1f000f13-16ed-5fe3-8464-cd62495e213a', '74049040', 'Los Cajones', '74', 'Junín'),
    ('db022753-8974-5870-abc0-b08df7832944', '74049050', 'Los Molles', '74', 'Junín'),
    ('8e685d6c-6323-5cfb-a4a3-bd910434227a', '74049060', 'Merlo', '74', 'Junín'),
    ('8d9787fd-7604-50c5-9943-a02b2d80f083', '74049070', 'Santa Rosa del Conlara', '74', 'Junín'),
    ('ea7a956e-e85f-5b74-9d6f-81a8be9d2404', '74049080', 'Talita', '74', 'Junín'),
    ('efbcc94a-0ed1-57cd-b296-98400c25f1f4', '74056010', 'Alto Pelado', '74', 'Juan Martín de Pueyrredón'),
    ('3615b3e4-d615-5ba7-b689-e9e971e804c6', '74056020', 'Alto Pencoso', '74', 'Juan Martín de Pueyrredón'),
    ('0798fabb-6c05-5024-837c-e8829910feb2', '74056030', 'Balde', '74', 'Juan Martín de Pueyrredón'),
    ('0e2a6a5a-cd21-5bb4-9aac-22c3e9428681', '74056040', 'Beazley', '74', 'Juan Martín de Pueyrredón'),
    ('4d7432e1-5409-54e1-8d3d-fffe5f73cd59', '74056050', 'Cazador', '74', 'Juan Martín de Pueyrredón'),
    ('26e90860-de2c-522c-a49d-2a97a50af817', '74056060', 'Chosmes', '74', 'Juan Martín de Pueyrredón'),
    ('978080d4-c885-51c9-9990-f057408958a2', '74056070', 'Desaguadero', '74', 'Juan Martín de Pueyrredón'),
    ('ca693fea-193c-568f-bdfa-3133d4180f24', '74056080', 'El Volcán', '74', 'Juan Martín de Pueyrredón'),
    ('c4458228-e711-5136-b178-f448bab3db18', '74056090', 'Jarilla', '74', 'Juan Martín de Pueyrredón'),
    ('09f4040b-84da-5073-a417-3f919b6b8c7a', '74056100', 'Juana Koslay', '74', 'Juan Martín de Pueyrredón'),
    ('613a941f-491c-5282-ae35-756d89e75bb5', '7405610001', 'Cerro Colorado', '74', 'Juan Martín de Pueyrredón'),
    ('c8151e04-dd59-5f7b-981e-c7b4dab3f4eb', '7405610002', 'Cruz de Piedra', '74', 'Juan Martín de Pueyrredón'),
    ('91f0e908-8d62-5240-b587-1e1c97d14424', '7405610003', 'El Chorrillo', '74', 'Juan Martín de Pueyrredón'),
    ('862fff25-e2ad-56d6-a559-11e411e7649f', '7405610004', 'Las Chacras', '74', 'Juan Martín de Pueyrredón'),
    ('0bd5f253-5a9b-5c32-b847-b6619beccfc6', '7405610005', 'San Roque', '74', 'Juan Martín de Pueyrredón'),
    ('2dba5e15-ba08-545a-bf28-9272e711387e', '74056105', 'La Punta', '74', 'Juan Martín de Pueyrredón'),
    ('27ee906c-6830-5167-be27-a504f4774134', '74056110', 'Mosmota', '74', 'Juan Martín de Pueyrredón'),
    ('7f9631bc-b42e-57bc-9ff2-4a0337cd2ee0', '74056120', 'Potrero de los Funes', '74', 'Juan Martín de Pueyrredón'),
    ('b3d683f0-210a-56ec-8632-b3be8f86c24e', '74056130', 'Salinas del Bebedero', '74', 'Juan Martín de Pueyrredón'),
    ('6ce26051-a4bf-54d4-a971-26e799e0e8d3', '74056140', 'San Jerónimo', '74', 'Juan Martín de Pueyrredón'),
    ('f88deb46-7e55-53be-aff0-7cfb21324910', '74056150', 'San Luis', '74', 'Juan Martín de Pueyrredón'),
    ('775c769e-f365-56d5-9912-087661731ed7', '74056160', 'Zanjitas', '74', 'Juan Martín de Pueyrredón'),
    ('0c2f9981-0ba4-5160-8c5c-823c50d40391', '74063010', 'La Vertiente', '74', 'Libertador General San Martín'),
    ('a1b92c1d-9118-598f-92a9-0ce88436ee83', '74063020', 'Las Aguadas', '74', 'Libertador General San Martín'),
    ('c47c7716-f8f8-5257-9a5d-8fc2d94478a8', '74063030', 'Las Chacras', '74', 'Libertador General San Martín'),
    ('42c12ca0-d730-55cb-bcc1-646ee47ee1fb', '74063040', 'Las Lagunas', '74', 'Libertador General San Martín'),
    ('a2136556-eb52-58d1-981d-e79ca24e2de3', '74063050', 'Paso Grande', '74', 'Libertador General San Martín'),
    ('400789d3-943e-53b8-a870-b4298d7b22ea', '74063060', 'Potrerillo', '74', 'Libertador General San Martín'),
    ('269f8618-412f-5d84-8778-1a4efc006b63', '74063070', 'San Martín', '74', 'Libertador General San Martín'),
    ('6766d583-a722-55b9-828e-cd7456d0899e', '74063080', 'Villa de Praga', '74', 'Libertador General San Martín'),
    ('ec04607e-7907-5e01-b747-b4a0d4ea798e', '78007010', 'Comandante Luis Piedrabuena', '78', 'Corpen Aike'),
    ('7b40241c-8b47-516a-b1f7-cb94aba98b72', '78007020', 'Puerto Santa Cruz', '78', 'Corpen Aike'),
    ('62aaf0af-607a-50a3-9d1d-dca87c5a9bd0', '78014010', 'Caleta Olivia', '78', 'Deseado'),
    ('e69225fd-109c-502b-b051-a3d3d4df3216', '78014020', 'Cañadón Seco', '78', 'Deseado'),
    ('d61f90a2-873e-5ec8-a67c-7d3e0fb1ba1d', '78014030', 'Fitz Roy', '78', 'Deseado'),
    ('768c2cc0-5743-59cf-bc5d-48e25a17660b', '78014040', 'Jaramillo', '78', 'Deseado'),
    ('6730232e-abd5-5a45-9377-8d60f572103b', '78014050', 'Koluel Kaike', '78', 'Deseado'),
    ('78943249-cda6-52f5-acd6-0a56ae0f5ed6', '78014060', 'Las Heras', '78', 'Deseado'),
    ('dc0a16dc-4454-528d-beee-0a1ec2e89876', '78014070', 'Pico Truncado', '78', 'Deseado'),
    ('c6e9e3a3-5fc1-5d4e-ae87-d07268b37cd4', '78014080', 'Puerto Deseado', '78', 'Deseado'),
    ('fef80100-e27b-50ac-9587-989c576676a0', '78014090', 'Tellier', '78', 'Deseado'),
    ('cfa2d571-3dde-55ab-8a95-fc86ba366e52', '78021010', 'El Turbio', '78', 'Güer Aike'),
    ('c399125f-012c-59ef-a48b-d3325dcc3451', '78021020', 'Julia Dufour', '78', 'Güer Aike'),
    ('cc2c1a0d-75d0-54c0-bf4e-b247eb037b7f', '78021040', 'Río Gallegos', '78', 'Güer Aike'),
    ('86195639-9a08-594e-89a0-9112c5b5cb65', '78021050', 'Rospentek', '78', 'Güer Aike'),
    ('d7c79370-e459-56d9-9d42-9599fcf606ed', '78021060', '28 de Noviembre', '78', 'Güer Aike'),
    ('e1f8fc7e-96ff-5e29-b7c6-6681a21acf51', '78021070', 'Yacimientos Río Turbio', '78', 'Güer Aike'),
    ('bf9b8346-ff07-5d2c-ba69-bd80db320fcc', '78028010', 'El Calafate', '78', 'Lago Argentino'),
    ('5092ca23-d064-5124-b105-772d6988f69f', '78028020', 'El Chaltén', '78', 'Lago Argentino'),
    ('f620e3c7-bb93-5582-9e65-ac043f72390e', '78028030', 'Tres Lagos', '78', 'Lago Argentino'),
    ('c9460214-7fef-5ddb-a6c4-61c004b0ceb9', '78035010', 'Los Antiguos', '78', 'Lago Buenos Aires'),
    ('e020291f-ceb2-51b2-8116-50ae3e3ce17c', '78035020', 'Perito Moreno', '78', 'Lago Buenos Aires'),
    ('73abc5c2-58c6-538e-b3d2-5cfb30152e0c', '78042010', 'Puerto San Julián', '78', 'Magallanes'),
    ('90169d00-d788-5840-999d-8d9bd433c508', '78049010', 'Bajo Caracoles', '78', 'Río Chico'),
    ('240271e4-e784-511d-ba7a-b439e3fb96d3', '78049020', 'Gobernador Gregores', '78', 'Río Chico'),
    ('ab38c9f6-ba6b-5488-a2c0-741ef24b57a7', '78049030', 'Hipólito Yrigoyen', '78', 'Río Chico'),
    ('80922dcb-f652-5ba2-89d8-8cef73032772', '82007010', 'Armstrong', '82', 'Belgrano'),
    ('9aefdf38-2c3a-5e76-8111-ac9132fd3a4b', '82007020', 'Bouquet', '82', 'Belgrano'),
    ('dcbfe503-07b1-562d-8506-9e0e5c0e03e4', '82007030', 'Las Parejas', '82', 'Belgrano'),
    ('46f8fa8d-aef2-542e-a858-8952560e806e', '82007040', 'Las Rosas', '82', 'Belgrano'),
    ('e060a227-7aa4-57b1-b7bf-1fe34675ee19', '82007050', 'Montes de Oca', '82', 'Belgrano'),
    ('4d7e08e1-f275-5293-86c0-9c19d91cd329', '82007060', 'Tortugas', '82', 'Belgrano'),
    ('83e127b2-5523-5c06-9111-9cda0c7009ff', '82014010', 'Arequito', '82', 'Caseros'),
    ('1278a345-6a28-56d8-94d5-44a2fff29423', '82014020', 'Arteaga', '82', 'Caseros'),
    ('e2da0146-5ec9-5e8a-8f5c-88177950ed71', '82014030', 'Beravebú', '82', 'Caseros'),
    ('bd322564-00ec-5162-952b-1d7b07509833', '82014040', 'Bigand', '82', 'Caseros'),
    ('bc907908-9fa3-5c83-92c1-a56326a38efa', '82014050', 'Casilda', '82', 'Caseros'),
    ('4898f46a-06b6-5bcd-bb0f-605a33473fa0', '82014060', 'Chabas', '82', 'Caseros'),
    ('3819338e-1688-5c7c-82f7-9e675352445f', '82014070', 'Chañar Ladeado', '82', 'Caseros'),
    ('289120da-5ea6-57ec-b23c-4bc884dca9fe', '82014080', 'Gödeken', '82', 'Caseros'),
    ('039aa726-58cb-51f1-8da6-241cf2f9b61c', '82014090', 'Los Molinos', '82', 'Caseros'),
    ('5a0475ed-ba0e-597a-9332-516fdc76cb09', '82014100', 'Los Nogales', '82', 'Caseros'),
    ('ed9e7742-e55f-5751-b09e-06bc61b93e6b', '82014110', 'Los Quirquinchos', '82', 'Caseros'),
    ('60a290e1-b9b2-5a3b-8152-0b589307986c', '82014120', 'San José de la Esquina', '82', 'Caseros'),
    ('ffe9b08b-313b-5b85-b6eb-499ba804b97e', '82014130', 'Sanford', '82', 'Caseros'),
    ('af88d2cb-73a5-575f-a9e9-d2d1c9162e74', '82014140', 'Villada', '82', 'Caseros'),
    ('a8ebbbc1-a4e1-5af2-9f23-b389b41c6607', '82021010', 'Aldao', '82', 'Castellanos'),
    ('6175c13a-c5ca-5227-96a4-aba6a1c9c9e5', '82021020', 'Angélica', '82', 'Castellanos'),
    ('e6ee76cd-753d-58a7-a344-4d26a00273c3', '82021030', 'Ataliva', '82', 'Castellanos'),
    ('21865a6b-76c1-5254-b650-b9e0f0353b05', '82021040', 'Aurelia', '82', 'Castellanos'),
    ('2975eff4-9d27-5842-a139-a0d08f3af7b5', '82021050', 'Barrios Acapulco y Veracruz', '82', 'Castellanos'),
    ('9fe89a7c-fcfd-5d23-b6cf-dc0bce5ac5e6', '82021060', 'Bauer y Sigel', '82', 'Castellanos'),
    ('7d4b4609-9a20-5f53-a58c-17ca2878de4c', '82021070', 'Bella Italia', '82', 'Castellanos'),
    ('5bcbcb45-c52f-51a9-b1f2-fe13910d9862', '82021080', 'Castellanos', '82', 'Castellanos'),
    ('657cf626-731e-57d3-b494-4d5afeec060e', '82021090', 'Colonia Bicha', '82', 'Castellanos'),
    ('8f62fbbd-8c5c-5aab-a7da-454a2cf451a6', '82021100', 'Colonia Cello', '82', 'Castellanos'),
    ('07df07ac-63a4-53ac-83c8-9ce8cdcf867e', '82021110', 'Colonia Margarita', '82', 'Castellanos'),
    ('ba1c44cc-f64c-50c5-908a-07b70d8d00fa', '82021120', 'Colonia Raquel', '82', 'Castellanos'),
    ('46795815-1c9c-5d91-b73c-939b22910530', '82021130', 'Coronel Fraga', '82', 'Castellanos'),
    ('9d8f36f4-82b0-57e9-9ca1-bb7246ffc782', '82021140', 'Egusquiza', '82', 'Castellanos'),
    ('68f187a6-5b8f-54cc-94d1-152ff66e91f1', '82021150', 'Esmeralda', '82', 'Castellanos'),
    ('6682329c-ae90-5e4b-b665-4ba48c5b73c0', '82021160', 'Estación Clucellas', '82', 'Castellanos'),
    ('2e229e6c-655e-5869-82b2-c442cade7abb', '82021170', 'Estación Saguier', '82', 'Castellanos'),
    ('ef100d30-f90d-50d9-9daf-3ada32b4fe3c', '82021180', 'Eusebia y Carolina', '82', 'Castellanos'),
    ('ae4d32b8-d653-55e2-b172-1f4a83947838', '82021190', 'Eustolia', '82', 'Castellanos'),
    ('5df2e4e8-b154-51a8-8f83-55e9e7463a36', '82021200', 'Frontera', '82', 'Castellanos'),
    ('40c41a29-8670-5fb7-8765-321d1584612c', '82021210', 'Garibaldi', '82', 'Castellanos'),
    ('24dd57d9-1178-5efa-82a8-810cad908641', '82021220', 'Humberto Primo', '82', 'Castellanos'),
    ('03e22577-64ea-54a3-9a33-5f80602dbe28', '82021230', 'Josefina', '82', 'Castellanos'),
    ('3833cfd3-9f9d-5644-bc4d-c9a91b1fa842', '82021240', 'Lehmann', '82', 'Castellanos'),
    ('981d1f87-10ef-5865-8bb0-f0a0d8946fe3', '82021250', 'María Juana', '82', 'Castellanos'),
    ('43d8c956-672e-5353-bb90-c3d4d9ced9b5', '82021260', 'Nueva Lehmann', '82', 'Castellanos'),
    ('b20ee84a-ea35-59c5-8afe-cc1f78aeedbd', '82021270', 'Plaza Clucellas', '82', 'Castellanos'),
    ('6a358a6e-58af-5c83-855a-8b2ba463252a', '82021280', 'Plaza Saguier', '82', 'Castellanos'),
    ('f72f6b07-39c1-5d91-ae57-36e35519c6ac', '82021290', 'Presidente Roca', '82', 'Castellanos'),
    ('8f23364c-8795-50fc-9dca-853852a6d88e', '8202129001', 'Estación Presidente Roca', '82', 'Castellanos'),
    ('a8e4354b-865d-51f0-8cb7-1c7dbec5b249', '8202129002', 'Presidente Roca', '82', 'Castellanos'),
    ('6a6a0c20-21ed-5e00-bd7d-fd2f251c302c', '82021300', 'Pueblo Marini', '82', 'Castellanos'),
    ('6577c7eb-43dd-5f5d-bf54-8bf88c88f16a', '82021310', 'Rafaela', '82', 'Castellanos'),
    ('95ab4551-422c-50f0-92b8-f79df3f0f4f9', '82021320', 'Ramona', '82', 'Castellanos'),
    ('6cc249e7-0684-572d-ad8a-481f862fc5c8', '82021330', 'San Antonio', '82', 'Castellanos'),
    ('743d2e15-5a83-53d6-be09-c1df10425aec', '82021340', 'San Vicente', '82', 'Castellanos'),
    ('f1494acb-9ea8-5b50-bc18-d5dab8afe7cb', '82021350', 'Santa Clara de Saguier', '82', 'Castellanos'),
    ('88aaa373-2ef9-5c13-9ad5-b608519a631b', '82021360', 'Sunchales', '82', 'Castellanos'),
    ('b4b89985-3d50-5914-bb2c-65fdf8aac070', '82021370', 'Susana', '82', 'Castellanos'),
    ('cd4b7ced-a8e7-50e3-a002-d2e3fa332ac3', '82021380', 'Tacural', '82', 'Castellanos'),
    ('5b13f01e-b51d-5ac5-b2ea-c8b4aafb2682', '82021390', 'Vila', '82', 'Castellanos'),
    ('d9000686-58dd-5601-a47a-c60f20575929', '82021400', 'Villa Josefina', '82', 'Castellanos'),
    ('e1f17d44-cf4a-5399-a470-03211435d1c0', '82021410', 'Villa San José', '82', 'Castellanos'),
    ('16173291-a120-5d1d-aaab-9e44e746492d', '82021420', 'Virginia', '82', 'Castellanos'),
    ('9708671b-3e46-5b84-ab9b-0b33371e0cf2', '82021430', 'Zenón Pereyra', '82', 'Castellanos'),
    ('82c3878c-125f-505f-ad0e-e642f015c67e', '82028010', 'Alcorta', '82', 'Constitución'),
    ('8cbe8228-5dda-5e2a-8297-81d631f7a101', '82028020', 'Barrio Arroyo del Medio', '82', 'Constitución'),
    ('a748aa95-f869-5aa4-8fd7-21e2276bbdc5', '82028030', 'Barrio Mitre', '82', 'Constitución'),
    ('14cb72bd-f078-5057-a615-53e371dc2f62', '82028040', 'Bombal', '82', 'Constitución'),
    ('fd768ab0-d9c7-5c38-9d1c-1174d915aca5', '82028050', 'Cañada Rica', '82', 'Constitución'),
    ('97e50956-4ea8-5660-a6bc-d31269507f17', '82028060', 'Cepeda', '82', 'Constitución'),
    ('a8bc87ec-f168-5e7e-850d-2c24d1701f97', '82028070', 'Empalme Villa Constitución', '82', 'Constitución'),
    ('9b49f032-dd06-5741-8523-d9ce7f1a5dd3', '82028080', 'Firmat', '82', 'Constitución'),
    ('23245925-6e70-551f-b5cd-add8f409fd63', '82028090', 'General Gelly', '82', 'Constitución'),
    ('4950651c-d019-5101-bed5-b3f1f221dfdd', '82028100', 'Godoy', '82', 'Constitución'),
    ('03753211-0f79-5055-9823-4444ed66d5c6', '82028110', 'Juan B. Molina', '82', 'Constitución'),
    ('265e754f-9d4b-592d-bac7-5cd9841f0c9f', '82028120', 'Juncal', '82', 'Constitución'),
    ('6ca89f42-126c-5cb4-a8a1-256816a9dd50', '82028130', 'La Vanguardia', '82', 'Constitución'),
    ('14bb47ac-1ae3-5493-b893-91f73de5bb7a', '82028140', 'Máximo Paz', '82', 'Constitución'),
    ('2a9c3044-f4c3-5c38-9f5c-6c52d992deac', '82028150', 'Pavón', '82', 'Constitución'),
    ('0d0370fb-e7a3-5839-8200-24a64610c1f4', '82028160', 'Pavón Arriba', '82', 'Constitución'),
    ('27db72eb-04c6-50dd-9e23-d316b32b0081', '82028170', 'Peyrano', '82', 'Constitución'),
    ('3d2035e3-e435-5b1c-acd0-032c81bc616c', '82028180', 'Rueda', '82', 'Constitución'),
    ('35bc62e6-680c-5026-9e98-770ebe990391', '82028190', 'Santa Teresa', '82', 'Constitución'),
    ('eceff285-b21f-5231-af25-ff976b3fe190', '82028200', 'Sargento Cabral', '82', 'Constitución'),
    ('19535c87-6368-5153-9b17-c5a62c80be5d', '82028210', 'Stephenson', '82', 'Constitución'),
    ('e04ccfc7-07ab-5d8a-9346-f6917f97ec05', '82028220', 'Theobald', '82', 'Constitución'),
    ('921691d5-d62d-5451-a4fc-32fded7e746b', '82028230', 'Villa Constitución', '82', 'Constitución'),
    ('3575957d-b8b3-58b8-8348-94dc3f87a1b9', '82035010', 'Cayastá', '82', 'Garay'),
    ('152d15cd-245f-5448-9d39-2c045fd102b8', '82035020', 'Helvecia', '82', 'Garay'),
    ('b56b7307-c4ab-5bae-bdad-b67f48fd1ea6', '82035030', 'Los Zapallos', '82', 'Garay'),
    ('23584b10-01d6-5cd9-b12f-4aca73476960', '82035040', 'Saladero Mariano Cabal', '82', 'Garay'),
    ('a73b5726-9553-5fa3-80de-e0c3ccb47340', '82035050', 'Santa Rosa de Calchines', '82', 'Garay'),
    ('9b40461c-766b-5fc8-a7c8-3b166a33dc48', '82042010', 'Aarón Castellanos', '82', 'General López'),
    ('93e8fb48-ecf2-52cf-a7b6-1ff116b5da6e', '82042020', 'Amenábar', '82', 'General López'),
    ('34306e5c-a27a-5156-a1f4-2ef030b33372', '82042030', 'Cafferata', '82', 'General López'),
    ('2a124298-7129-5730-a043-094f46b2cb44', '82042040', 'Cañada del Ucle', '82', 'General López'),
    ('dcdea8d1-106e-5d4b-9cfd-ecd30ae48086', '82042050', 'Carmen', '82', 'General López'),
    ('7ef5c8e0-d240-5fbc-a4b6-a0325d66aabd', '82042060', 'Carreras', '82', 'General López'),
    ('9920771c-9048-5cfa-854b-d6ac04e9bcd8', '82042070', 'Chapuy', '82', 'General López'),
    ('55586f49-f126-55ca-9f1d-9804e08d51cc', '82042080', 'Chovet', '82', 'General López'),
    ('971e0427-6815-5399-88d3-0a8f4ad93e47', '82042090', 'Christophersen', '82', 'General López'),
    ('f28fe560-8dcc-5b0d-91df-09ac65aa6ab0', '82042100', 'Diego de Alvear', '82', 'General López'),
    ('3b9a1d83-3449-57af-9190-b0e508aac883', '82042110', 'Elortondo', '82', 'General López'),
    ('08da0e45-87a0-5201-82ca-a0388a0eb0af', '82042120', 'Firmat', '82', 'General López'),
    ('3a419d96-2d31-5eb7-ba0e-6facc8ee33c6', '82042130', 'Hughes', '82', 'General López'),
    ('21f779f1-d7f0-50ca-bc49-68f86cf6a227', '82042140', 'La Chispa', '82', 'General López'),
    ('1b12f520-7fde-5001-89cf-536cca76d221', '82042150', 'Labordeboy', '82', 'General López'),
    ('2ff667cd-bdc6-5935-ba64-5b2392539cd7', '82042160', 'Lazzarino', '82', 'General López'),
    ('51de9b81-8282-5e3c-a99b-25164056caba', '82042170', 'Maggiolo', '82', 'General López'),
    ('0a57080a-8424-558d-bfb8-669f05bf7f60', '82042180', 'María Teresa', '82', 'General López'),
    ('8cd13a13-b0a1-561b-9b13-eb33e769cef4', '82042190', 'Melincué', '82', 'General López'),
    ('545f1b30-c18e-5fd5-9177-f3b46fda1661', '82042200', 'Miguel Torres', '82', 'General López'),
    ('90230d23-c2a0-5a15-a646-b1e6773cb0cf', '82042210', 'Murphy', '82', 'General López'),
    ('895ae127-6f36-525a-a138-cb83185f6b2a', '82042220', 'Rufino', '82', 'General López'),
    ('215d1e61-ae39-50cc-a948-c69f748cdbbb', '82042230', 'San Eduardo', '82', 'General López'),
    ('a009641c-4da2-549f-ab42-3dc3c44293c9', '82042240', 'San Francisco de Santa Fe', '82', 'General López'),
    ('eb5c0be3-bcf1-56ba-88c4-f9b7715f7687', '82042250', 'San Gregorio', '82', 'General López'),
    ('5ec16b65-9fe1-599a-ba84-7be1f9fbf612', '82042260', 'Sancti Spiritu', '82', 'General López'),
    ('fc9a40f9-2078-5628-8905-148d8ec7bb05', '82042270', 'Santa Isabel', '82', 'General López'),
    ('bf287b79-4629-5210-acf5-a99c7a435fb2', '82042280', 'Teodelina', '82', 'General López'),
    ('1f3de07c-1acb-5e26-914c-58810acb348a', '82042290', 'Venado Tuerto', '82', 'General López'),
    ('ecc6ea33-649f-573b-8259-8eb218341199', '82042300', 'Villa Cañás', '82', 'General López'),
    ('9cb6dfe6-0edf-5125-b9c4-795607b77729', '82042310', 'Wheelwright', '82', 'General López'),
    ('46be069f-deb2-5101-acb6-ae6b301bacf7', '82049010', 'Arroyo Ceibal', '82', 'General Obligado'),
    ('0e72dfa7-db27-52a0-b08a-3b22a08c40ee', '82049020', 'Avellaneda', '82', 'General Obligado'),
    ('ce522ad1-2276-5400-a333-8dbd23009a02', '82049030', 'Berna', '82', 'General Obligado'),
    ('5ffebbe2-46f4-5c3f-b6d4-ea5cc8f5b00b', '82049040', 'El Araza', '82', 'General Obligado'),
    ('c86cefb9-7aee-55a1-98b5-2347f07baca3', '82049050', 'El Rabón', '82', 'General Obligado'),
    ('b6545be0-afc4-512e-9f7f-f05b889f3b25', '82049060', 'Florencia', '82', 'General Obligado'),
    ('5802b1bd-444d-5894-b62d-be7277bea361', '82049070', 'Guadalupe Norte', '82', 'General Obligado'),
    ('c3626b2f-e7e1-5113-a055-f87a0790b9ce', '82049080', 'Ingeniero Chanourdie', '82', 'General Obligado'),
    ('91f1fac3-7f64-525e-8478-a29f5955d3a8', '82049090', 'La Isleta', '82', 'General Obligado'),
    ('6229112b-3167-5b8f-986f-17688d689cfe', '82049100', 'La Sarita', '82', 'General Obligado'),
    ('486bcda0-d7c6-5820-8eaf-6729eb96932d', '82049110', 'Lanteri', '82', 'General Obligado'),
    ('8ee7da0e-b3b6-5418-a781-d14b1ff4adc2', '82049120', 'Las Garzas', '82', 'General Obligado'),
    ('60d41e16-44f4-578c-ba45-e8ed2d1f542c', '82049130', 'Las Toscas', '82', 'General Obligado'),
    ('bbe8d8a4-bd39-5d36-bf4c-f4d1ae7d9cfb', '82049140', 'Los Laureles', '82', 'General Obligado'),
    ('8a6e0a8f-6e87-5dc6-b997-ad7815446e40', '82049150', 'Malabrigo', '82', 'General Obligado'),
    ('f9d998c8-4263-5d2f-b12c-18c2a65ba404', '82049160', 'Paraje San Manuel', '82', 'General Obligado'),
    ('5d62ff32-d8c9-52f0-97c5-5818e792cbce', '82049170', 'Puerto Reconquista', '82', 'General Obligado'),
    ('33e145fd-0014-5558-a4f3-e244a896285b', '82049180', 'Reconquista', '82', 'General Obligado'),
    ('c3206b7b-df14-5cb4-8abf-ed2d0dd4749c', '82049190', 'San Antonio de Obligado', '82', 'General Obligado'),
    ('fe914c04-8737-506d-b6c3-1945e62424bc', '82049200', 'Tacuarendí', '82', 'General Obligado'),
    ('553eef5e-cb41-5312-a91d-1ff7b6228ce4', '82049210', 'Villa Ana', '82', 'General Obligado'),
    ('e2fad983-cdea-531b-9f04-3fe3249fcc61', '82049220', 'Villa Guillermina', '82', 'General Obligado'),
    ('4b183aa4-2e57-5a85-bc5f-fa5ead945b40', '82049230', 'Villa Ocampo', '82', 'General Obligado'),
    ('a8191a0c-bc24-5444-a851-99631b8ecb2c', '82056010', 'Barrio Cicarelli', '82', 'Iriondo'),
    ('088aed94-0b23-5103-b1ec-435899cd87e8', '82056020', 'Bustinza', '82', 'Iriondo'),
    ('2374a476-f524-51e5-8d35-18e8e51c7f22', '82056030', 'Cañada de Gómez', '82', 'Iriondo'),
    ('7b0e7d99-c5b9-5454-99f6-af2cb77e616f', '82056040', 'Carrizales', '82', 'Iriondo'),
    ('bfc53b5f-9e7b-56ef-b6cd-1952929273e8', '82056050', 'Classon', '82', 'Iriondo'),
    ('d6ea527c-5fda-58a1-829c-2ecba868bcaa', '82056060', 'Colonia Médici', '82', 'Iriondo'),
    ('8b678ad7-c7e7-524f-ad40-d8fe0393a986', '82056070', 'Correa', '82', 'Iriondo'),
    ('bbd3790e-8200-5893-8417-73dad981c467', '82056080', 'Larguía', '82', 'Iriondo'),
    ('fea80649-dde6-50ab-b6f1-74273fb035db', '82056090', 'Lucio V. López', '82', 'Iriondo'),
    ('71631a1c-bebd-5766-8ea2-0767dbe597d8', '82056100', 'Oliveros', '82', 'Iriondo'),
    ('d058fee1-c2cc-5e7c-b7c7-d40f62389c9d', '82056110', 'Pueblo Andino', '82', 'Iriondo'),
    ('09e35e89-e295-5633-9446-0e3f017000c4', '82056120', 'Salto Grande', '82', 'Iriondo'),
    ('1ca9008c-daa1-5751-b2e0-feb3afe5d776', '82056130', 'Serodino', '82', 'Iriondo'),
    ('d42bca55-6e67-5a40-9628-026d437973f0', '82056140', 'Totoras', '82', 'Iriondo'),
    ('c1c5758d-e109-5347-ac6b-70d49a76d3f9', '82056150', 'Villa Eloísa', '82', 'Iriondo'),
    ('49a7ecc7-d35c-5979-94d1-4d0a5552e952', '82056160', 'Villa La Rivera (Oliveros)', '82', 'Iriondo'),
    ('b5885bdd-dccb-5765-98fc-a6da828ca516', '82056170', 'Villa La Rivera (Pueblo Andino)', '82', 'Iriondo'),
    ('1a7d26e5-e68b-5819-8f35-24c99823248f', '82063010', 'Angel Gallardo', '82', 'La Capital'),
    ('559d3a94-a4f7-5951-9139-532444e7d10a', '82063020', 'Arroyo Aguiar', '82', 'La Capital'),
    ('5e9e414e-aedb-5e8e-bc0f-326fb85056ad', '82063030', 'Arroyo Leyes', '82', 'La Capital'),
    ('48860612-0960-5355-91a1-160d191fc5e8', '8206303001', 'Arroyo Leyes', '82', 'La Capital'),
    ('e4f57f23-8f6c-590e-8909-9a395ff65a28', '8206303002', 'Rincón Norte', '82', 'La Capital'),
    ('1488dd1a-e997-5fa5-b5f7-9a2fba781957', '82063040', 'Cabal', '82', 'La Capital'),
    ('2ec052a6-c009-551e-b521-21d9d2e2d21b', '82063050', 'Campo Andino', '82', 'La Capital'),
    ('ac3bbd8d-cbb2-5654-857f-a26adb01c3b7', '82063060', 'Candioti', '82', 'La Capital'),
    ('6cbee4bb-63eb-5758-b47c-cdfe6b70dd91', '82063070', 'Emilia', '82', 'La Capital'),
    ('5d6c66cb-f977-519e-9176-650748890360', '82063080', 'Laguna Paiva', '82', 'La Capital'),
    ('a10d4369-aa0b-5a5f-82c6-8a258b7b4dd6', '82063090', 'Llambi Campbell', '82', 'La Capital'),
    ('b2e310eb-fa9b-504e-bd01-8688ca4a953b', '82063100', 'Monte Vera', '82', 'La Capital'),
    ('1e549d43-939e-5300-a781-9e0396c7472f', '82063110', 'Nelson', '82', 'La Capital'),
    ('814b958c-15e8-50bf-97f8-1a7ec6ff22f7', '82063120', 'Paraje Chaco Chico', '82', 'La Capital'),
    ('cfd9ec88-c96f-5531-92e0-dcbb212f9e22', '82063130', 'Paraje La Costa', '82', 'La Capital'),
    ('6fe84f26-f707-591f-9cf5-e0f038e0e8cb', '82063140', 'Recreo', '82', 'La Capital'),
    ('377db967-f12e-56d6-971f-1388474f1211', '82063150', 'Rincón Potrero', '82', 'La Capital'),
    ('dd43872a-994f-5d01-83b7-5fc8ee32879e', '82063160', 'San José del Rincón', '82', 'La Capital'),
    ('a4587734-261e-541e-9293-c3438c0cda21', '82063170', 'Santa Fe', '82', 'La Capital'),
    ('e7139389-69c2-5c2c-9fce-ccdc128cf49d', '82063180', 'Santo Tomé', '82', 'La Capital'),
    ('c38c1487-e644-5992-9021-94801f7b63e6', '82063190', 'Sauce Viejo', '82', 'La Capital'),
    ('71933efb-4702-58b0-972e-d199e95454b3', '8206319001', 'Sauce Viejo', '82', 'La Capital'),
    ('05dbcdc7-3403-5800-ad0a-1bc6b2f2efcb', '8206319002', 'Villa Adelina', '82', 'La Capital'),
    ('6f1a2f55-fc48-53c7-851d-35a473f8d23d', '82063200', 'Villa Laura', '82', 'La Capital'),
    ('4ffa54a2-8d73-5cbc-9443-2538b3c5a5d4', '82070010', 'Cavour', '82', 'Las Colonias'),
    ('5bf5c389-0b0c-55e2-996b-c235a6911e5d', '82070020', 'Cululú', '82', 'Las Colonias'),
    ('6035dcdc-920a-5b26-b75b-7ba999548644', '82070030', 'Elisa', '82', 'Las Colonias'),
    ('6b838c1d-b57f-5d90-aca3-8aba479828b0', '82070040', 'Empalme San Carlos', '82', 'Las Colonias'),
    ('2228260c-3022-528b-8492-4c16d5332b5d', '82070050', 'Esperanza', '82', 'Las Colonias'),
    ('256b120f-2187-5be3-b8f2-68a338ac1aa8', '82070060', 'Felicia', '82', 'Las Colonias'),
    ('0eb99e35-6a28-57d1-b60d-b5608c16fdfb', '82070070', 'Franck', '82', 'Las Colonias'),
    ('2d91e013-5c69-5c7b-b148-1221d8e912b0', '82070080', 'Grutly', '82', 'Las Colonias'),
    ('22876b71-1f7d-5e0d-a8c1-3f110a309213', '82070090', 'Hipatía', '82', 'Las Colonias'),
    ('a6bc844a-e16e-5d28-8aec-7f847a978a49', '82070100', 'Humboldt', '82', 'Las Colonias'),
    ('27e2c667-d237-5c74-8770-cb3f9a99433a', '82070110', 'Jacinto L. Aráuz', '82', 'Las Colonias'),
    ('96e56ca2-77a2-5ca2-9360-570980336b55', '82070120', 'La Pelada', '82', 'Las Colonias'),
    ('a69fc5bf-d8a3-5b20-9bf5-4fcd366471ba', '82070130', 'Las Tunas', '82', 'Las Colonias'),
    ('cc530f92-6576-5cd8-8cef-9defc52bc150', '82070140', 'María Luisa', '82', 'Las Colonias'),
    ('4705ab9a-bafe-5e6c-a6c9-ad27e2ce55c9', '82070150', 'Matilde', '82', 'Las Colonias'),
    ('944d71ed-e852-5a63-b5b0-4e0c3c0bd465', '82070160', 'Nuevo Torino', '82', 'Las Colonias'),
    ('d558a1c6-23fa-5a0d-baf4-914e7a4d6e95', '82070170', 'Pilar', '82', 'Las Colonias'),
    ('53c78406-aa29-5889-b6bb-1c4283c01de8', '82070180', 'Plaza Matilde', '82', 'Las Colonias'),
    ('a7f76476-0123-5522-9ea3-d38341962211', '82070190', 'Progreso', '82', 'Las Colonias'),
    ('7966ba4a-caa9-5b02-a65c-9866ac816096', '82070200', 'Providencia', '82', 'Las Colonias'),
    ('d1a0c5ac-d4a6-5939-aed9-328987c2a218', '82070210', 'Sa Pereyra', '82', 'Las Colonias'),
    ('51483b5d-597f-5e90-bf2a-c89a4b45c155', '82070220', 'San Agustín', '82', 'Las Colonias'),
    ('01521385-1dc3-5a57-8ac8-c0fdf728072f', '82070230', 'San Carlos Centro', '82', 'Las Colonias'),
    ('2349b1fd-587c-5298-978f-9978a8e13f5f', '82070240', 'San Carlos Norte', '82', 'Las Colonias'),
    ('bb145c09-9074-5647-8f23-5474315c367d', '82070250', 'San Carlos Sud', '82', 'Las Colonias'),
    ('d9142cab-1d0e-5609-82f5-4f395c58be8c', '82070260', 'San Jerónimo del Sauce', '82', 'Las Colonias'),
    ('45cb1eb5-1290-5bab-91cb-825332126abe', '82070270', 'San Jerónimo Norte', '82', 'Las Colonias'),
    ('d1dc72e3-26cb-53d8-8bd3-32552b13834d', '82070280', 'San Mariano', '82', 'Las Colonias'),
    ('2d28100d-f4f4-5ab2-8013-6a61e27bed94', '82070290', 'Santa Clara de Buena Vista', '82', 'Las Colonias'),
    ('8d84496d-3939-5695-83fa-2f7b296e44e1', '82070300', 'Santo Domingo', '82', 'Las Colonias'),
    ('b3c51ad9-0d42-5308-8437-4ec45109e6a8', '82070310', 'Sarmiento', '82', 'Las Colonias'),
    ('12c9d2a9-0dd1-54eb-9ca5-7674a6961781', '82077010', 'Esteban Rams', '82', '9 de Julio'),
    ('c7c0aac2-226e-541f-9dc7-e27af107db6a', '82077020', 'Gato Colorado', '82', '9 de Julio'),
    ('a3eadc2b-e116-50e9-b873-b9afe2031a84', '82077030', 'Gregoria Pérez de Denis', '82', '9 de Julio'),
    ('3a43f9ad-0477-5d59-8362-0f659baeae0f', '82077040', 'Logroño', '82', '9 de Julio'),
    ('33fff195-9c8a-5e3e-b738-df34ff8b5efd', '82077050', 'Montefiore', '82', '9 de Julio'),
    ('df78f90e-e31b-5b52-8865-8e42074b9495', '82077060', 'Pozo Borrado', '82', '9 de Julio')
ON CONFLICT (indec_id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('9ba567db-3231-5e87-9a6c-6547a3dfcc26', '82077065', 'San Bernardo', '82', '9 de Julio'),
    ('7f824687-422d-5f26-a0c0-8fc69063a2b7', '82077070', 'Santa Margarita', '82', '9 de Julio'),
    ('9dcda47e-33bd-51f0-a6fa-f4dc8ce462e2', '82077080', 'Tostado', '82', '9 de Julio'),
    ('78a23d59-65af-5d76-836f-3d87b2149c26', '82077090', 'Villa Minetti', '82', '9 de Julio'),
    ('dbfe4f99-ae9b-5163-9192-869210563a34', '82084010', 'Acébal', '82', 'Rosario'),
    ('3cd213b5-9b97-575b-87cd-c7e94ed09f5e', '82084020', 'Albarellos', '82', 'Rosario'),
    ('43e6a7e5-6269-5bdb-8145-41332f8caceb', '82084030', 'Álvarez', '82', 'Rosario'),
    ('f94f78a7-569b-50c4-b196-0a421da235dd', '82084040', 'Alvear', '82', 'Rosario'),
    ('058140c5-c721-5025-a357-e6305d2f9a4c', '82084050', 'Arbilla', '82', 'Rosario'),
    ('4be50973-c456-5efd-85dd-063216d0aff7', '82084060', 'Arminda', '82', 'Rosario'),
    ('c34a733d-7869-5db9-a45e-ec10bb81af33', '82084070', 'Arroyo Seco', '82', 'Rosario'),
    ('cad2b827-86af-522f-9441-08aa2258aabd', '82084080', 'Carmen del Sauce', '82', 'Rosario'),
    ('e537c679-4c9d-53cb-9375-ca1b5079b2f5', '82084090', 'Coronel Bogado', '82', 'Rosario'),
    ('3c89bad8-62d9-5d82-a8d3-3faee8bc6b15', '82084100', 'Coronel Rodolfo S. Domínguez', '82', 'Rosario'),
    ('a2632645-f141-5f71-959d-cda13b9ca6eb', '82084110', 'Cuatro Esquinas', '82', 'Rosario'),
    ('730925a8-4f2e-5721-b3b7-50885a612040', '82084120', 'El Caramelo', '82', 'Rosario'),
    ('a4fe0727-2cdd-586d-bf51-c7c183e256cf', '82084130', 'Fighiera', '82', 'Rosario'),
    ('52c7a141-c8e4-551e-b3fa-ab162a7aa5ad', '82084140', 'Funes', '82', 'Rosario'),
    ('52cb7858-15b2-5afd-95f6-1f1c5456cfb3', '82084150', 'General Lagos', '82', 'Rosario'),
    ('636c4504-8cd4-5145-a1da-3581753c0027', '82084160', 'Granadero Baigorria', '82', 'Rosario'),
    ('6f7448c4-ad11-5f8b-a116-39ff360d2867', '82084170', 'Ibarlucea', '82', 'Rosario'),
    ('7d6ebbb1-ff37-598e-8fc1-86e1da0ac1bf', '82084180', 'Kilómetro 101', '82', 'Rosario'),
    ('3cbf619a-f4e0-5ae8-aef3-7eb7cedd6905', '82084190', 'Los Muchachos - La Alborada', '82', 'Rosario'),
    ('4b532809-a3f1-53ee-a660-adb0b368e8f5', '82084200', 'Monte Flores', '82', 'Rosario'),
    ('5bf5d1fb-445f-5df3-b9e7-eb04d5b808d1', '82084210', 'Pérez', '82', 'Rosario'),
    ('a6160733-b0f6-5cd5-9cc7-0b846d756277', '82084220', 'Piñero', '82', 'Rosario'),
    ('01f86506-87d7-5dd8-896c-52c3df6087f3', '82084230', 'Pueblo Esther', '82', 'Rosario'),
    ('83264596-d5a0-5a2e-b3f0-f0457cc345f4', '82084240', 'Pueblo Muñóz', '82', 'Rosario'),
    ('0bc5096b-1771-528a-8762-feab8fc8d6c7', '82084250', 'Pueblo Uranga', '82', 'Rosario'),
    ('bca1b352-127f-5d0d-ba79-aa274af005d6', '82084260', 'Puerto Arroyo Seco', '82', 'Rosario'),
    ('8320dc31-9344-5c27-a566-91d98d45f01b', '82084270', 'Rosario', '82', 'Rosario'),
    ('b217e8f8-adff-51e2-85cd-d5ba4d06a7b4', '82084280', 'Soldini', '82', 'Rosario'),
    ('1e6c647e-3c8d-5f52-9cb8-2e1b3887758d', '82084290', 'Villa Amelia', '82', 'Rosario'),
    ('08d7d1db-9f86-536d-860a-5e772f3eff5c', '82084300', 'Villa del Plata', '82', 'Rosario'),
    ('89873e37-729b-5832-9031-65d336750410', '82084310', 'Villa Gobernador Gálvez', '82', 'Rosario'),
    ('f16bde3b-8969-518c-91ec-8e075be88bda', '82084320', 'Zavalla', '82', 'Rosario'),
    ('57f2ab5d-b525-513c-9b44-5a81fabf5b1d', '82091010', 'Aguará Grande', '82', 'San Cristóbal'),
    ('c6e6cb40-6556-5f3c-bc38-4c04b447ffb3', '82091020', 'Ambrosetti', '82', 'San Cristóbal'),
    ('7114f845-dc81-5a60-8b0d-fc819057757a', '82091030', 'Arrufo', '82', 'San Cristóbal'),
    ('25766d97-f275-56ec-af68-14a24a8256a9', '82091040', 'Balneario La Verde', '82', 'San Cristóbal'),
    ('2974d528-f012-5add-a0ab-ee65cc48562f', '82091050', 'Capivara', '82', 'San Cristóbal'),
    ('857d244d-040f-55e2-ab0b-32f3a7ce351c', '82091060', 'Ceres', '82', 'San Cristóbal'),
    ('af716400-15d6-5d71-ba2b-9370ef2b6045', '82091070', 'Colonia Ana', '82', 'San Cristóbal'),
    ('dc3c3ba3-522e-519a-8d19-4efca4294f35', '82091080', 'Colonia Bossi', '82', 'San Cristóbal'),
    ('059d6f38-c7d2-5694-9bf8-283a022be2bd', '82091090', 'Colonia Rosa', '82', 'San Cristóbal'),
    ('df8c0bb9-24de-5195-9fb9-84791f21535a', '82091100', 'Constanza', '82', 'San Cristóbal'),
    ('410d7ba0-daf1-54d3-92fe-fceebd507042', '82091110', 'Curupaytí', '82', 'San Cristóbal'),
    ('89a1bf7b-1a84-5d81-b4f4-49c52b6f92f6', '82091120', 'Hersilia', '82', 'San Cristóbal'),
    ('5d9595a6-8469-56b7-b35c-7e2cce827493', '82091130', 'Huanqueros', '82', 'San Cristóbal'),
    ('959fc6b4-79f0-5e87-b5eb-dad070a63182', '82091140', 'La Cabral', '82', 'San Cristóbal'),
    ('a7819295-cb37-5bde-992c-6ab107cc1a1f', '82091145', 'La Lucila', '82', 'San Cristóbal'),
    ('0cf559f4-235a-5010-93df-eddf3e7046ca', '82091150', 'La Rubia', '82', 'San Cristóbal'),
    ('afe2311e-2031-5a82-a42b-53cf760fd4f5', '82091160', 'Las Avispas', '82', 'San Cristóbal'),
    ('d576656b-931b-5b1f-89cd-cb322567aad0', '82091170', 'Las Palmeras', '82', 'San Cristóbal'),
    ('30229196-9d44-5b3a-b882-0d7b0761c3f9', '82091180', 'Moisés Ville', '82', 'San Cristóbal'),
    ('d5b3891c-a2a9-5566-b8a2-950341acee16', '82091190', 'Monigotes', '82', 'San Cristóbal'),
    ('7143a05a-e84e-5b21-a05a-e0685cd57831', '82091200', 'Ñanducita', '82', 'San Cristóbal'),
    ('99c711fb-aacf-570a-99fa-af7027f83d53', '82091210', 'Palacios', '82', 'San Cristóbal'),
    ('2985962e-a889-5889-9caa-86b09a32c1b3', '82091220', 'San Cristóbal', '82', 'San Cristóbal'),
    ('81cfa9d2-ac8e-569f-a3fa-ad48f08b4e02', '82091230', 'San Guillermo', '82', 'San Cristóbal'),
    ('25483e88-ecf4-5416-a609-882ea4ee6d0e', '82091240', 'Santurce', '82', 'San Cristóbal'),
    ('0e418944-b190-59d3-a4f2-acb687a2e7a8', '82091250', 'Soledad', '82', 'San Cristóbal'),
    ('37ba3220-f6da-521e-bb89-fb099139e43f', '82091260', 'Suardi', '82', 'San Cristóbal'),
    ('78d457a0-1f8c-5a9b-8be6-e970904715dd', '82091270', 'Villa Saralegui', '82', 'San Cristóbal'),
    ('608ac094-55e1-5b7b-abe5-1addfbfb8046', '82091280', 'Villa Trinidad', '82', 'San Cristóbal'),
    ('36fb2cf0-cd9f-56ba-b5a8-9e44e8c2af34', '82098010', 'Alejandra', '82', 'San Javier'),
    ('0c43fe79-3b06-58b2-8df4-a98522c9ef27', '82098020', 'Cacique Ariacaiquín', '82', 'San Javier'),
    ('66492e65-70e4-505a-9f65-232703fac647', '82098030', 'Colonia Durán', '82', 'San Javier'),
    ('9eacffd9-9d9d-5d57-9dbc-286f63a7f8f8', '82098040', 'La Brava', '82', 'San Javier'),
    ('05559843-795f-52b0-8fa7-5f6b4101378a', '82098050', 'Romang', '82', 'San Javier'),
    ('678e694a-8523-5495-a47c-83c12d919384', '82098060', 'San Javier', '82', 'San Javier'),
    ('fb58ea2b-7971-5e6f-90a1-26c38904b068', '82105010', 'Arocena', '82', 'San Jerónimo'),
    ('b5a5afdf-5a81-5709-94d4-7d0b038805b7', '82105020', 'Balneario Monje', '82', 'San Jerónimo'),
    ('4ac1676c-33c9-57a7-80bd-3b7ddd679c40', '82105030', 'Barrancas', '82', 'San Jerónimo'),
    ('270f5329-4958-5945-9428-1d4ec47adce2', '82105040', 'Barrio Caima', '82', 'San Jerónimo'),
    ('2a624ad4-68c6-5f64-8587-a8ad0cdeae2b', '82105050', 'Barrio El Pacaá - Barrio Comipini', '82', 'San Jerónimo'),
    ('9c0bfaaf-e6fb-5abd-b644-949261bc602d', '82105060', 'Bernardo de Irigoyen', '82', 'San Jerónimo'),
    ('4cce162c-d4ec-5a07-8e0a-0ece50b90186', '82105070', 'Casalegno', '82', 'San Jerónimo'),
    ('881b7338-a012-5950-8141-6d4554adf858', '82105080', 'Centeno', '82', 'San Jerónimo'),
    ('2447bf2c-1752-54ef-b10b-b0abd1fa4cf3', '82105090', 'Coronda', '82', 'San Jerónimo'),
    ('c961020d-c125-5871-9a3c-658a659ec46d', '82105100', 'Desvío Arijón', '82', 'San Jerónimo'),
    ('1ec045db-6524-5d61-934a-62f6cae16590', '82105110', 'Díaz', '82', 'San Jerónimo'),
    ('c5360a0c-f5d7-5fdc-a1a7-cdf501dddbe0', '82105120', 'Gaboto', '82', 'San Jerónimo'),
    ('ce80586d-57ee-5871-a890-f0f732416e98', '82105130', 'Gálvez', '82', 'San Jerónimo'),
    ('4074fb36-f9a8-51f8-bd80-9c532864a253', '82105140', 'Gessler', '82', 'San Jerónimo'),
    ('a305f290-3626-5cf8-b278-228a84b7de29', '82105150', 'Irigoyen', '82', 'San Jerónimo'),
    ('c5a5fd63-0959-56d9-ba89-2acab178ce8b', '82105160', 'Larrechea', '82', 'San Jerónimo'),
    ('64f701a7-5055-56f4-9b36-8af2605516b0', '82105170', 'Loma Alta', '82', 'San Jerónimo'),
    ('e3573189-2e1c-5331-9b78-e962397019f9', '82105180', 'López', '82', 'San Jerónimo'),
    ('4f788e31-1448-5321-9518-76ea887a2bfe', '82105190', 'Maciel', '82', 'San Jerónimo'),
    ('8e7fde16-79c7-53de-a560-f308bd5a21b6', '82105200', 'Monje', '82', 'San Jerónimo'),
    ('a323be10-0a24-5c5a-ba26-923dfc3ee74c', '82105210', 'Puerto Aragón', '82', 'San Jerónimo'),
    ('bd5a6321-6e68-5d9c-bcfb-1ce1cbbb4bb2', '82105220', 'San Eugenio', '82', 'San Jerónimo'),
    ('b5626eca-d3ad-578b-8727-3ae3efdca4ca', '82105230', 'San Fabián', '82', 'San Jerónimo'),
    ('4873fa61-4fa8-52f5-bc88-4a0accd76e31', '82105240', 'San Genaro', '82', 'San Jerónimo'),
    ('768fb9d8-9d4b-59ee-9c0f-69c6c108f280', '82105250', 'San Genaro Norte', '82', 'San Jerónimo'),
    ('ca1e9ba0-a8e5-5c10-9751-acc9697383cd', '82112010', 'Angeloni', '82', 'San Justo'),
    ('4538c782-2157-5886-babb-143fc271a05e', '82112020', 'Cayastacito', '82', 'San Justo'),
    ('573c7b67-bed3-5d2d-a907-5f071841d264', '82112030', 'Colonia Dolores', '82', 'San Justo'),
    ('cb239511-fbc2-5972-b89b-b1310a2e8d40', '82112040', 'Esther', '82', 'San Justo'),
    ('3d1d840b-f447-5f46-aa11-ce2ba78da865', '82112050', 'Gobernador Crespo', '82', 'San Justo'),
    ('bd7d2cd2-f263-58a2-87e6-2e50c3e252df', '82112060', 'La Criolla', '82', 'San Justo'),
    ('626f35cd-7869-5225-8b60-9c7f95e92b1e', '82112070', 'La Penca y Caraguatá', '82', 'San Justo'),
    ('67f63fe4-0029-5f1e-8d07-f2510251862a', '82112080', 'Marcelino Escalada', '82', 'San Justo'),
    ('788c2da7-b99e-503d-9fde-4bf20e6b38be', '82112090', 'Naré', '82', 'San Justo'),
    ('a5d3011a-2f28-5722-9f4d-d51635e9a67d', '82112100', 'Pedro Gómez Cello', '82', 'San Justo'),
    ('6aae30b5-2f1b-557f-88fd-334563a0661e', '82112110', 'Ramayón', '82', 'San Justo'),
    ('124c71b9-f1f5-53eb-95f3-5ab727c608e4', '82112120', 'San Bernardo', '82', 'San Justo'),
    ('e30e7621-158c-53a4-bf15-e76305e4e4e7', '82112130', 'San Justo', '82', 'San Justo'),
    ('76ee0a2a-eb05-5195-836e-17c404f3c6e0', '82112140', 'San Martín Norte', '82', 'San Justo'),
    ('27e70314-7a4c-583d-addb-9eac316c236e', '82112150', 'Silva', '82', 'San Justo'),
    ('823645d1-7ba8-5574-9663-0706658ffd1b', '82112160', 'Vera y Pintado', '82', 'San Justo'),
    ('4bffd66b-39fc-53e9-b6fc-f6e8b61e4519', '82112170', 'Videla', '82', 'San Justo'),
    ('eb94c9c2-4b7d-5b36-bd95-7ffe0c1e52ab', '82119010', 'Aldao', '82', 'San Lorenzo'),
    ('36879116-e795-51c3-aca4-aa6b11e193c0', '82119020', 'Capitán Bermúdez', '82', 'San Lorenzo'),
    ('c7fcb27b-2df6-5ac7-bd2f-9a66e133d864', '82119030', 'Carcarañá', '82', 'San Lorenzo'),
    ('11630b7f-adf9-55a7-a579-7f3eeaee1fb6', '82119040', 'Coronel Arnold', '82', 'San Lorenzo'),
    ('b0674fc6-8078-5371-ac54-70763f79a6f3', '82119050', 'Fray Luis Beltrán', '82', 'San Lorenzo'),
    ('a56139c2-9e49-51eb-8b6b-a630b7c405aa', '82119060', 'Fuentes', '82', 'San Lorenzo'),
    ('8d750402-6173-5b4e-afb1-22b6b85c66cb', '82119070', 'Luis Palacios', '82', 'San Lorenzo'),
    ('d35a1d6c-bc7f-51fe-9e0d-58bf03edf284', '82119080', 'Puerto General San Martín', '82', 'San Lorenzo'),
    ('da9601b2-e66a-5d0a-bff0-37deee352e92', '82119090', 'Pujato', '82', 'San Lorenzo'),
    ('e0dd212e-7c50-5aa5-bfa1-05d561da5261', '82119100', 'Ricardone', '82', 'San Lorenzo'),
    ('bafd118c-76c3-5035-9cd1-07442c9b44d0', '82119110', 'Roldán', '82', 'San Lorenzo'),
    ('3c76d361-02b2-5926-9455-bff1cddb1e71', '82119120', 'San Jerónimo Sud', '82', 'San Lorenzo'),
    ('08079e82-4ff3-556c-b445-70693816efd8', '82119130', 'San Lorenzo', '82', 'San Lorenzo'),
    ('3434f09d-b0c2-59c5-ad29-79dc6e32b833', '82119140', 'Timbúes', '82', 'San Lorenzo'),
    ('e0bca4ec-f8ac-5303-a4c2-2aa34e28efe9', '82119150', 'Villa Elvira', '82', 'San Lorenzo'),
    ('26d52fef-4509-566f-bc4b-a22695e6f83a', '82119160', 'Villa Mugueta', '82', 'San Lorenzo'),
    ('f523aa52-a58a-5143-98fa-19ad1499c893', '82126010', 'Cañada Rosquín', '82', 'San Martín'),
    ('a550f2ee-8058-5b6e-9ca7-fad5f630890f', '82126020', 'Carlos Pellegrini', '82', 'San Martín'),
    ('80885b7e-734c-5bbf-80e8-244934d37984', '82126030', 'Casas', '82', 'San Martín'),
    ('cb3db498-d1d4-5293-a84e-11c4cdb3d594', '82126040', 'Castelar', '82', 'San Martín'),
    ('fb4327a5-94b4-57cc-8a00-aa53971065c8', '82126050', 'Colonia Belgrano', '82', 'San Martín'),
    ('96645b93-3bff-5ebf-a6fa-b3e01873dc04', '82126060', 'Crispi', '82', 'San Martín'),
    ('e4839cbc-708f-5ebb-9e50-b58544a756bb', '82126070', 'El Trébol', '82', 'San Martín'),
    ('167d3247-89a2-5ae3-bf17-a135f906a46a', '82126080', 'Landeta', '82', 'San Martín'),
    ('aa6cfed4-267d-5450-92fb-fe86d2cc821e', '82126090', 'Las Bandurrias', '82', 'San Martín'),
    ('c2264da2-b44a-5e1d-84b4-becca2393d28', '82126100', 'Las Petacas', '82', 'San Martín'),
    ('a89785c4-51d8-5fa1-8fd8-90fe0c6aae83', '82126110', 'Los Cardos', '82', 'San Martín'),
    ('706a420b-bdac-5376-be2b-5d334fa4c485', '82126120', 'María Susana', '82', 'San Martín'),
    ('714dda09-aeb3-5b8c-8d9d-2702c9da80fd', '82126130', 'Piamonte', '82', 'San Martín'),
    ('ada03144-00b6-5a55-b578-5886e8203533', '82126140', 'San Jorge', '82', 'San Martín'),
    ('31520044-ce96-5aae-a5a8-9d47513752a1', '82126150', 'San Martín de las Escobas', '82', 'San Martín'),
    ('7fb802f8-4e4e-5bde-8e80-e7f98577f865', '82126160', 'Sastre', '82', 'San Martín'),
    ('d9188681-a87b-5e81-95f3-a581addcfab5', '82126170', 'Traill', '82', 'San Martín'),
    ('dccca4e5-1a68-54a0-b9a6-92319046d68a', '82126180', 'Wildermuth', '82', 'San Martín'),
    ('47214821-b97a-5ccf-857e-4b5b5e51d337', '82133010', 'Calchaquí', '82', 'Vera'),
    ('d6708ca4-1e7a-513d-9ea1-57d78a100884', '82133020', 'Cañada Ombú', '82', 'Vera'),
    ('a799a3f9-f88b-5258-8cda-ca8f109ee7b0', '82133030', 'Colmena', '82', 'Vera'),
    ('b65aa64b-f009-52b1-b4c6-ddcd99a76e89', '82133040', 'Fortín Olmos', '82', 'Vera'),
    ('fead2a47-56f8-5037-a366-3b126c8da473', '82133050', 'Garabato', '82', 'Vera'),
    ('c26c89df-3381-5475-99bd-4e7962e47c7d', '82133060', 'Golondrina', '82', 'Vera'),
    ('f95ca53c-1918-52a3-9c57-c81fd3dff296', '82133070', 'Intiyaco', '82', 'Vera'),
    ('569d05be-53a2-5f3e-85d5-ba0cc8b71cca', '82133080', 'Kilómetro 115', '82', 'Vera'),
    ('5dc45f30-b2d7-5e5f-a7d4-efdea3fb1260', '82133090', 'La Gallareta', '82', 'Vera'),
    ('ada6e49b-b213-543e-a956-d3404bf80dcd', '82133100', 'Los Amores', '82', 'Vera'),
    ('c8384da5-6e93-5a8e-a34b-4699e7447197', '82133110', 'Margarita', '82', 'Vera'),
    ('7be9af98-f5c1-5e37-b4f6-ed16bbd47daa', '82133120', 'Paraje 29', '82', 'Vera'),
    ('7f400654-e22e-5ddf-8fed-c51af24bb96a', '82133130', 'Pozo de los Indios', '82', 'Vera'),
    ('7ef4a1b1-a2f7-5b41-8f98-7e00a65179d0', '82133140', 'Pueblo Santa Lucía', '82', 'Vera'),
    ('c88fd898-5944-5856-b493-da06ebb7c120', '82133150', 'Tartagal', '82', 'Vera'),
    ('70f2863b-be20-5730-a100-73ba225e564a', '82133160', 'Toba', '82', 'Vera'),
    ('eb9dc191-5807-5cf8-bec7-b4b2a0616415', '82133170', 'Vera', '82', 'Vera'),
    ('a2ab807a-54cd-5546-b360-43a3c99b8fc4', '86007010', 'Argentina', '86', 'Aguirre'),
    ('d0347c68-6a8a-5610-8947-bcafa731c166', '86007020', 'Casares', '86', 'Aguirre'),
    ('b7d3b9dd-2301-5695-9d3d-3ee19750ebb7', '86007030', 'Malbrán', '86', 'Aguirre'),
    ('30317421-6c65-5cb9-a4ac-9d510fbf4f61', '86007040', 'Villa General Mitre', '86', 'Aguirre'),
    ('b743c95c-7a44-52b5-8f8e-89d0b5a495d2', '86014010', 'Campo Gallo', '86', 'Alberdi'),
    ('27da84d4-1526-5f38-9f5b-e2cfbab259c1', '86014020', 'Coronel Manuel L. Rico', '86', 'Alberdi'),
    ('a96494e5-d073-53e4-91d1-5a51fa829464', '86014030', 'Donadeu', '86', 'Alberdi'),
    ('9b68d007-d0c7-5897-9242-24ae695ee9b2', '86014040', 'Sachayoj', '86', 'Alberdi'),
    ('dcd6e3b5-de77-5401-8cc0-3eedddf2a1d7', '86014050', 'Santos Lugares', '86', 'Alberdi'),
    ('73ac4d7b-685e-51cd-bd79-a81d648cbcad', '86021010', 'Estación Atamisqui', '86', 'Atamisqui'),
    ('5b210084-3cfd-5929-855b-7a485048a313', '86021020', 'Medellín', '86', 'Atamisqui'),
    ('25e6a3b2-4566-57d8-9e6e-46b66a2254b4', '86021030', 'Villa Atamisqui', '86', 'Atamisqui'),
    ('3a9f1aae-9159-5ad6-9d50-07e078346c30', '86028010', 'Colonia Dora', '86', 'Avellaneda'),
    ('d1e03d2d-eba3-571c-8073-5a9f64a3e889', '86028020', 'Herrera', '86', 'Avellaneda'),
    ('32a5dd05-03f9-5d14-8240-5ba32a2495ca', '86028030', 'Icaño', '86', 'Avellaneda'),
    ('f001a32f-9b1c-5a03-96f3-717618cc770f', '86028040', 'Lugones', '86', 'Avellaneda'),
    ('f5119736-0748-5703-ad80-3f7e2962f5f0', '86028050', 'Real Sayana', '86', 'Avellaneda'),
    ('55348fba-3ff7-582e-a3a9-68d32340c3da', '86028060', 'Villa Mailín', '86', 'Avellaneda'),
    ('fa2bfa16-2fdb-5b49-bb42-b5b18293f0b0', '86035010', 'Abra Grande', '86', 'Banda'),
    ('c75896b2-a904-5c52-895c-72c4a0b41e40', '86035020', 'Antajé', '86', 'Banda'),
    ('bc076fd1-b42b-51b9-9b53-7ae1106fbd5e', '86035030', 'Ardiles', '86', 'Banda'),
    ('97d804d2-a950-5ea8-9049-464303ee85ff', '86035040', 'Cañada Escobar', '86', 'Banda'),
    ('e0e58fc0-370d-50f4-81d7-d6ce7521dfb0', '86035050', 'Chaupi Pozo', '86', 'Banda'),
    ('b0127883-3dfe-5207-a904-08dff8e960f7', '86035060', 'Clodomira', '86', 'Banda'),
    ('308286f5-010a-5c96-bfcb-e5ca69a0f5fa', '86035070', 'Huyamampa', '86', 'Banda'),
    ('73c1466b-7e06-569c-a0c7-3f7b7ecaac4c', '86035080', 'La Aurora', '86', 'Banda'),
    ('0a22e795-d808-594f-a867-bc71d0f0a08b', '86035090', 'La Banda', '86', 'Banda'),
    ('ad05e7a4-7143-53d3-a764-8065d517bf35', '86035100', 'La Dársena', '86', 'Banda'),
    ('dd5675a7-14f2-5fb2-b85e-6ea601b0217e', '86035110', 'Los Quiroga', '86', 'Banda'),
    ('6a21fab8-2832-5ee9-ba47-251a3ed6bd60', '86035120', 'Los Soria', '86', 'Banda'),
    ('7a421534-146b-592d-bb70-e463595dfe36', '86035130', 'Simbolar', '86', 'Banda'),
    ('c705a207-b435-5852-8810-9035350797f4', '86035140', 'Tramo 16', '86', 'Banda'),
    ('1ae4084c-d022-5c66-a81c-f7881c6cefcd', '86035150', 'Tramo 20', '86', 'Banda'),
    ('812f1ca9-7f0b-5dc8-b32c-cfe88240c2a7', '86042010', 'Bandera', '86', 'Belgrano'),
    ('484601de-09ea-5668-9708-bce3c37bcfdf', '86042020', 'Cuatro Bocas', '86', 'Belgrano'),
    ('765c07a7-2b67-5a49-b6e0-5702d61356dd', '86042030', 'Fortín Inca', '86', 'Belgrano'),
    ('cb1d7071-101d-5384-ba80-6414fe3787a4', '86042040', 'Guardia Escolta', '86', 'Belgrano'),
    ('4e175658-cbbf-565b-a25b-cdbbb9e87bea', '86049010', 'El Deán', '86', 'Capital'),
    ('c98429a3-7235-5c4a-92d9-91db182bd720', '86049020', 'El Mojón', '86', 'Capital'),
    ('928b63eb-21e3-5316-a11c-094132562316', '86049030', 'El Zanjón', '86', 'Capital'),
    ('75f4263e-292d-540a-9cf5-b12c149d42c9', '86049040', 'Los Cardozos', '86', 'Capital'),
    ('1a3a04b9-fcb7-53a8-a02a-384d3d470ab9', '86049050', 'Maco', '86', 'Capital'),
    ('038df206-dd71-5d88-8a12-69f1f78bb147', '86049060', 'Maquito', '86', 'Capital'),
    ('ee0e1607-db10-5e2a-a1fa-b6155c952101', '86049070', 'Morales', '86', 'Capital'),
    ('6e6593ba-c16b-5935-be8e-ceb37d33d821', '86049080', 'Puesto de San Antonio', '86', 'Capital'),
    ('331af0d9-3def-5ae8-89f8-e92c0e850528', '86049090', 'San Pedro', '86', 'Capital'),
    ('d49260a9-d332-5c5a-8c44-71a067ae9d60', '86049100', 'Santa María', '86', 'Capital'),
    ('834ed7e7-5893-54ec-9231-0294280601c5', '86049110', 'Santiago del Estero', '86', 'Capital'),
    ('70af5fbd-37ff-5b69-a4cc-ebc69326eca2', '86049120', 'Vuelta de la Barranca', '86', 'Capital'),
    ('c569db84-e2b8-5175-9606-aa55a8ae8d7c', '86049130', 'Yanda', '86', 'Capital'),
    ('429fd3e1-0f2e-5b22-88ad-3dfe4718405d', '86056010', 'El Caburé', '86', 'Copo'),
    ('16a6bb93-2028-539c-8bc3-ed5bbdfc3ab9', '86056030', 'Los Pirpintos', '86', 'Copo'),
    ('50dc59eb-f8a3-5de8-879b-4fd65d850978', '86056040', 'Los Tigres', '86', 'Copo'),
    ('9c6c2d25-a03b-5410-a1aa-5cea828d1a63', '86056050', 'Monte Quemado', '86', 'Copo'),
    ('0b082e04-c4b3-5609-a477-daba6ccbd3bb', '86056070', 'Pampa de los Guanacos', '86', 'Copo'),
    ('a774afc4-0a20-5f0d-a21f-eb7e1a162ed8', '86056080', 'San José del Boquerón', '86', 'Copo'),
    ('67f7c759-ea4f-5686-9bee-6be209dc86aa', '86056090', 'Urutaú', '86', 'Copo'),
    ('59f8f62e-c795-5700-a04a-fe0c0105e4ff', '86063010', 'Ancaján', '86', 'Choya'),
    ('336af033-600d-5ddb-b447-a57783f3b814', '86063020', 'Choya', '86', 'Choya'),
    ('e35bd3ff-ff6f-5c9b-8ee6-783e974e2662', '86063030', 'Estación La Punta', '86', 'Choya'),
    ('977ba7ba-6fb6-5cdc-8b6b-c1a88e555ede', '86063040', 'Frías', '86', 'Choya'),
    ('4bb72843-6c95-5cc0-bb3a-2656362f14ca', '86063050', 'Laprida', '86', 'Choya'),
    ('cee57a57-81e6-53db-821f-3fca549ac44c', '86063070', 'San Pedro', '86', 'Choya'),
    ('990a6f03-4236-5e7f-b29b-e9e05f24d7f3', '86063080', 'Tapso', '86', 'Choya'),
    ('1be64d3b-83d6-549f-a7fa-eec648fef64a', '86063090', 'Villa La Punta', '86', 'Choya'),
    ('d9d76491-0363-5185-ba93-85ae40c29355', '86070010', 'Bandera Bajada', '86', 'Figueroa'),
    ('3837dd42-2b74-5449-b495-64e6dbb0a8cb', '86070020', 'Caspi Corral', '86', 'Figueroa'),
    ('32a0f8aa-d6bb-552c-802d-5c16d75b9e59', '86070030', 'Colonia San Juan', '86', 'Figueroa'),
    ('afcb9898-f83b-58ea-b6b6-2646e2dfa3d3', '86070040', 'El Crucero', '86', 'Figueroa'),
    ('a7fc624a-c206-58e0-906f-d324d0b344f3', '86070060', 'La Cañada', '86', 'Figueroa'),
    ('f10cce5f-00d1-5abc-adfe-6de0355f04f6', '86070070', 'La Invernada', '86', 'Figueroa'),
    ('2543e407-e84b-5e4b-9f36-e3696b572e3d', '86070080', 'Minerva', '86', 'Figueroa'),
    ('cfbdf068-f192-5865-98be-29c8987b6029', '86070090', 'Vaca Huañuna', '86', 'Figueroa'),
    ('0560922f-33d9-594e-8df4-9c71f5f8176c', '86070100', 'Villa Figueroa', '86', 'Figueroa'),
    ('e2e4689a-77a9-5376-bbd2-b9411b5e7e3d', '86077010', 'Añatuya', '86', 'General Taboada'),
    ('f74cbca5-3dd2-5925-9aed-b40087b0f365', '86077020', 'Averías', '86', 'General Taboada'),
    ('5a2fb148-2571-548a-8cc4-90570286d1dc', '86077030', 'Estación Tacañitas', '86', 'General Taboada'),
    ('65678645-ec6f-5c85-b04e-ba526179f416', '86077040', 'La Nena', '86', 'General Taboada'),
    ('956cb5df-5fbf-5f96-ae67-55d58cf3b77b', '86077050', 'Los Juríes', '86', 'General Taboada'),
    ('58115f2e-7d7a-5ddf-899e-6e20ff16b75a', '86077060', 'Tomás Young', '86', 'General Taboada'),
    ('3a53bb46-b507-5bd4-8be7-a3f6da63b654', '86084010', 'Lavalle', '86', 'Guasayán'),
    ('21808ee9-2b0c-5792-be48-310692a6fdf9', '86084020', 'San Pedro', '86', 'Guasayán'),
    ('5845cb0f-8f80-5712-b3f4-c242629e3154', '86091005', 'El Arenal', '86', 'Jiménez'),
    ('d9ab8eee-ec48-5eaa-a32f-9b412ec50746', '86091010', 'El Bobadal', '86', 'Jiménez'),
    ('9388636d-d511-5995-9f72-969e199beff9', '86091020', 'El Charco', '86', 'Jiménez'),
    ('43a1d900-77aa-54c0-a7e6-fd4f181249e4', '86091030', 'El Rincón', '86', 'Jiménez'),
    ('c1d96588-5d25-56d0-9461-46dec3e0e2e0', '86091040', 'Gramilla', '86', 'Jiménez'),
    ('3d762005-c918-564a-9fbd-982deb6f8b52', '86091050', 'Isca Yacu', '86', 'Jiménez'),
    ('af793f85-3166-59c2-b971-62341cd2b55f', '86091060', 'Isca Yacu Semaul', '86', 'Jiménez'),
    ('fa3cdfd6-b990-51ab-a9ec-1f6ce1740fa4', '86091070', 'Pozo Hondo', '86', 'Jiménez'),
    ('a0d71a53-5a34-5749-b5a3-92c5a15e264f', '86091080', 'San Pedro', '86', 'Jiménez'),
    ('4767e681-744d-5d52-a73a-f38e2f15f155', '86098010', 'El Colorado', '86', 'Juan Felipe Ibarra'),
    ('738b977e-fcce-5331-a841-f0419d2a3c74', '86098020', 'El Cuadrado', '86', 'Juan Felipe Ibarra'),
    ('92d79eab-a607-5377-94af-2062979bc21a', '86098030', 'Matará', '86', 'Juan Felipe Ibarra'),
    ('9964b060-e5ad-5b0b-8287-4b16de6cd764', '86098040', 'Suncho Corral', '86', 'Juan Felipe Ibarra'),
    ('5a80cfd7-6925-5b5e-a986-0efff392b6a4', '86098050', 'Vilelas', '86', 'Juan Felipe Ibarra'),
    ('f829bb35-1879-5dae-8f93-fdcb2390ee68', '86098060', 'Yuchán', '86', 'Juan Felipe Ibarra'),
    ('4d89a8a0-3fa1-5f3d-bb7e-8a90a8bcd043', '86105010', 'Villa San Martín (Est. Loreto)', '86', 'Loreto'),
    ('7532b595-3722-57fb-8669-2053ba11f3a6', '86112010', 'Villa Unión', '86', 'Mitre'),
    ('ce155256-7012-5b0c-8790-07cbb01e31c0', '86119010', 'Aerolito', '86', 'Moreno'),
    ('dc789b96-1e64-5360-a22c-1681718dc875', '86119020', 'Alhuampa', '86', 'Moreno'),
    ('a5f09d87-fcda-5a7c-8eb4-c608d1fb8ba4', '86119030', 'Hasse', '86', 'Moreno'),
    ('a2aba635-bc4a-55fa-9ac1-79117f0996f6', '86119040', 'Hernán Mejía Miraval', '86', 'Moreno'),
    ('f4951c96-a837-5577-b0c0-8f6a8224faa8', '86119050', 'Las Tinajas', '86', 'Moreno'),
    ('bcfff2c1-98e7-536c-9c46-5da4a02bad47', '86119060', 'Libertad', '86', 'Moreno'),
    ('de9e6b85-54e7-5c39-8c54-c5be195f88bc', '86119070', 'Lilo Viejo', '86', 'Moreno'),
    ('af33f5f0-e572-5539-aaa8-a2881d8bc0ed', '86119080', 'Patay', '86', 'Moreno'),
    ('fde0d66f-3581-510d-95c0-96e2f4efa36e', '86119090', 'Pueblo Pablo Torelo', '86', 'Moreno'),
    ('38393f3a-d05c-50e0-97b4-67a7087c5808', '86119100', 'Quimili', '86', 'Moreno'),
    ('f9636cbf-0dc9-5aef-b343-bce67c71c20b', '86119110', 'Roversi', '86', 'Moreno'),
    ('14e17658-46b8-5e60-adf0-8a003e5f616c', '86119120', 'Tintina', '86', 'Moreno'),
    ('fb055660-e3f6-5c13-9cdd-5cfd5e78123c', '86119130', 'Weisburd', '86', 'Moreno'),
    ('388ab30b-4cee-5d96-b0b8-265634ab4d5e', '86126010', 'El 49', '86', 'Ojo de Agua'),
    ('80c922e5-7adf-5797-b2c9-5e0832ac1e3b', '86126020', 'Sol de Julio', '86', 'Ojo de Agua'),
    ('991eb4a8-0bac-521b-abed-8e42073201fe', '86126030', 'Villa Ojo de Agua', '86', 'Ojo de Agua'),
    ('880033d4-34cf-5386-bf0d-b0622cc5e138', '86133010', 'El Mojón', '86', 'Pellegrini'),
    ('d39c0ac9-4ae4-5310-8902-a5f9e6aefb4f', '86133020', 'Las Delicias', '86', 'Pellegrini'),
    ('c748e488-189e-503a-9411-8b346cc8607c', '86133030', 'Nueva Esperanza', '86', 'Pellegrini'),
    ('f48517ef-a1f3-55b8-b8fd-2a91737c752b', '86133040', 'Pozo Betbeder', '86', 'Pellegrini'),
    ('f335408f-1ceb-5826-8a04-96fdc4e38442', '86133050', 'Rapelli', '86', 'Pellegrini'),
    ('fdaae7d2-4b0c-56d1-9e41-8bd7e7252209', '86133060', 'Santo Domingo', '86', 'Pellegrini'),
    ('dd98d11f-d55f-5496-b394-f62e72c485f4', '86140010', 'Ramírez de Velazco', '86', 'Quebrachos'),
    ('1d509b6c-53a6-5c55-bd03-5b6c2d53eaa6', '86140020', 'Sumampa', '86', 'Quebrachos'),
    ('96a0930b-1929-517e-822f-1913b76109a4', '86140030', 'Sumampa Viejo', '86', 'Quebrachos'),
    ('843751c0-6d3c-5edb-8169-2244a26fa3cd', '86147020', 'Chauchillas', '86', 'Río Hondo'),
    ('1852eac6-02ed-59a1-8cb4-9e40b354f40b', '86147030', 'Colonia Tinco', '86', 'Río Hondo'),
    ('b837a5b6-495e-5519-b78d-ef63ed691844', '86147040', 'El Charco', '86', 'Río Hondo'),
    ('929dcb4b-2f72-588a-864d-b8bd5bbc0207', '86147050', 'Gramilla', '86', 'Río Hondo'),
    ('eacca7bb-1ff8-5f89-a9fc-08d90156be41', '86147060', 'La Nueva Donosa', '86', 'Río Hondo'),
    ('5f107af3-e5ff-5910-83f5-2ad10acce231', '86147070', 'Los Miranda', '86', 'Río Hondo'),
    ('356474c8-c147-500b-ac77-57e0016f0863', '86147080', 'Los Núñez', '86', 'Río Hondo'),
    ('34cd52bf-b62c-5e4d-95f1-cb2487d8a277', '86147090', 'Mansupa', '86', 'Río Hondo'),
    ('80b6342a-cf3a-5c59-9ae5-eb50df9db4cc', '86147100', 'Pozuelos', '86', 'Río Hondo'),
    ('dbff76b9-7ea1-5eff-8d03-544f8d048439', '86147110', 'Rodeo de Valdez', '86', 'Río Hondo'),
    ('57163b0f-5f29-5b76-862a-729a9431a9e9', '86147120', 'El Sauzal', '86', 'Río Hondo'),
    ('9cd50462-c935-51d6-96ef-b0b4f30b9b11', '86147130', 'Termas de Río Hondo', '86', 'Río Hondo'),
    ('048decfb-d9f8-56cf-860c-96bdaa2fefcb', '86147140', 'Villa Giménez', '86', 'Río Hondo'),
    ('a61685f7-1bcf-5dda-a4f5-a354531fc695', '86147150', 'Villa Río Hondo', '86', 'Río Hondo'),
    ('99f9145d-8233-5d9a-acde-94c4cb859259', '86147170', 'Vinará', '86', 'Río Hondo'),
    ('bcfb5d2c-9779-535b-94a4-813769ca3553', '86154010', 'Colonia Alpina', '86', 'Rivadavia'),
    ('7fa70686-97ab-5c76-b838-0e19fe0deb51', '86154020', 'Palo Negro', '86', 'Rivadavia'),
    ('6fbdfe2b-7ab5-59a1-8514-3ed92ae7d1b1', '86154030', 'Selva', '86', 'Rivadavia'),
    ('0bbd71a3-28ad-52db-a28e-51545d3d9f13', '86161010', 'Beltrán', '86', 'Robles'),
    ('3552163f-e8b5-523b-ae78-22a2f927d2a3', '86161020', 'Colonia El Simbolar', '86', 'Robles'),
    ('adb299c0-f297-5c22-85e9-387069713f84', '86161030', 'Fernández', '86', 'Robles'),
    ('537abc91-6dc1-5049-bebb-1a6342962b3e', '86161040', 'Ingeniero Forres', '86', 'Robles'),
    ('3401ae5e-924b-517a-83e9-5042a4949e6f', '86161050', 'Vilmer', '86', 'Robles'),
    ('96b5914c-63bc-5816-9718-e4f1838b7436', '86168010', 'Chilca Juliana', '86', 'Salavina'),
    ('8d68b82a-9898-53f2-a934-dd84f7fb3d4e', '86168020', 'Los Telares', '86', 'Salavina'),
    ('d81973ff-cf30-5473-81d4-5ca5aaf03209', '86168030', 'Villa Salavina', '86', 'Salavina'),
    ('2d4d4bda-53a1-53ee-8d9f-41028a84ee97', '86175010', 'Brea Pozo', '86', 'San Martín'),
    ('a50911df-20b3-5823-b9b5-6f8d15122826', '86175020', 'Estación Robles', '86', 'San Martín'),
    ('097bd85f-ecdc-58bc-bc85-3e0c2e806f78', '86175030', 'Estación Taboada', '86', 'San Martín'),
    ('5914679b-5e31-597b-8aae-098a00083008', '86175040', 'Villa Nueva', '86', 'San Martín'),
    ('1fb129f6-b7b9-5256-b08c-76fc63c289bb', '86182010', 'Garza', '86', 'Sarmiento'),
    ('ac53f4b2-dcaa-5861-b60c-ddf311601b6e', '86189010', 'Árraga', '86', 'Silípica'),
    ('1dd01b58-a7e5-5827-a9f5-56b4091aa175', '86189020', 'Nueva Francia', '86', 'Silípica'),
    ('950664f6-dde3-5a11-82f8-547e1394ea5b', '86189030', 'Simbol', '86', 'Silípica'),
    ('d129bb84-a288-53ec-b164-baa5d77975de', '86189040', 'Sumamao', '86', 'Silípica'),
    ('5db21800-f903-59fa-b2a9-7ccf36d6b035', '86189050', 'Villa Silípica', '86', 'Silípica'),
    ('e4879907-371b-5782-964e-0fd13486f31b', '90007010', 'Barrio San Jorge', '90', 'Burruyacú'),
    ('6b2e88d9-9558-5c77-94e8-b587a5768dc4', '90007020', 'El Chañar', '90', 'Burruyacú'),
    ('159ad1df-909d-51ad-8110-600955055d6d', '90007030', 'El Naranjo', '90', 'Burruyacú'),
    ('69c70e83-dce1-5230-b417-1d4d478daab4', '90007040', 'Garmendia', '90', 'Burruyacú'),
    ('adbe5da0-61e5-5bbd-91db-e03cd7b5bad6', '90007050', 'La Ramada', '90', 'Burruyacú'),
    ('0fe0f936-5465-5843-81ba-001032e7cbb1', '90007060', 'Macomitas', '90', 'Burruyacú'),
    ('030e8fe9-dca9-50a6-aab1-59836fd01de1', '90007070', 'Piedrabuena', '90', 'Burruyacú'),
    ('18a5a825-05cb-5c9d-b517-1bc10f3468ec', '90007090', 'Villa Benjamín Aráoz', '90', 'Burruyacú'),
    ('b171067f-7275-52fa-bf18-2c2ff92a26fc', '90007100', 'Villa Burruyacú', '90', 'Burruyacú'),
    ('a33b2ac3-5158-5d48-891b-3521040cd6bf', '90007110', 'Villa Padre Monti', '90', 'Burruyacú'),
    ('68a551ab-6b52-58eb-b1de-5d41eb20df8f', '90014010', 'Alderetes', '90', 'Cruz Alta'),
    ('732804fb-b9ce-56bc-a73d-2e1dec518cca', '9001401001', 'Alderetes', '90', 'Cruz Alta'),
    ('94b259f8-4dbd-54f9-b209-2c3d033347cd', '9001401002', 'El Corte', '90', 'Cruz Alta'),
    ('2c4a060c-6839-5380-a2f3-77f20186c49e', '9001401003', 'Los Gutiérrez', '90', 'Cruz Alta'),
    ('577a18f6-683e-548b-a9d3-3b383f371c40', '90014020', 'Banda del Río Salí', '90', 'Cruz Alta'),
    ('ca652181-7f21-5b35-8796-d879f75895cf', '9001402001', 'Banda del Río Salí', '90', 'Cruz Alta'),
    ('af6ceaa7-bafe-5785-bb64-146a4bfb22e7', '9001402002', 'Barrio Aeropuerto', '90', 'Cruz Alta'),
    ('91e2d362-43e4-5f0c-9984-77cb70bbe4d3', '9001402003', 'Lastenia', '90', 'Cruz Alta'),
    ('5590dfd2-aace-5905-8174-c83a105588c6', '90014040', 'Colombres', '90', 'Cruz Alta'),
    ('71bd86ad-ce0b-5d09-8f11-07d70c7112f1', '90014050', 'Colonia Mayo - Barrio La Milagrosa', '90', 'Cruz Alta'),
    ('68eb63f7-6b0f-59ab-9782-53a4fc81d4d0', '90014060', 'Delfín Gallo', '90', 'Cruz Alta'),
    ('2e805b8c-ed32-5b6e-ae6f-55dd4b37ee3f', '9001406001', 'El Paraíso', '90', 'Cruz Alta'),
    ('31aede36-8b89-5e96-9b38-dbc8b9da6a04', '9001406002', 'Ex Ingenio Esperanza', '90', 'Cruz Alta'),
    ('8fef3f4f-4895-5fd1-97dc-3e3b7f91e665', '9001406003', 'Ex Ingenio Luján', '90', 'Cruz Alta'),
    ('7c070ff1-8e12-5fff-8c01-e88cabb4a388', '90014070', 'El Bracho', '90', 'Cruz Alta'),
    ('8006923a-b0a3-5766-8c63-a6f287cb267e', '90014080', 'La Florida', '90', 'Cruz Alta'),
    ('c9870baa-756a-53fc-9af1-da2ec8c06327', '9001408001', 'Ingenio La Florida', '90', 'Cruz Alta'),
    ('8c277590-d5a8-5bd0-b79e-ec656a4d302c', '9001408002', 'La Florida', '90', 'Cruz Alta'),
    ('0b048584-aa0a-54f0-a766-c232b741e4f3', '90014090', 'Las Cejas', '90', 'Cruz Alta'),
    ('73395ad7-3a2d-55a4-9028-42b74f5f352c', '90014100', 'Los Ralos', '90', 'Cruz Alta'),
    ('f9f29da4-e841-5f9a-8116-4a7b97f402d8', '9001410001', 'Ex Ingenio Los Ralos', '90', 'Cruz Alta'),
    ('ea1b71ab-fb14-5c63-a3ce-0e4f535f8008', '9001410002', 'Villa Recaste', '90', 'Cruz Alta'),
    ('7b40a04e-0442-59c8-8a41-2f9cf65cc3f8', '9001410003', 'Villa Tercera', '90', 'Cruz Alta'),
    ('74ea3284-0db4-50af-bdf5-5660b6efd73e', '90014110', 'Pacará', '90', 'Cruz Alta'),
    ('e22100c3-fe64-5cfc-b916-a01ac71d88d5', '90014120', 'Ranchillos', '90', 'Cruz Alta'),
    ('0075f6d1-4d4e-52b2-a590-6c85317811bc', '90014130', 'San Andrés', '90', 'Cruz Alta'),
    ('15642ff0-ff0a-59c6-b827-e54f5fe8d59b', '90021010', 'Alpachiri', '90', 'Chicligasta'),
    ('ccc786ad-ffee-5357-9616-c363320a3441', '90021020', 'Alto Verde', '90', 'Chicligasta'),
    ('aba61f3e-8373-54e1-a03d-1f1bd683ab88', '90021030', 'Arcadia', '90', 'Chicligasta'),
    ('9b9b30f7-0519-5b20-b717-6b15e308231f', '90021050', 'Concepción', '90', 'Chicligasta'),
    ('b2540442-36cf-56f7-9255-5d33062d567c', '90021060', 'Iltico', '90', 'Chicligasta'),
    ('afb04a5a-2049-5c51-b25e-a9d98cc38f14', '90021070', 'La Trinidad', '90', 'Chicligasta'),
    ('ffa3caa8-d5a9-541b-ac04-9faf383aea0a', '90021080', 'Medina', '90', 'Chicligasta'),
    ('9c4310ef-9121-53ee-925c-8e02d5d364d7', '90028020', 'Campo de Herrera', '90', 'Famaillá'),
    ('7763e2cd-f7a4-562e-b851-046e306f47f5', '90028030', 'Famaillá', '90', 'Famaillá'),
    ('fb043a04-4b6c-5e6d-b571-1a8b85295cda', '9002803001', 'Ex Ingenio Nueva Baviera', '90', 'Famaillá'),
    ('d69b5ce0-9379-511f-88a4-d969f1040ec5', '9002803002', 'Famaillá', '90', 'Famaillá'),
    ('0ffc3856-4295-5cd8-ad8c-1502c0cd72cb', '90028040', 'Ingenio Fronterita', '90', 'Famaillá'),
    ('a9bad4b1-e2f3-5e1d-97f2-de914a81a8df', '90035010', 'Graneros', '90', 'Graneros'),
    ('a3d9d950-c6ef-55c2-96e9-c811f679c07d', '90035020', 'Lamadrid', '90', 'Graneros'),
    ('c4d577e7-26b1-5011-b1d0-1bb27b82dd7f', '90035030', 'Taco Ralo', '90', 'Graneros'),
    ('b0ec5c1e-c40c-5ecb-b34e-633a2cc61c6f', '90042010', 'Juan Bautista Alberdi', '90', 'Juan Bautista Alberdi'),
    ('2b1dc98b-7481-56b9-94e5-6bf5d08cbca4', '90042020', 'Villa Belgrano', '90', 'Juan Bautista Alberdi'),
    ('48fc8f07-9215-5540-b35f-4a367ffc58f9', '90049010', 'La Cocha', '90', 'La Cocha'),
    ('1a96a407-d0d3-5039-8a7c-5c8b3d3360ad', '90049020', 'San José de La Cocha', '90', 'La Cocha'),
    ('be93d809-bcdf-59a1-a033-8c4df2eaf3c2', '90056010', 'Bella Vista', '90', 'Leales'),
    ('0dd342cc-81e6-5c21-87ed-ae6a72198214', '90056020', 'Estación Aráoz', '90', 'Leales'),
    ('47ae84ca-e28c-501a-a472-0147c389163c', '90056030', 'Los Puestos', '90', 'Leales'),
    ('188484cd-3193-5149-ba66-5aa8650c6e78', '90056040', 'Manuel García Fernández', '90', 'Leales'),
    ('ecd071fb-90c7-5b2f-8875-dd7a1585ee00', '90056060', 'Río Colorado', '90', 'Leales'),
    ('36bdd2ac-4018-5203-b41a-9191f4c798ce', '90056070', 'Santa Rosa de Leales', '90', 'Leales'),
    ('37a17826-2785-5118-9494-45ac8bcbd80e', '90056080', 'Villa Fiad - Ingenio Leales', '90', 'Leales'),
    ('9ce66e92-c14b-5bd1-95c0-b72ac5170ddb', '90056090', 'Villa de Leales', '90', 'Leales'),
    ('c2a7777a-af59-5208-8b18-4dff3ad7b2a7', '90063010', 'Barrio San Felipe', '90', 'Lules'),
    ('a081802e-c59d-583d-866d-1c15bd24fa9d', '90063020', 'El Manantial', '90', 'Lules'),
    ('16a724fc-9a04-5b04-ab96-bccc22a63a29', '9006302001', 'Barrio Araujo', '90', 'Lules'),
    ('00977521-2f9e-5fca-a365-02bcdc89f91f', '9006302002', 'El Manantial', '90', 'Lules'),
    ('84b6003e-efc4-5f15-83c5-e8b57ee0c767', '90063030', 'Ingenio San Pablo', '90', 'Lules'),
    ('01d1654a-df47-59f9-bc35-fc4e99ed8c30', '90063040', 'La Reducción', '90', 'Lules'),
    ('c31ac815-40b4-56cf-a5ba-77605a411a58', '90063050', 'Lules', '90', 'Lules'),
    ('ad7a3304-80ae-5c68-b016-4f53efc365ab', '90070010', 'Acheral', '90', 'Monteros'),
    ('cc2ae18d-a48e-5a31-b503-135bfe6c2d01', '90070020', 'Capitán Cáceres', '90', 'Monteros'),
    ('7fd33626-6992-546b-9cd9-e7d5ae33e2a5', '90070030', 'Monteros', '90', 'Monteros'),
    ('587a7e02-3fac-5d62-99ab-42d1d349429e', '90070040', 'Pueblo Independencia', '90', 'Monteros'),
    ('92f65d66-294e-5007-965f-5395b16137ee', '90070050', 'Río Seco', '90', 'Monteros'),
    ('ab4fd3bb-8ce4-53de-9e55-8f0cb878634a', '90070060', 'Santa Lucía', '90', 'Monteros')
ON CONFLICT (indec_id) DO NOTHING;

INSERT INTO cities (id, indec_id, name, province_id, department_name) VALUES
    ('75eb777a-f92e-5d83-a54c-3e7adedeb1fb', '90070070', 'Sargento Moya', '90', 'Monteros'),
    ('8220206f-3714-5b9d-81a7-155ec02c0c29', '90070080', 'Soldado Maldonado', '90', 'Monteros'),
    ('dd970be8-4f35-54b7-bd06-b4edb319b457', '90070090', 'Teniente Berdina', '90', 'Monteros'),
    ('db267da1-0df1-5922-8b61-a63404c1835f', '90070100', 'Villa Quinteros', '90', 'Monteros'),
    ('35f56c91-7201-57b5-8fe5-1601f4cbcebc', '90077010', 'Aguilares', '90', 'Río Chico'),
    ('029df553-9a71-5f14-bfc6-1625865d66ea', '9007701001', 'Aguilares', '90', 'Río Chico'),
    ('8e8fbec4-c87e-58e4-bae2-1a570a18e178', '9007701002', 'Ingenio Santa Bárbara', '90', 'Río Chico'),
    ('790d0fc6-90d0-5fff-82af-2c91087c5aff', '90077020', 'Los Sarmientos', '90', 'Río Chico'),
    ('78b1d850-8c6a-5de2-951e-fa6c8b294dfe', '90077030', 'Río Chico', '90', 'Río Chico'),
    ('d592451a-1a69-5043-a3b4-54bbab0567ff', '90077040', 'Santa Ana', '90', 'Río Chico'),
    ('50af2fad-99f3-58d3-b11f-eef885abc3ae', '90077050', 'Villa Clodomiro Hileret', '90', 'Río Chico'),
    ('8cc252f2-cfa1-5e8e-b85e-f703639eb1f6', '90084010', 'San Miguel de Tucumán', '90', 'Capital'),
    ('acdb9cd0-9888-59ad-b8f3-c4fd8c61c4ab', '90091010', 'Atahona', '90', 'Simoca'),
    ('daf7467c-47b5-5665-9dd0-7e387cd21ee2', '90091020', 'Monteagudo', '90', 'Simoca'),
    ('cc14b8cc-f278-56ca-af94-37b2827808df', '90091030', 'Nueva Trinidad', '90', 'Simoca'),
    ('0487ecfb-bbba-5bb7-b58e-be1188918ede', '90091040', 'Santa Cruz', '90', 'Simoca'),
    ('335eb441-b087-5eb1-b456-4b92ce734ba9', '90091050', 'Simoca', '90', 'Simoca'),
    ('315981b6-d7fc-561e-b627-f544bd67b090', '90091060', 'Villa Chicligasta', '90', 'Simoca'),
    ('572c90f0-e016-50d8-b59d-c37c1b48c5e0', '90098010', 'Amaicha del Valle', '90', 'Tafí del Valle'),
    ('7f6f4471-de7c-50aa-8da9-d218d6275613', '90098020', 'Colalao del Valle', '90', 'Tafí del Valle'),
    ('acea58af-bb98-57e3-b90b-ec0f6b4c74d2', '90098030', 'El Mollar', '90', 'Tafí del Valle'),
    ('34e0408a-2f52-51a9-bf74-29082b1ff690', '90098040', 'Tafí del Valle', '90', 'Tafí del Valle'),
    ('1716ad57-0b42-5cfa-8eac-610a6d6e520a', '90105030', 'Barrio Mutual San Martín', '90', 'Tafí Viejo'),
    ('fb5c3534-30ba-527c-a444-444eb56bc767', '90105070', 'El Cadillal', '90', 'Tafí Viejo'),
    ('eb39ea1e-cf99-5da2-a9cd-2c9ee3dde7f5', '90105080', 'Tafí Viejo', '90', 'Tafí Viejo'),
    ('efa13112-d96f-5a90-9b31-06e731b90f9c', '90105100', 'Villa Mariano Moreno - El Colmenar', '90', 'Tafí Viejo'),
    ('af9d6b13-2126-5e1e-9041-e40bb5ffe96d', '90112010', 'Choromoro', '90', 'Trancas'),
    ('04e263f8-413d-5486-b0e3-af5926be8bea', '90112020', 'San Pedro de Colalao', '90', 'Trancas'),
    ('129e643a-fa7f-546d-a3ea-bfbb9d9d10c7', '90112030', 'Villa de Trancas', '90', 'Trancas'),
    ('7cc4d405-2622-5237-aacd-0dadb0ff2d73', '90119020', 'Villa Carmela', '90', 'Yerba Buena'),
    ('b389422d-dbdb-5111-a017-eca66d38f7cc', '90119030', 'Yerba Buena - Marcos Paz', '90', 'Yerba Buena'),
    ('f4aca974-9b32-54c1-9aa0-047a369759f3', '9011903001', 'Ex Ingenio San José', '90', 'Yerba Buena'),
    ('d6ad6977-900d-5b6c-9dc1-2ade66e9222b', '9011903002', 'Yerba Buena - Marcos Paz', '90', 'Yerba Buena'),
    ('72fb4af8-ef83-5b7b-b5bd-105cfd0068e7', '94008010', 'Río Grande', '94', 'Río Grande'),
    ('c08aab07-2e5e-5b7e-8720-b16d1de2432e', '94015010', 'Laguna Escondida', '94', 'Ushuaia'),
    ('505b43bc-41db-5228-af2e-2b7abef205e0', '94015020', 'Ushuaia', '94', 'Ushuaia'),
    ('eedba659-bd2d-5f83-912c-07a7ec66162d', '94021010', 'Puerto Argentino', '94', 'Islas del Atlántico Sur')
ON CONFLICT (indec_id) DO NOTHING;
-- <<< END GENERATED GEOREF DATA <<<

-- ===========================================================================
-- 5. Map the organization cities onto Georef, repoint customers, drop the old table
-- ===========================================================================

DO $mig$
DECLARE
    r          record;
    v_key      text;
    v_stage    integer;
    v_primary  integer;
    v_total    integer;
    v_first    uuid;
    v_target   uuid;
    v_note     text;
    v_mapped   integer := 0;
    v_unmapped integer := 0;
BEGIN
    IF to_regclass('public.org_cities_retired') IS NULL THEN
        RETURN;  -- already retired by an earlier run
    END IF;

    -- Retiring the organization cities cannot be undone, so it never runs against
    -- an empty global table (a truncated file or a partial commit of this one).
    IF NOT EXISTS (SELECT 1 FROM cities WHERE indec_id IS NOT NULL) THEN
        RAISE EXCEPTION 'core-geography: the Georef localities are not loaded; refusing to retire the organization cities';
    END IF;

    -- The foreign key to the old table must go before customers are repointed.
    ALTER TABLE customers DROP CONSTRAINT IF EXISTS customers_city_org_fk;

    CREATE TEMP TABLE _city_map (old_id uuid PRIMARY KEY, global_id uuid) ON COMMIT DROP;

    FOR r IN SELECT * FROM org_cities_retired ORDER BY organization_id, name, id LOOP
        v_key := translate(lower(btrim(r.name)), 'áéíóúüñàèìòùâêîôûäëïöç', 'aeiouunaeiouaeiouaeioc');
        v_target := NULL;
        v_note := NULL;

        SELECT id INTO v_target FROM cities
        WHERE indec_id = CASE v_key
            WHEN 'capital federal'   THEN '02014010'
            WHEN 'capitan sarmiento' THEN '06140010'
            WHEN 'rio tala'          THEN '06770040'
        END;
        IF v_target IS NOT NULL THEN
            v_note := 'owner-decided';
        ELSE
            FOR v_stage IN 1..3 LOOP
                SELECT count(*) FILTER (WHERE length(indec_id) = 8), count(*),
                       (array_agg(id ORDER BY (length(indec_id) = 8) DESC, indec_id))[1]
                INTO v_primary, v_total, v_first
                FROM cities
                WHERE indec_id IS NOT NULL
                  AND CASE v_stage
                        WHEN 1 THEN province_id = '06' AND search_key = v_key
                        WHEN 2 THEN province_id = '06'
                                    AND search_key LIKE replace(replace(replace(v_key, '\', '\\'), '%', '\%'), '_', '\_') || ' %'
                        ELSE search_key = v_key
                      END;
                CONTINUE WHEN v_total = 0;
                -- The first stage that finds anything decides.
                IF v_primary = 1 OR (v_primary = 0 AND v_total = 1) THEN
                    v_target := v_first;
                    v_note := CASE v_stage WHEN 1 THEN 'exact name in Buenos Aires province'
                                           WHEN 2 THEN 'name prefix in Buenos Aires province'
                                           ELSE 'exact name elsewhere in the country' END;
                ELSE
                    v_note := 'AMBIGUOUS (' || v_total || ' candidates)';
                END IF;
                EXIT;
            END LOOP;
        END IF;

        INSERT INTO _city_map (old_id, global_id) VALUES (r.id, v_target);
        IF v_target IS NULL THEN
            v_unmapped := v_unmapped + 1;
            RAISE NOTICE 'core-geography: UNMAPPED city "%" (organization %): %', r.name, r.organization_id,
                COALESCE(v_note, 'no Georef locality with that name');
        ELSE
            v_mapped := v_mapped + 1;
            RAISE NOTICE 'core-geography: mapped city "%" (organization %) -> % [%]', r.name, r.organization_id,
                (SELECT indec_id FROM cities WHERE id = v_target), v_note;
        END IF;
    END LOOP;

    -- Customers follow their city. An unmapped city leaves the reference empty,
    -- so its name is kept in the free-text locality (unless one is already set).
    UPDATE customers cu SET locality = oc.name
    FROM _city_map m JOIN org_cities_retired oc ON oc.id = m.old_id
    WHERE cu.city_id = m.old_id AND m.global_id IS NULL AND NULLIF(btrim(cu.locality), '') IS NULL;
    UPDATE customers cu SET city_id = m.global_id FROM _city_map m WHERE cu.city_id = m.old_id;

    -- The audit dates of the original rows survive on the matching global rows
    -- (the earliest creation and the latest update when several organizations
    -- used the same locality).
    UPDATE cities g
    SET created_at_utc = s.created_at, updated_at_utc = s.updated_at
    FROM (
        SELECT m.global_id, min(oc.created_at_utc) AS created_at, max(oc.updated_at_utc) AS updated_at
        FROM _city_map m JOIN org_cities_retired oc ON oc.id = m.old_id
        WHERE m.global_id IS NOT NULL
        GROUP BY m.global_id
    ) s
    WHERE g.id = s.global_id;

    RAISE NOTICE 'core-geography: % organization cities mapped, % unmapped', v_mapped, v_unmapped;
    DROP TABLE org_cities_retired;
END $mig$;

-- ===========================================================================
-- 6. customers.city_id -> global cities
-- ===========================================================================

DO $mig$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_city_fk') THEN
        ALTER TABLE customers
            ADD CONSTRAINT customers_city_fk
            FOREIGN KEY (city_id) REFERENCES cities (id) ON DELETE RESTRICT;
    END IF;
END $mig$;

COMMIT;

-- customer-contacts: 0029_customer_contacts.sql, appended verbatim per the hand-kept
-- mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- customer-contacts: a customer (typically a company) can have SEVERAL contact
-- people, so the single free-text `customers.contact_name` of 0027 becomes the
-- organization-scoped sub-table `customer_contacts` (first name required, last
-- name, phone, email, role, one optional primary contact, display order).
-- Existing `contact_name` values are moved into it as the primary contact and
-- the column is dropped.
--
-- APPLIED AFTER: 0028_core_geography.sql. Touches `customers` (0008, 0027).
--
-- RLS: same shape as `customers` (0008): the policy compares only
-- `organization_id` (fail-closed when no organization is scoped). Unlike the
-- catalogs, DELETE is granted: saving a customer REPLACES its contact set
-- (contacts missing from the request are removed in the same transaction).
-- The composite foreign key keeps a contact inside its customer's organization
-- (RLS filters reads, not foreign-key checks) and cascades when a customer row
-- is ever removed. At most one primary per customer: a partial unique index.
--
-- Migration of `contact_name`: first_name = btrim(contact_name), is_primary =
-- true, no last name (the free text had no structure; the owner edits it in
-- the app). Rows with a blank value get no contact. The ids are deterministic
-- (md5 of the customer id) so a re-run before the column drop cannot duplicate.
--
-- The whole file runs in ONE transaction and can be re-run safely (once the
-- column is dropped, the move is skipped).
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   ALTER TABLE customers ADD COLUMN contact_name text;
--   UPDATE customers c SET contact_name = (SELECT btrim(cc.first_name || ' ' || coalesce(cc.last_name, ''))
--     FROM customer_contacts cc WHERE cc.customer_id = c.id ORDER BY cc.is_primary DESC, cc.sort_order LIMIT 1);
--   DROP TABLE customer_contacts;
--   ALTER TABLE customers DROP CONSTRAINT customers_org_scoped_uk;

BEGIN;

-- ===========================================================================
-- 1. Target of the composite foreign key
-- ===========================================================================

DO $mig$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'customers'::regclass AND conname = 'customers_org_scoped_uk'
    ) THEN
        ALTER TABLE customers ADD CONSTRAINT customers_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $mig$;

-- ===========================================================================
-- 2. customer_contacts
-- ===========================================================================

CREATE TABLE IF NOT EXISTS customer_contacts (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    customer_id     uuid NOT NULL,
    first_name      text NOT NULL CHECK (btrim(first_name) <> ''),
    last_name       text,
    phone           text,
    email           text,
    role            text,
    is_primary      boolean NOT NULL DEFAULT false,
    sort_order      integer NOT NULL DEFAULT 0,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_contacts_customer_fk
        FOREIGN KEY (organization_id, customer_id)
        REFERENCES customers (organization_id, id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS customer_contacts_customer_idx
    ON customer_contacts (organization_id, customer_id, sort_order);
CREATE UNIQUE INDEX IF NOT EXISTS customer_contacts_one_primary_uk
    ON customer_contacts (customer_id) WHERE is_primary;

ALTER TABLE customer_contacts ENABLE ROW LEVEL SECURITY;
ALTER TABLE customer_contacts FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON customer_contacts FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE, DELETE ON customer_contacts TO app_runtime;

DROP POLICY IF EXISTS customer_contacts_tenant_isolation ON customer_contacts;
CREATE POLICY customer_contacts_tenant_isolation ON customer_contacts
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 3. Move customers.contact_name into a primary contact, then drop the column
-- ===========================================================================

DO $mig$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'customers' AND column_name = 'contact_name'
    ) THEN
        INSERT INTO customer_contacts (id, organization_id, customer_id, first_name, is_primary)
        SELECT md5('customer-contact:' || c.id::text)::uuid, c.organization_id, c.id, btrim(c.contact_name), true
        FROM customers c
        WHERE btrim(coalesce(c.contact_name, '')) <> ''
        ON CONFLICT DO NOTHING;

        ALTER TABLE customers DROP COLUMN contact_name;
    END IF;
END $mig$;

COMMIT;

-- suppliers: 0030_suppliers.sql, appended verbatim per the hand-kept
-- mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- suppliers: the supplier registry (modelled on `customers`, 0008/0027/0029).
-- Adds the ORGANIZATION-owned catalog `supplier_categories` ("rubros": meat,
-- technology services, cleaning, ...; same shape as `business_types`, 0027),
-- `suppliers` (commercial, tax and contact data, payment terms in days, bank
-- details) and the `supplier_contacts` sub-table (several contact people per
-- supplier, at most one primary; same shape as `customer_contacts`, 0029).
--
-- APPLIED AFTER: 0029_customer_contacts.sql. References the global `cities`
-- (0028) and `organizations` (0003). The current-account ledger that hangs off
-- `suppliers` is 0031_current_account_movements.sql.
--
-- RLS: the same shape as `customers` (0008): every policy compares only
-- `organization_id` (fail-closed when no organization is scoped). The composite
-- foreign keys keep a supplier inside its organization's category and a contact
-- inside its supplier's organization (RLS filters reads, not foreign-key
-- checks). `supplier_categories` has no DELETE grant: an entry that stops being
-- used is disabled (`is_active = false`) so suppliers keep their reference.
-- `suppliers` has no DELETE grant either (a supplier is disabled, never
-- removed, because its current account must stay readable); contacts are a
-- replace-set, so DELETE is granted on them.
--
-- `city_id` references the GLOBAL geography (no organization): a plain foreign
-- key. `payment_terms_days` is the default term used to date the due day of
-- an invoice; `bank_cbu` is a 22 digit CBU/CVU; `bank_alias` is a 6-20 char
-- alias of letters, digits, dots and dashes. Tax ids are stored digits only
-- (the API normalizes them); the CHECK keeps "type <-> value" consistent.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   DROP TABLE supplier_contacts; DROP TABLE suppliers; DROP TABLE supplier_categories;

BEGIN;

-- ===========================================================================
-- 1. supplier_categories
-- ===========================================================================

CREATE TABLE IF NOT EXISTS supplier_categories (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    key             text NOT NULL CHECK (btrim(key) <> ''),
    sort_order      integer NOT NULL DEFAULT 0,
    is_active       boolean NOT NULL DEFAULT true,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    -- Target of the composite foreign key from `suppliers` (same shape as 0018/0027).
    CONSTRAINT supplier_categories_org_scoped_uk UNIQUE (organization_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS supplier_categories_org_name_uk
    ON supplier_categories (organization_id, lower(btrim(name)));
CREATE UNIQUE INDEX IF NOT EXISTS supplier_categories_org_key_uk
    ON supplier_categories (organization_id, key);

ALTER TABLE supplier_categories ENABLE ROW LEVEL SECURITY;
ALTER TABLE supplier_categories FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON supplier_categories FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON supplier_categories TO app_runtime;

DROP POLICY IF EXISTS supplier_categories_tenant_isolation ON supplier_categories;
CREATE POLICY supplier_categories_tenant_isolation ON supplier_categories
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 2. suppliers
-- ===========================================================================

CREATE TABLE IF NOT EXISTS suppliers (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    display_name       text NOT NULL CHECK (btrim(display_name) <> ''),
    legal_name         text NULL,
    tax_id_type        text NOT NULL DEFAULT 'None'
                            CHECK (tax_id_type IN ('None','Cuit','Cuil','Dni')),
    tax_id             text NULL,
    tax_condition      text NOT NULL DEFAULT 'NoAplica'
                            CHECK (tax_condition IN ('ConsumidorFinal','ResponsableInscripto',
                                                     'Monotributo','Exento','NoAplica')),
    phone              text NULL,
    email              text NULL,
    address_street     text NULL,
    address_number     text NULL,
    neighborhood       text NULL,
    postal_code        text NULL,
    city_id            uuid NULL,
    category_id        uuid NULL,
    payment_terms_days integer NULL CHECK (payment_terms_days >= 0),
    bank_cbu           text NULL CHECK (bank_cbu ~ '^[0-9]{22}$'),
    bank_alias         text NULL CHECK (bank_alias ~ '^[A-Za-z0-9.-]{6,20}$'),
    notes              text NULL,
    is_enabled         boolean NOT NULL DEFAULT true,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid NOT NULL,
    updated_at_utc     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT suppliers_tax_id_requires_type
        CHECK ((tax_id_type = 'None' AND tax_id IS NULL) OR
               (tax_id_type <> 'None' AND tax_id IS NOT NULL)),
    -- Target of the composite foreign keys from `supplier_contacts` and the ledger (0031).
    CONSTRAINT suppliers_org_scoped_uk UNIQUE (organization_id, id),
    CONSTRAINT suppliers_city_fk FOREIGN KEY (city_id) REFERENCES cities (id),
    CONSTRAINT suppliers_category_org_fk
        FOREIGN KEY (organization_id, category_id)
        REFERENCES supplier_categories (organization_id, id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS suppliers_org_idx          ON suppliers (organization_id);
CREATE INDEX IF NOT EXISTS suppliers_org_updated      ON suppliers (organization_id, updated_at_utc);
CREATE INDEX IF NOT EXISTS suppliers_org_city_idx     ON suppliers (organization_id, city_id);
CREATE INDEX IF NOT EXISTS suppliers_org_category_idx ON suppliers (organization_id, category_id);

ALTER TABLE suppliers ENABLE ROW LEVEL SECURITY;
ALTER TABLE suppliers FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON suppliers FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON suppliers TO app_runtime;

DROP POLICY IF EXISTS suppliers_tenant_isolation ON suppliers;
CREATE POLICY suppliers_tenant_isolation ON suppliers
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ===========================================================================
-- 3. supplier_contacts
-- ===========================================================================

CREATE TABLE IF NOT EXISTS supplier_contacts (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    supplier_id     uuid NOT NULL,
    first_name      text NOT NULL CHECK (btrim(first_name) <> ''),
    last_name       text,
    phone           text,
    email           text,
    role            text,
    is_primary      boolean NOT NULL DEFAULT false,
    sort_order      integer NOT NULL DEFAULT 0,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_contacts_supplier_fk
        FOREIGN KEY (organization_id, supplier_id)
        REFERENCES suppliers (organization_id, id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS supplier_contacts_supplier_idx
    ON supplier_contacts (organization_id, supplier_id, sort_order);
CREATE UNIQUE INDEX IF NOT EXISTS supplier_contacts_one_primary_uk
    ON supplier_contacts (supplier_id) WHERE is_primary;

ALTER TABLE supplier_contacts ENABLE ROW LEVEL SECURITY;
ALTER TABLE supplier_contacts FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON supplier_contacts FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE, DELETE ON supplier_contacts TO app_runtime;

DROP POLICY IF EXISTS supplier_contacts_tenant_isolation ON supplier_contacts;
CREATE POLICY supplier_contacts_tenant_isolation ON supplier_contacts
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;

-- current-account-movements: 0031_current_account_movements.sql, appended verbatim per the hand-kept
-- mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- current-account-movements: the generic, APPEND-ONLY current-account ledger
-- (PRD 9.13). It is designed for several kinds of party (suppliers now;
-- customers and employees later) but wired to `suppliers` only in this
-- migration. The balance is never stored: it is always derived from the
-- movements.
--
-- APPLIED AFTER: 0030_suppliers.sql.
--
-- SIGN CONVENTION. `direction` says which side of the account a movement
-- lands on; `amount` is always positive. For a SUPPLIER account the balance
-- is what the business OWES the supplier:
--     balance = sum(amount WHERE direction = 'Credit') - sum(amount WHERE direction = 'Debit')
--   Invoice, DebitNote, OpeningBalance (a positive debt)  -> Credit (owes more)
--   Payment, CreditNote                                   -> Debit  (owes less)
--   Adjustment                                            -> either, chosen explicitly
--   Reversal -> the OPPOSITE direction of the movement it reverses, same amount
-- The application owns the kind -> direction mapping; the database keeps the
-- closed vocabularies and the structural invariants below.
--
-- PARTY LINK. `party_kind` + `party_id` identify the account owner generically.
-- Referential integrity is a nullable, typed foreign key per party kind
-- (`supplier_id` today; a future `customer_id`/`employee_id` is added the same
-- way) plus a CHECK that the typed column agrees with `party_id` for its kind.
-- This keeps real composite foreign keys (same-organization guarantee) without a
-- trigger or a polymorphic, unchecked uuid.
--
-- APPEND-ONLY. `app_runtime` gets SELECT and INSERT only: a movement can never
-- be updated or deleted by the application. A mistake is corrected with a
-- `Reversal` movement that points at the original (`reverses_movement_id`).
-- Database-enforced reversal rules: a reversal names a movement and only a
-- reversal does (`..._reversal_link`); a movement is reversed at most once
-- (partial unique index); a reversal can not itself be reversed (the composite
-- foreign key also matches the target's generated `is_reversible` flag, which is
-- false for a Reversal). That the reversal belongs to the same supplier and
-- mirrors amount/direction is enforced by the API.
--
-- RLS: same shape as `customers` (0008): the policy compares only
-- `organization_id`. Composite foreign keys keep a movement inside its
-- organization's supplier and reversal target.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   DROP TABLE current_account_movements;

BEGIN;

CREATE TABLE IF NOT EXISTS current_account_movements (
    id                    uuid PRIMARY KEY,
    organization_id       uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    party_kind            text NOT NULL CHECK (party_kind IN ('Supplier')),
    party_id              uuid NOT NULL,
    supplier_id           uuid NULL,
    kind                  text NOT NULL CHECK (kind IN (
        'OpeningBalance', 'Invoice', 'DebitNote', 'CreditNote', 'Payment', 'Adjustment', 'Reversal')),
    direction             text NOT NULL CHECK (direction IN ('Debit', 'Credit')),
    amount                numeric(18,2) NOT NULL CHECK (amount > 0),
    occurred_on           date NOT NULL,
    due_on                date NULL,
    document_reference    text NULL,
    concept               text NOT NULL CHECK (btrim(concept) <> ''),
    reverses_movement_id  uuid NULL,
    created_at_utc        timestamptz NOT NULL DEFAULT now(),
    created_by_user_id    uuid NOT NULL,
    -- Generated helpers of the reversal rules (see the header).
    is_reversible         boolean GENERATED ALWAYS AS (kind <> 'Reversal') STORED,
    reverses_reversible   boolean GENERATED ALWAYS AS (CASE WHEN reverses_movement_id IS NULL THEN NULL ELSE true END) STORED,
    CONSTRAINT current_account_movements_due_not_before_occurred
        CHECK (due_on IS NULL OR due_on >= occurred_on),
    CONSTRAINT current_account_movements_party_supplier
        CHECK (party_kind <> 'Supplier' OR supplier_id = party_id),
    CONSTRAINT current_account_movements_reversal_link
        CHECK ((kind = 'Reversal') = (reverses_movement_id IS NOT NULL)),
    -- Targets of the composite foreign keys (own and the reversal self reference).
    CONSTRAINT current_account_movements_org_scoped_uk UNIQUE (organization_id, id),
    CONSTRAINT current_account_movements_reversal_target_uk UNIQUE (organization_id, id, is_reversible),
    CONSTRAINT current_account_movements_supplier_fk
        FOREIGN KEY (organization_id, supplier_id)
        REFERENCES suppliers (organization_id, id),
    CONSTRAINT current_account_movements_reverses_fk
        FOREIGN KEY (organization_id, reverses_movement_id, reverses_reversible)
        REFERENCES current_account_movements (organization_id, id, is_reversible)
);

-- At most one reversal per movement.
CREATE UNIQUE INDEX IF NOT EXISTS current_account_movements_one_reversal_uk
    ON current_account_movements (reverses_movement_id) WHERE reverses_movement_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS current_account_movements_supplier_idx
    ON current_account_movements (organization_id, supplier_id, occurred_on, created_at_utc);
CREATE INDEX IF NOT EXISTS current_account_movements_org_created
    ON current_account_movements (organization_id, created_at_utc);

ALTER TABLE current_account_movements ENABLE ROW LEVEL SECURITY;
ALTER TABLE current_account_movements FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON current_account_movements FROM PUBLIC;
GRANT SELECT, INSERT ON current_account_movements TO app_runtime;

DROP POLICY IF EXISTS current_account_movements_tenant_isolation ON current_account_movements;
CREATE POLICY current_account_movements_tenant_isolation ON current_account_movements
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;

-- purchases-receptions-and-stock: 0032_purchase_receptions.sql, appended verbatim per the hand-kept
-- mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- purchases-receptions-and-stock (T1): goods receptions (PRD 9.6 / 9.8). A reception records the goods a supplier
-- delivered to ONE branch. It starts as a DRAFT (editable, no number), is CONFIRMED once (the application then, in the
-- same transaction, assigns its human number, moves the stock, posts the Invoice to the supplier current account and
-- records the cost history) and can later be VOIDED (compensating movements, never a delete).
--
-- APPLIED AFTER: 0030_suppliers.sql, 0031_current_account_movements.sql (and 0016/0017 for the branch-owned catalog).
--
-- BRANCH OWNED. Like the catalog (0016) and pricing (0017), a reception and its lines carry `branch_id` and the RLS
-- policies compare BOTH `app.current_org_id` and `app.current_branch_id` (USING and WITH CHECK): with no branch
-- selected nothing is visible and nothing can be written. Composite foreign keys keep the supplier inside the
-- organization and the presentation inside the branch.
--
-- NUMBER. `R{branch code}-W-{sequence}` (docs/document-numbering.md). The three parts (`branch_code`, `sequence`,
-- `number`) are present together exactly when the reception is not a draft; the sequence counts per branch and is
-- unique (`purchase_receptions_number_uk`). The application takes it under a per-branch advisory lock.
--
-- DUPLICATE GUARD. The same supplier document cannot be confirmed twice: unique (organization, supplier, document
-- type, normalized reference) among CONFIRMED receptions with a reference. A voided reception frees its document.
--
-- LINES. Replaced as a set while the reception is a draft (the application deletes and re-inserts); the trigger
-- `purchase_reception_lines_require_draft` refuses any change once it is Confirmed or Voided.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   DROP TABLE purchase_reception_lines; DROP TABLE purchase_receptions;
--   DROP FUNCTION purchase_reception_lines_require_draft();

BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS purchase_receptions (
    id                          uuid PRIMARY KEY,
    organization_id             uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    branch_id                   uuid NOT NULL,
    supplier_id                 uuid NOT NULL,
    status                      text NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft', 'Confirmed', 'Voided')),
    branch_code                 smallint NULL,
    sequence                    integer NULL,
    number                      text NULL,
    document_type               text NOT NULL CHECK (document_type IN ('Invoice', 'DeliveryNote', 'Other')),
    document_reference          text NULL CHECK (document_reference IS NULL OR btrim(document_reference) <> ''),
    occurred_on                 date NOT NULL,
    due_on                      date NULL,
    notes                       text NULL,
    total_amount                numeric(18,2) NOT NULL DEFAULT 0 CHECK (total_amount >= 0),
    ledger_invoice_movement_id  uuid NULL,
    ledger_reversal_movement_id uuid NULL,
    void_reason                 text NULL,
    created_by_user_id          uuid NOT NULL,
    created_at_utc              timestamptz NOT NULL DEFAULT now(),
    confirmed_by_user_id        uuid NULL,
    confirmed_at_utc            timestamptz NULL,
    voided_by_user_id           uuid NULL,
    voided_at_utc               timestamptz NULL,
    updated_at_utc              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT purchase_receptions_due_not_before_occurred
        CHECK (due_on IS NULL OR due_on >= occurred_on),
    CONSTRAINT purchase_receptions_numbered_ck
        CHECK ((status = 'Draft') = (number IS NULL AND sequence IS NULL AND branch_code IS NULL)
               AND (sequence IS NULL OR sequence >= 1)),
    CONSTRAINT purchase_receptions_state_ck
        CHECK ((status = 'Draft' OR confirmed_at_utc IS NOT NULL)
               AND (status <> 'Voided' OR (voided_at_utc IS NOT NULL AND void_reason IS NOT NULL AND btrim(void_reason) <> ''))),
    -- Targets of the composite foreign keys.
    CONSTRAINT purchase_receptions_org_scoped_uk UNIQUE (organization_id, id),
    CONSTRAINT purchase_receptions_branch_scoped_uk UNIQUE (organization_id, branch_id, id),
    CONSTRAINT purchase_receptions_number_uk UNIQUE (organization_id, branch_id, sequence),
    CONSTRAINT purchase_receptions_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT purchase_receptions_supplier_fk
        FOREIGN KEY (organization_id, supplier_id) REFERENCES suppliers (organization_id, id)
);

-- The same supplier document cannot be confirmed twice (see the header).
CREATE UNIQUE INDEX IF NOT EXISTS purchase_receptions_confirmed_document_uk
    ON purchase_receptions (organization_id, supplier_id, document_type, lower(btrim(document_reference)))
    WHERE status = 'Confirmed' AND document_reference IS NOT NULL;

CREATE INDEX IF NOT EXISTS purchase_receptions_branch_list_idx
    ON purchase_receptions (organization_id, branch_id, occurred_on DESC, created_at_utc DESC);
CREATE INDEX IF NOT EXISTS purchase_receptions_supplier_idx
    ON purchase_receptions (organization_id, supplier_id);

CREATE TABLE IF NOT EXISTS purchase_reception_lines (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL,
    branch_id        uuid NOT NULL,
    reception_id     uuid NOT NULL,
    presentation_id  uuid NOT NULL,
    quantity         numeric(18,3) NOT NULL CHECK (quantity > 0),
    unit_cost        numeric(18,4) NOT NULL CHECK (unit_cost >= 0),
    line_total       numeric(18,2) NOT NULL CHECK (line_total >= 0),
    lot_code         text NULL CHECK (lot_code IS NULL OR btrim(lot_code) <> ''),
    expires_on       date NULL,
    sort_order       integer NOT NULL DEFAULT 0,
    CONSTRAINT purchase_reception_lines_reception_fk
        FOREIGN KEY (organization_id, branch_id, reception_id)
        REFERENCES purchase_receptions (organization_id, branch_id, id) ON DELETE CASCADE,
    CONSTRAINT purchase_reception_lines_presentation_fk
        FOREIGN KEY (branch_id, presentation_id) REFERENCES presentations (branch_id, id)
);

CREATE INDEX IF NOT EXISTS purchase_reception_lines_reception_idx
    ON purchase_reception_lines (organization_id, branch_id, reception_id, sort_order);
CREATE INDEX IF NOT EXISTS purchase_reception_lines_presentation_idx
    ON purchase_reception_lines (branch_id, presentation_id);

CREATE OR REPLACE FUNCTION purchase_reception_lines_require_draft() RETURNS trigger AS $$
DECLARE
    reception_status text;
BEGIN
    SELECT status INTO reception_status
      FROM purchase_receptions
     WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.reception_id ELSE NEW.reception_id END;

    -- A cascading delete of the whole reception (owner maintenance) finds no parent any more: allow it.
    IF TG_OP = 'DELETE' AND reception_status IS NULL THEN
        RETURN OLD;
    END IF;

    IF reception_status IS DISTINCT FROM 'Draft' THEN
        RAISE EXCEPTION 'reception lines can only change while the reception is a draft';
    END IF;

    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS purchase_reception_lines_require_draft ON purchase_reception_lines;
CREATE TRIGGER purchase_reception_lines_require_draft
    BEFORE INSERT OR UPDATE OR DELETE ON purchase_reception_lines
    FOR EACH ROW EXECUTE FUNCTION purchase_reception_lines_require_draft();

ALTER TABLE purchase_receptions      ENABLE ROW LEVEL SECURITY;
ALTER TABLE purchase_receptions      FORCE  ROW LEVEL SECURITY;
ALTER TABLE purchase_reception_lines ENABLE ROW LEVEL SECURITY;
ALTER TABLE purchase_reception_lines FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON purchase_receptions      FROM PUBLIC;
REVOKE ALL ON purchase_reception_lines FROM PUBLIC;
-- No DELETE on a reception: a mistake is a Void. Lines are replaced as a set while the reception is a draft.
GRANT SELECT, INSERT, UPDATE         ON purchase_receptions      TO app_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON purchase_reception_lines TO app_runtime;

DROP POLICY IF EXISTS purchase_receptions_tenant_isolation ON purchase_receptions;
CREATE POLICY purchase_receptions_tenant_isolation ON purchase_receptions
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS purchase_reception_lines_tenant_isolation ON purchase_reception_lines;
CREATE POLICY purchase_reception_lines_tenant_isolation ON purchase_reception_lines
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

COMMIT;

-- purchases-receptions-and-stock: 0033_stock.sql, appended verbatim per the hand-kept
-- mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- purchases-receptions-and-stock (T1): the stock ledger of a branch, its minimum levels and the purchase cost history.
--
-- APPLIED AFTER: 0032_purchase_receptions.sql (and 0016/0017 for the branch-owned presentations).
--
-- STOCK IS CLOUD-AUTHORITATIVE and DERIVED. `stock_movements` is an APPEND-ONLY ledger; the on-hand quantity of a
-- presentation is never stored, it is SUM(quantity) of its movements in the branch (index
-- `stock_movements_on_hand_idx`). `app_runtime` gets SELECT and INSERT only: a movement can never be updated or
-- deleted. A mistake is corrected with a compensating movement (`Reversal`, which points at the original through
-- `reverses_movement_id`; a movement is reversed at most once and a reversal cannot be reversed) or a manual
-- adjustment.
--
-- SIGN CONVENTION. `quantity` is SIGNED, in the presentation's unit (units or kg): Opening and PurchaseReceipt are
-- positive, Sale and Shrinkage are negative (`stock_movements_sign_ck`), Adjustment, CountCorrection and Reversal
-- are either sign, and no movement is zero. Stock may go negative (the POS warns, it does not block).
--
-- IDEMPOTENCY KEY. A movement derived from a document line carries (`source_type`, `source_id`, `source_line_id`):
-- the document kind, the document id and the line id. UNIQUE (organization, branch, source_type, source_line_id) makes
-- projecting the same line twice a no-op for the writer (`INSERT ... ON CONFLICT DO NOTHING`): the synced POS sale
-- line uses source_type 'PosSale' (source_id = sale id, source_line_id = the sale line id). The compensating movement
-- of a void uses ANOTHER source_type for the same line ('PurchaseReceptionVoid', 'PosSaleVoid'), so a void never
-- collides with the original. Manual movements have no source.
--
-- BRANCH OWNED: org AND branch RLS (USING and WITH CHECK), exactly the pattern of 0016/0017; the presentation must
-- belong to the same branch (composite foreign key).
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as comments - NOT executed by this file:
--   DROP TABLE presentation_costs; DROP TABLE stock_minimums; DROP TABLE stock_movements;

BEGIN;

CREATE TABLE IF NOT EXISTS stock_movements (
    id                    uuid PRIMARY KEY,
    organization_id       uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    branch_id             uuid NOT NULL,
    presentation_id       uuid NOT NULL,
    quantity              numeric(18,3) NOT NULL,
    kind                  text NOT NULL CHECK (kind IN (
        'Opening', 'PurchaseReceipt', 'Sale', 'Adjustment', 'Shrinkage', 'CountCorrection', 'Reversal')),
    source_type           text NULL,
    source_id             uuid NULL,
    source_line_id        uuid NULL,
    reverses_movement_id  uuid NULL,
    reason                text NULL,
    lot_code              text NULL,
    occurred_at_utc       timestamptz NOT NULL,
    created_at_utc        timestamptz NOT NULL DEFAULT now(),
    created_by_user_id    uuid NULL,
    -- Generated helpers of the reversal rules (same trick as current_account_movements).
    is_reversible         boolean GENERATED ALWAYS AS (kind <> 'Reversal') STORED,
    reverses_reversible   boolean GENERATED ALWAYS AS (CASE WHEN reverses_movement_id IS NULL THEN NULL ELSE true END) STORED,
    CONSTRAINT stock_movements_quantity_nonzero_ck CHECK (quantity <> 0),
    CONSTRAINT stock_movements_sign_ck CHECK (
        CASE kind
            WHEN 'Opening'         THEN quantity > 0
            WHEN 'PurchaseReceipt' THEN quantity > 0
            WHEN 'Sale'            THEN quantity < 0
            WHEN 'Shrinkage'       THEN quantity < 0
            ELSE true
        END),
    CONSTRAINT stock_movements_source_ck CHECK (
        (source_type IS NULL) = (source_id IS NULL)
        AND (source_line_id IS NULL OR source_type IS NOT NULL)
        AND (source_type IS NULL OR btrim(source_type) <> '')),
    CONSTRAINT stock_movements_reversal_link CHECK ((kind = 'Reversal') = (reverses_movement_id IS NOT NULL)),
    CONSTRAINT stock_movements_branch_scoped_uk UNIQUE (organization_id, branch_id, id),
    CONSTRAINT stock_movements_reversal_target_uk UNIQUE (organization_id, branch_id, id, is_reversible),
    CONSTRAINT stock_movements_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT stock_movements_presentation_fk
        FOREIGN KEY (branch_id, presentation_id) REFERENCES presentations (branch_id, id),
    CONSTRAINT stock_movements_reverses_fk
        FOREIGN KEY (organization_id, branch_id, reverses_movement_id, reverses_reversible)
        REFERENCES stock_movements (organization_id, branch_id, id, is_reversible)
);

-- At most one reversal per movement.
CREATE UNIQUE INDEX IF NOT EXISTS stock_movements_one_reversal_uk
    ON stock_movements (reverses_movement_id) WHERE reverses_movement_id IS NOT NULL;

-- Idempotency key of the movements derived from a document line (see the header).
CREATE UNIQUE INDEX IF NOT EXISTS stock_movements_source_line_uk
    ON stock_movements (organization_id, branch_id, source_type, source_line_id) WHERE source_line_id IS NOT NULL;

-- On hand = SUM(quantity) per presentation, answered from the index.
CREATE INDEX IF NOT EXISTS stock_movements_on_hand_idx
    ON stock_movements (organization_id, branch_id, presentation_id) INCLUDE (quantity, occurred_at_utc);
-- Movement history of a presentation, newest first.
CREATE INDEX IF NOT EXISTS stock_movements_history_idx
    ON stock_movements (organization_id, branch_id, presentation_id, occurred_at_utc DESC, id DESC);
CREATE INDEX IF NOT EXISTS stock_movements_source_idx
    ON stock_movements (organization_id, branch_id, source_type, source_id);

CREATE TABLE IF NOT EXISTS stock_minimums (
    organization_id    uuid NOT NULL,
    branch_id          uuid NOT NULL,
    presentation_id    uuid NOT NULL,
    minimum_quantity   numeric(18,3) NOT NULL CHECK (minimum_quantity >= 0),
    updated_at_utc     timestamptz NOT NULL DEFAULT now(),
    updated_by_user_id uuid NULL,
    CONSTRAINT stock_minimums_pk PRIMARY KEY (organization_id, branch_id, presentation_id),
    CONSTRAINT stock_minimums_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT stock_minimums_presentation_fk
        FOREIGN KEY (branch_id, presentation_id) REFERENCES presentations (branch_id, id) ON DELETE CASCADE
);

-- Purchase cost history: one row per received line, append-only. The current cost of a presentation is its latest
-- row whose reception is still Confirmed (a voided reception stops counting).
CREATE TABLE IF NOT EXISTS presentation_costs (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL,
    branch_id          uuid NOT NULL,
    presentation_id    uuid NOT NULL,
    supplier_id        uuid NOT NULL,
    reception_id       uuid NOT NULL,
    reception_line_id  uuid NULL,
    unit_cost          numeric(18,4) NOT NULL CHECK (unit_cost >= 0),
    occurred_on        date NOT NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT presentation_costs_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT presentation_costs_presentation_fk
        FOREIGN KEY (branch_id, presentation_id) REFERENCES presentations (branch_id, id),
    CONSTRAINT presentation_costs_supplier_fk
        FOREIGN KEY (organization_id, supplier_id) REFERENCES suppliers (organization_id, id),
    CONSTRAINT presentation_costs_reception_fk
        FOREIGN KEY (organization_id, branch_id, reception_id)
        REFERENCES purchase_receptions (organization_id, branch_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS presentation_costs_line_uk
    ON presentation_costs (reception_line_id) WHERE reception_line_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS presentation_costs_presentation_idx
    ON presentation_costs (organization_id, branch_id, presentation_id, occurred_on DESC);

ALTER TABLE stock_movements    ENABLE ROW LEVEL SECURITY;
ALTER TABLE stock_movements    FORCE  ROW LEVEL SECURITY;
ALTER TABLE stock_minimums     ENABLE ROW LEVEL SECURITY;
ALTER TABLE stock_minimums     FORCE  ROW LEVEL SECURITY;
ALTER TABLE presentation_costs ENABLE ROW LEVEL SECURITY;
ALTER TABLE presentation_costs FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON stock_movements    FROM PUBLIC;
REVOKE ALL ON stock_minimums     FROM PUBLIC;
REVOKE ALL ON presentation_costs FROM PUBLIC;
GRANT SELECT, INSERT         ON stock_movements    TO app_runtime;   -- no UPDATE, no DELETE: append-only
GRANT SELECT, INSERT, UPDATE, DELETE ON stock_minimums TO app_runtime;   -- a minimum can be cleared
GRANT SELECT, INSERT         ON presentation_costs TO app_runtime;   -- append-only

DROP POLICY IF EXISTS stock_movements_tenant_isolation ON stock_movements;
CREATE POLICY stock_movements_tenant_isolation ON stock_movements
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS stock_minimums_tenant_isolation ON stock_minimums;
CREATE POLICY stock_minimums_tenant_isolation ON stock_minimums
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

DROP POLICY IF EXISTS presentation_costs_tenant_isolation ON presentation_costs;
CREATE POLICY presentation_costs_tenant_isolation ON presentation_costs
    USING (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    )
    WITH CHECK (
        organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid
        AND branch_id   = NULLIF(current_setting('app.current_branch_id', true), '')::uuid
    );

COMMIT;

-- purchases-receptions-and-stock: 0034_stock_replica_index.sql, appended verbatim per the hand-kept
-- mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- purchases-receptions-and-stock (T5): index for the cloud -> branch stock replica channel (`GET /device/stock/sync`).
--
-- APPLIED AFTER: 0033_stock.sql.
--
-- The replica asks "which presentations of this branch had a movement since the cursor". `created_at_utc` is the
-- monotonic column of that cursor (the same idea as `updated_at_utc` for the customers/catalog replicas); without this
-- index the question is a scan of the whole branch ledger on every sweep of every terminal. The index only changes
-- query plans: no data, no permission and no RLS policy changes.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   DROP INDEX stock_movements_created_idx;

BEGIN;

CREATE INDEX IF NOT EXISTS stock_movements_created_idx
    ON stock_movements (organization_id, branch_id, created_at_utc);

COMMIT;

-- purchases-receptions-and-stock: 0035_organization_settings.sql, appended verbatim per the hand-kept
-- mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- purchases-receptions-and-stock (T7): per-organization number format, the first field of the organization
-- settings. `quantity_decimal_separator` says how this business writes quantities (kilos): `Comma` ("1,5", the
-- default and today's behaviour) or `Dot` ("1.5"). Money formatting is NOT part of it.
--
-- APPLIED AFTER: 0034_stock_replica_index.sql.
--
-- Why columns on `organizations` and not a 1:1 `organization_settings` table: `organizations` already carries the
-- per-business web settings (`logo_url`, `primary_color`, 0015), its tenant-isolation RLS policy and the
-- `app_runtime` SELECT/UPDATE grant already cover a new column (table-level grants apply to every column), and
-- reading a setting needs no join. Each future setting is one more NOT NULL DEFAULT column with its own CHECK.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE organizations DROP CONSTRAINT organizations_quantity_decimal_separator_ck;
--   ALTER TABLE organizations DROP COLUMN quantity_decimal_separator;

BEGIN;

ALTER TABLE organizations
    ADD COLUMN IF NOT EXISTS quantity_decimal_separator text NOT NULL DEFAULT 'Comma';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'organizations_quantity_decimal_separator_ck'
    ) THEN
        ALTER TABLE organizations
            ADD CONSTRAINT organizations_quantity_decimal_separator_ck
            CHECK (quantity_decimal_separator IN ('Comma', 'Dot'));
    END IF;
END $$;

COMMIT;

-- vaca-verde-suppliers-and-catalog-seed: 0036_product_soft_delete.sql, appended verbatim per the hand-kept
-- mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- vaca-verde-suppliers-and-catalog-seed (T1): product soft deletion. A deactivated product stays in the table (its
-- receptions, stock movements, sales and price entries keep pointing at it) but disappears from the default catalog
-- lists, the guest ordering catalog, new receptions and the POS replica. `deactivated_at_utc` records when.
--
-- APPLIED AFTER: 0035_organization_settings.sql.
--
-- The flag lives on the PRODUCT only: the presentations of an inactive product are inactive by implication, so there
-- is one switch to flip and no way to leave a product half active. The branch replica is told about the removal
-- through the existing `removedPresentationIds` of `GET /device/catalog/sync`; the deactivation bumps
-- `products.updated_at_utc`, which that cursor already follows (`products_org_updated`), so no new index or column is
-- needed for it. No index on `is_active` either: a branch catalog is hundreds of rows and the lists already scan it.
-- Table-level grants and the tenant-isolation RLS policy of `products` (0009/0016) already cover the new columns.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE products DROP COLUMN deactivated_at_utc;
--   ALTER TABLE products DROP COLUMN is_active;

BEGIN;

ALTER TABLE products ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true;
ALTER TABLE products ADD COLUMN IF NOT EXISTS deactivated_at_utc timestamptz NULL;

COMMIT;

-- customer-price-lists: 0037_customer_price_lists.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- customer-price-lists (T1): every sale is priced from the list that applies to the buyer. Three references:
--
--   * customers.price_list_id                    - the customer's own price list (NULL = "use the organization default").
--   * organizations.default_customer_price_list_id - the list a customer without one of its own is priced from, and
--                                                  the list the web form pre-selects for a new customer.
--   * price_lists.floor_price_list_id            - the list this list must never price below, product by product.
--
-- APPLIED AFTER: 0036_product_soft_delete.sql.
--
-- Scoping rule (decision): customers and organizations are ORGANIZATION scoped while price lists are BRANCH scoped
-- since 0017. A customer (or the organization default) therefore points at ONE price list of its organization, enforced
-- by the composite foreign key (organization_id, id) of 0014 (`price_lists_org_scoped_uk`). Resolution only honours the
-- reference when that list is visible in the branch that is selling; otherwise it falls back to the selling branch's
-- default list (see `BuyerPriceListResolver`). A branch that wants customers priced differently owns its own lists.
-- A floor list stays inside the SAME branch (composite key `price_lists_branch_scoped_uk`, 0017) and is never the list
-- itself; longer cycles are refused by the API.
--
-- Nothing here deletes: price lists keep NO DELETE grant for `app_runtime`, so a referenced list cannot disappear
-- under a customer (the default `NO ACTION` of the foreign keys is the second guard). Table-level grants and the
-- tenant-isolation policies of `customers`, `organizations` and `price_lists` already cover the new columns.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE price_lists DROP COLUMN floor_price_list_id;
--   ALTER TABLE organizations DROP COLUMN default_customer_price_list_id;
--   ALTER TABLE customers DROP COLUMN price_list_id;

BEGIN;

ALTER TABLE customers     ADD COLUMN IF NOT EXISTS price_list_id uuid NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS default_customer_price_list_id uuid NULL;
ALTER TABLE price_lists   ADD COLUMN IF NOT EXISTS floor_price_list_id uuid NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_price_list_org_fk') THEN
        ALTER TABLE customers
            ADD CONSTRAINT customers_price_list_org_fk
            FOREIGN KEY (organization_id, price_list_id) REFERENCES price_lists (organization_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'organizations_default_customer_price_list_fk') THEN
        ALTER TABLE organizations
            ADD CONSTRAINT organizations_default_customer_price_list_fk
            FOREIGN KEY (id, default_customer_price_list_id) REFERENCES price_lists (organization_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_lists_floor_branch_fk') THEN
        ALTER TABLE price_lists
            ADD CONSTRAINT price_lists_floor_branch_fk
            FOREIGN KEY (branch_id, floor_price_list_id) REFERENCES price_lists (branch_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_lists_floor_not_self_ck') THEN
        ALTER TABLE price_lists
            ADD CONSTRAINT price_lists_floor_not_self_ck
            CHECK (floor_price_list_id IS NULL OR floor_price_list_id <> id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS customers_price_list_idx ON customers (price_list_id) WHERE price_list_id IS NOT NULL;

COMMIT;

-- customer-price-lists: 0038_order_line_price_provenance.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- customer-price-lists (T6): when the buyer's list has no effective price for a presentation, the organization default
-- list (Mostrador) prices it. Each order line records which list priced it:
--
--   * order_lines.priced_from_price_list_id - the price list that priced the line (NULL on lines stored before this).
--   * order_lines.price_fell_back           - true when the buyer's own list had no price and the default list priced it.
--
-- APPLIED AFTER: 0037_customer_price_lists.sql.
--
-- Provenance is a snapshot like the rest of the line (ADR-003): no foreign key, so it can never block or change later
-- list maintenance. Table-level grants and the tenant-isolation policy of `order_lines` already cover the new columns.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE order_lines DROP COLUMN price_fell_back;
--   ALTER TABLE order_lines DROP COLUMN priced_from_price_list_id;

BEGIN;

ALTER TABLE order_lines ADD COLUMN IF NOT EXISTS priced_from_price_list_id uuid NULL;
ALTER TABLE order_lines ADD COLUMN IF NOT EXISTS price_fell_back boolean NOT NULL DEFAULT false;

COMMIT;

-- admin-console-field-fixes: 0039_organization_country_and_city_postal_code.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- admin-console-field-fixes (T2): the country of an organization and the postal code of a city.
--
--   * organizations.country_code - the organization's country (`countries.code`). NOT NULL DEFAULT 'AR': adding the
--     column backfills every existing organization with Argentina, the only country loaded. The customer form lists
--     the provinces of this country.
--   * cities.postal_code         - optional postal code of a city, maintained by the platform system administrator
--     (Georef/INDEC publishes none, so nothing is invented). An Argentine CP of 4 digits ("2000") or a CPA: the
--     province letter (no I or O), 4 digits and 3 letters ("S2000ABC"), always upper case.
--
-- APPLIED AFTER: 0038_order_line_price_provenance.sql.
--
-- Table-level grants and policies already cover the new columns: `app_runtime` reads/updates `organizations` under
-- its tenant-isolation policy (0003) and reads/inserts/updates the global `cities` (0028).
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE cities DROP CONSTRAINT cities_postal_code_format_ck;
--   ALTER TABLE cities DROP COLUMN postal_code;
--   ALTER TABLE organizations DROP CONSTRAINT organizations_country_fk;
--   ALTER TABLE organizations DROP COLUMN country_code;

BEGIN;

ALTER TABLE organizations ADD COLUMN IF NOT EXISTS country_code text NOT NULL DEFAULT 'AR';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'organizations_country_fk') THEN
        ALTER TABLE organizations
            ADD CONSTRAINT organizations_country_fk FOREIGN KEY (country_code) REFERENCES countries (code);
    END IF;
END $$;

ALTER TABLE cities ADD COLUMN IF NOT EXISTS postal_code text NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'cities_postal_code_format_ck') THEN
        ALTER TABLE cities
            ADD CONSTRAINT cities_postal_code_format_ck
            CHECK (postal_code IS NULL OR postal_code ~ '^([0-9]{4}|[A-HJ-NP-Z][0-9]{4}[A-Z]{3})$');
    END IF;
END $$;

COMMIT;

-- admin-console-field-fixes: 0040_customer_party_type.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- admin-console-field-fixes (T3b): whether a customer is a person or a company, independent of the commercial
-- Retail / Wholesale kind. It decides what the single customer name (`display_name`) means: a person's full name or a
-- company's legal name. `legal_name`, `locality` and `province` stay as columns but the admin API no longer writes them.
--
--   * customers.party_type - 'Person' | 'Company'. Existing customers are backfilled as 'Company' when their tax id
--     type is 'Cuit', otherwise 'Person' (editable afterwards). DEFAULT 'Person' for rows inserted without it; the API
--     always sends it.
--
-- APPLIED AFTER: 0039_organization_country_and_city_postal_code.sql.
--
-- The backfill only fills rows that have no party type yet, so re-running never overwrites an edited one. The
-- tenant-isolation policy and table-level grants of `customers` already cover the new column. The branch replica and
-- the device sync contract are unchanged.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE customers DROP CONSTRAINT customers_party_type_ck;
--   ALTER TABLE customers DROP COLUMN party_type;

BEGIN;

ALTER TABLE customers ADD COLUMN IF NOT EXISTS party_type text NULL;

UPDATE customers
SET party_type = CASE WHEN tax_id_type = 'Cuit' THEN 'Company' ELSE 'Person' END
WHERE party_type IS NULL;

ALTER TABLE customers ALTER COLUMN party_type SET DEFAULT 'Person';
ALTER TABLE customers ALTER COLUMN party_type SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_party_type_ck') THEN
        ALTER TABLE customers
            ADD CONSTRAINT customers_party_type_ck CHECK (party_type IN ('Person', 'Company'));
    END IF;
END $$;

COMMIT;

-- staff-order-taking: 0041_take_orders_permission.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only data migration.
--
-- staff-order-taking (T1): staff take orders for customers from the web with the new `TakeOrders` permission
-- (bit 32), granted to `seller` and `business-admin` in RoleCatalog. Roles are persisted per user as
-- `{"name", "permissions"}` in `users.roles` and the effective permissions are read from those rows (see 0020), so
-- every existing `seller` and `business-admin` entry must gain the bit or no existing user could take an order.
-- `cashier`, `provider` and `platform-admin` deliberately do NOT gain it. APPLIED AFTER: 0040_customer_party_type.sql.
--
-- `users` has `FORCE ROW LEVEL SECURITY` (0002): with no `app.current_org_id` even the owner sees ZERO rows, so an
-- unguarded UPDATE would "succeed" while touching nothing (the hazard 0006/0020 document). FORCE is switched off for
-- the owner ONLY inside this one transaction and restored before COMMIT; the post-condition aborts the whole
-- transaction if any seller or business-admin entry still lacks the bit.
--
-- Idempotent: the permission is combined with a bitwise OR, so a second run rewrites the same value (and the WHERE
-- skips rows that already carry it).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
--   UPDATE users SET roles = (
--       SELECT jsonb_agg(CASE WHEN e->>'name' IN ('seller', 'business-admin')
--           THEN jsonb_set(e, '{permissions}', to_jsonb(((e->>'permissions')::int) & ~32))
--           ELSE e END)
--       FROM jsonb_array_elements(roles) AS e)
--    WHERE roles @> '[{"name":"seller"}]' OR roles @> '[{"name":"business-admin"}]';
--   ALTER TABLE users FORCE ROW LEVEL SECURITY;
--   COMMIT;

BEGIN;

ALTER TABLE users NO FORCE ROW LEVEL SECURITY;

UPDATE users
   SET roles = (
       SELECT jsonb_agg(
           CASE WHEN e->>'name' IN ('seller', 'business-admin')
                THEN jsonb_set(e, '{permissions}', to_jsonb(((e->>'permissions')::int) | 32))
                ELSE e END
           ORDER BY ord)
       FROM jsonb_array_elements(roles) WITH ORDINALITY AS r(e, ord))
 WHERE EXISTS (
       SELECT 1 FROM jsonb_array_elements(roles) AS e
        WHERE e->>'name' IN ('seller', 'business-admin')
          AND (((e->>'permissions')::int) & 32) = 0);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM users u, jsonb_array_elements(u.roles) AS e
         WHERE e->>'name' IN ('seller', 'business-admin')
           AND (((e->>'permissions')::int) & 32) = 0) THEN
        RAISE EXCEPTION '0041: a seller or business-admin role entry survived without TakeOrders';
    END IF;
END
$$;

ALTER TABLE users FORCE ROW LEVEL SECURITY;

COMMIT;

-- staff-order-taking: 0042_staff_order_entry.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- staff-order-taking (T2): a staff member (seller, business admin, or a system administrator acting on the
-- organization) takes an order for a customer from the web. The order records who took it and an optional note.
--
--   * orders.taken_by_user_id - the signed-in staff member who took the order (always the caller, never a request
--     field). NULL for orders the customer or a guest submitted themselves.
--   * orders.note             - optional free text the staff member typed for the order, at most 500 characters.
--
-- APPLIED AFTER: 0041_take_orders_permission.sql (and 0025_orders.sql, which creates `orders`).
--
-- Both columns are written once, at insert, like the rest of the order's identity: the table-level SELECT and INSERT
-- grants of 0025 already cover them and the column-limited UPDATE grant (status, pending_reason) deliberately does
-- not. The tenant-isolation policy of 0025 is unchanged. No foreign key to `users`: a system administrator's own row
-- lives in another organization, and the order keeps the id even if the user is later removed.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE orders DROP CONSTRAINT orders_note_length_ck;
--   ALTER TABLE orders DROP COLUMN note;
--   ALTER TABLE orders DROP COLUMN taken_by_user_id;

BEGIN;

ALTER TABLE orders ADD COLUMN IF NOT EXISTS taken_by_user_id uuid NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS note text NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'orders_note_length_ck') THEN
        ALTER TABLE orders
            ADD CONSTRAINT orders_note_length_ck CHECK (note IS NULL OR char_length(note) <= 500);
    END IF;
END $$;

COMMIT;

-- price-editing-and-desktop-polish: 0043_price_entry_same_day_correction.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- price-editing-and-desktop-polish (T4): same-day price corrections from the batch publish.
--
-- `price_list_entries` is append-only on purpose: app_runtime has held only SELECT and INSERT on it since 0009, so a
-- published price is never rewritten and the history is the record of every price a list ever had. The owner needs ONE
-- narrow exception: publishing a batch of prices (`POST /pricing/price-lists/{id}/entries/batch`) for a presentation
-- that already has an entry effective that SAME day replaces that entry's price as a correction, because
-- `price_list_entries_one_per_day` allows a single entry per (list, presentation, day).
--
-- The exception, kept as narrow as possible:
--   * a column-scoped UPDATE grant: only `unit_price`, `created_at_utc` (bumped so the POS catalog replica, whose cursor
--     is `created_at_utc > since`, picks the correction up) and `created_by_user_id` (who corrected it). The list,
--     presentation, effective date, organization, branch, source and import batch of an entry stay immutable, and
--     there is still no DELETE grant;
--   * the only code path that issues it is `PostgresPriceListStore.PublishEntriesAsync`, inside the same transaction
--     that writes the batch's audit row with the old and new price of every replaced entry. The single-entry
--     `POST .../entries` keeps refusing a second price for the same day (409 entry-already-exists-for-date);
--   * no policy change: `price_list_entries_tenant_isolation` (0017) applies to every command with USING and WITH CHECK
--     on the organization AND the branch, so an UPDATE only ever sees and keeps rows of the caller's own tenant.
--
-- APPLIED AFTER: 0042_staff_order_entry.sql (and 0009/0017, which create the table and its branch-scoped policy).
--
-- The whole file runs in ONE transaction and can be re-run safely (GRANT is idempotent).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   REVOKE UPDATE (unit_price, created_at_utc, created_by_user_id) ON price_list_entries FROM app_runtime;

BEGIN;

GRANT UPDATE (unit_price, created_at_utc, created_by_user_id) ON price_list_entries TO app_runtime;

COMMIT;

-- pos-sales-history-and-void: 0044_pos_sale_voids.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- POS sale voids: a terminal can void (annul) a sale of its open cash session,
-- authorized with the branch PIN. The sale itself is never rewritten (`pos_sales`
-- is append-only): the void arrives as its own `sale.voided` envelope and is
-- projected here, one row per voided sale, next to an audit row `sale.voided`.
-- The stock the sale took out is put back with `Reversal` movements of source
-- `PosSaleVoid` (reserved since 0033), one per original `PosSale` movement.
-- APPLIED AFTER: 0043_price_entry_same_day_correction.sql.
--
--   sale_id        the voided sale. No foreign key to pos_sales: the void may be
--                  ingested before its sale (the terminal pushes in order, but a
--                  failed push of the sale does not stop the next one).
--   reason         required, what the operator typed (at most 200 characters on
--                  the terminal; plain text here so ingestion never fails on it).
--   authorized_by  the operator whose PIN authorized the void, and the PIN
--                  version, as for a discount.
--
-- Append-only like `pos_sales` (0023): app_runtime gets SELECT, INSERT, no UPDATE
-- or DELETE; a void is never undone. FORCE ROW LEVEL SECURITY with the symmetric
-- tenant-isolation policy (NULLIF pooler-safety hardening from 0001 - never regress
-- it). The composite foreign key to branches follows 0014/0016/0023.
--
-- Deploy order: apply this BEFORE the API version that projects voids. An API
-- deployed ahead of it still ingests every void (the projection runs in a savepoint
-- and is skipped with a log line); the envelope stays in `sync_inbox`.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS pos_sale_voids;
--   COMMIT;

BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS pos_sale_voids (
    organization_id       uuid        NOT NULL,
    branch_id             uuid        NOT NULL,
    sale_id               uuid        NOT NULL,
    operation_id          uuid        NOT NULL,
    voided_at_utc         timestamptz NOT NULL,
    voided_by_operator_id uuid        NOT NULL,
    authorized_by         uuid        NOT NULL,
    pin_version           bigint      NOT NULL,
    reason                text        NOT NULL,
    total_amount          numeric     NOT NULL,
    cash_session_id       uuid        NULL,
    recorded_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pos_sale_voids_pk PRIMARY KEY (organization_id, sale_id),
    CONSTRAINT pos_sale_voids_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS pos_sale_voids_branch_time_idx
    ON pos_sale_voids (organization_id, branch_id, voided_at_utc);

ALTER TABLE pos_sale_voids ENABLE ROW LEVEL SECURITY;
ALTER TABLE pos_sale_voids FORCE ROW LEVEL SECURITY;
REVOKE ALL ON pos_sale_voids FROM PUBLIC;
GRANT SELECT, INSERT ON pos_sale_voids TO app_runtime;   -- no UPDATE, no DELETE: append-only

DROP POLICY IF EXISTS pos_sale_voids_tenant_isolation ON pos_sale_voids;
CREATE POLICY pos_sale_voids_tenant_isolation ON pos_sale_voids
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;

-- order-fulfillment-and-delivery: 0045_order_fulfillment_and_delivery.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Order fulfillment, delivery runs and remitos (delivery notes).
-- APPLIED AFTER: 0044_pos_sale_voids.sql.
--
-- 1. ORDERS get an operational status, separate from the ADR-003 sync status
--    (`status`/`pending_reason`, untouched):
--      fulfillment_status  Confirmed -> InPreparation -> ReadyToDispatch -> OutForDelivery
--                          -> Delivered | PartiallyDelivered; Cancelled before dispatch.
--                          Existing orders start as Confirmed.
--      remito_sequence     the order's remito number within its branch (R01-00000042),
--                          assigned once, the first time the remito is printed or the
--                          order is dispatched; `remito_counters` hands them out.
--      settlement          how a delivered order was settled: on the customer's current
--                          account, or paid on delivery (also every guest order).
--      delivered_total     what was delivered, at the order's net prices.
--    The new columns get a column-level UPDATE grant; order_lines stay frozen.
--
-- 2. DELIVERY RUNS (repartos): a date, a driver and a vehicle, and the ordered list of
--    the orders the truck takes (`delivery_run_orders`, an order is in at most one run).
--    Planned -> OutForDelivery (dispatch) -> Completed (settled on return).
--    `order_line_deliveries` keeps what was really delivered per line (kilos may differ
--    from the order).
--
-- 3. CURRENT ACCOUNTS for CUSTOMERS: `current_account_movements` (0031, supplier-only)
--    accepts party_kind 'Customer' with its `customer_id`. For a customer the balance is
--    what the CUSTOMER owes: a delivery is a Debit, a payment a Credit. `source_type` /
--    `source_id` make a posting idempotent (one movement per delivered order).
--
-- 4. DOCUMENT DATA of the organization (legal name, CUIT, tax condition, gross income
--    number, activity start, fiscal address, footer) and of each branch (address,
--    locality, phone, e-mail, warehouse) printed on remitos. All nullable: existing
--    rows simply have none yet. Covered by the tables' existing grants and policies.
--
-- RLS: every new table is org-scoped with FORCE ROW LEVEL SECURITY and the symmetric
-- tenant-isolation policy (NULLIF pooler-safety hardening from 0001 - never regress it),
-- like `orders` (0025). Composite foreign keys keep every reference inside the tenant.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS order_line_deliveries, delivery_run_orders, delivery_runs, remito_counters;
--   ALTER TABLE orders DROP COLUMN IF EXISTS fulfillment_status, DROP COLUMN IF EXISTS fulfillment_updated_at,
--       DROP COLUMN IF EXISTS cancel_reason, DROP COLUMN IF EXISTS remito_sequence, DROP COLUMN IF EXISTS settlement,
--       DROP COLUMN IF EXISTS delivered_total, DROP COLUMN IF EXISTS delivered_at;
--   DROP INDEX IF EXISTS current_account_movements_source_uk, current_account_movements_customer_idx;
--   ALTER TABLE current_account_movements DROP CONSTRAINT IF EXISTS current_account_movements_party_customer,
--       DROP CONSTRAINT IF EXISTS current_account_movements_customer_fk,
--       DROP CONSTRAINT IF EXISTS current_account_movements_party_kind_ck,
--       DROP COLUMN IF EXISTS customer_id, DROP COLUMN IF EXISTS source_type, DROP COLUMN IF EXISTS source_id;
--   ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_party_kind_check
--       CHECK (party_kind IN ('Supplier'));   -- only when no Customer movement exists
--   ALTER TABLE organizations DROP COLUMN IF EXISTS legal_name, DROP COLUMN IF EXISTS tax_id,
--       DROP COLUMN IF EXISTS tax_condition, DROP COLUMN IF EXISTS gross_income_number,
--       DROP COLUMN IF EXISTS activity_start_date, DROP COLUMN IF EXISTS fiscal_address,
--       DROP COLUMN IF EXISTS document_footer;
--   ALTER TABLE branches DROP COLUMN IF EXISTS address, DROP COLUMN IF EXISTS locality,
--       DROP COLUMN IF EXISTS phone, DROP COLUMN IF EXISTS email, DROP COLUMN IF EXISTS warehouse_address;
--   COMMIT;

BEGIN;

-- ---- 1. orders -------------------------------------------------------------------------

ALTER TABLE orders ADD COLUMN IF NOT EXISTS fulfillment_status     text          NOT NULL DEFAULT 'Confirmed';
ALTER TABLE orders ADD COLUMN IF NOT EXISTS fulfillment_updated_at timestamptz   NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS cancel_reason          text          NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS remito_sequence        integer       NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS settlement             text          NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS delivered_total        numeric(18,2) NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS delivered_at           timestamptz   NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'orders_fulfillment_status_ck') THEN
        ALTER TABLE orders ADD CONSTRAINT orders_fulfillment_status_ck CHECK (fulfillment_status IN (
            'Confirmed', 'InPreparation', 'ReadyToDispatch', 'OutForDelivery', 'Delivered', 'PartiallyDelivered', 'Cancelled'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'orders_settlement_ck') THEN
        ALTER TABLE orders ADD CONSTRAINT orders_settlement_ck
            CHECK (settlement IS NULL OR settlement IN ('CurrentAccount', 'PaidOnDelivery'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'orders_cancel_reason_ck') THEN
        ALTER TABLE orders ADD CONSTRAINT orders_cancel_reason_ck
            CHECK (cancel_reason IS NULL OR char_length(cancel_reason) <= 200);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'orders_remito_sequence_ck') THEN
        ALTER TABLE orders ADD CONSTRAINT orders_remito_sequence_ck CHECK (remito_sequence IS NULL OR remito_sequence >= 1);
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS orders_remito_number_uk
    ON orders (organization_id, destination_branch_id, remito_sequence) WHERE remito_sequence IS NOT NULL;
CREATE INDEX IF NOT EXISTS orders_fulfillment_idx
    ON orders (organization_id, destination_branch_id, fulfillment_status, submitted_at_utc);

GRANT UPDATE (fulfillment_status, fulfillment_updated_at, cancel_reason, remito_sequence, settlement, delivered_total, delivered_at)
    ON orders TO app_runtime;

CREATE TABLE IF NOT EXISTS remito_counters (
    organization_id uuid    NOT NULL,
    branch_id       uuid    NOT NULL,
    last_sequence   integer NOT NULL CHECK (last_sequence >= 0),
    CONSTRAINT remito_counters_pk PRIMARY KEY (organization_id, branch_id),
    CONSTRAINT remito_counters_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

ALTER TABLE remito_counters ENABLE ROW LEVEL SECURITY;
ALTER TABLE remito_counters FORCE ROW LEVEL SECURITY;
REVOKE ALL ON remito_counters FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON remito_counters TO app_runtime;
DROP POLICY IF EXISTS remito_counters_tenant_isolation ON remito_counters;
CREATE POLICY remito_counters_tenant_isolation ON remito_counters
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ---- 2. delivery runs ------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS delivery_runs (
    organization_id    uuid        NOT NULL,
    id                 uuid        NOT NULL,
    branch_id          uuid        NOT NULL,
    run_number         integer     NOT NULL CHECK (run_number >= 1),
    run_date           date        NOT NULL,
    driver_name        text        NULL CHECK (driver_name IS NULL OR char_length(driver_name) <= 120),
    vehicle            text        NULL CHECK (vehicle IS NULL OR char_length(vehicle) <= 120),
    notes              text        NULL CHECK (notes IS NULL OR char_length(notes) <= 500),
    status             text        NOT NULL DEFAULT 'Planned' CHECK (status IN ('Planned', 'OutForDelivery', 'Completed')),
    created_at         timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid        NOT NULL,
    dispatched_at      timestamptz NULL,
    completed_at       timestamptz NULL,
    CONSTRAINT delivery_runs_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT delivery_runs_number_uk UNIQUE (organization_id, branch_id, run_number),
    CONSTRAINT delivery_runs_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS delivery_runs_branch_date_idx ON delivery_runs (organization_id, branch_id, run_date);

ALTER TABLE delivery_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery_runs FORCE ROW LEVEL SECURITY;
REVOKE ALL ON delivery_runs FROM PUBLIC;
GRANT SELECT, INSERT ON delivery_runs TO app_runtime;
GRANT UPDATE (run_date, driver_name, vehicle, notes, status, dispatched_at, completed_at) ON delivery_runs TO app_runtime;
DROP POLICY IF EXISTS delivery_runs_tenant_isolation ON delivery_runs;
CREATE POLICY delivery_runs_tenant_isolation ON delivery_runs
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

CREATE TABLE IF NOT EXISTS delivery_run_orders (
    organization_id uuid    NOT NULL,
    run_id          uuid    NOT NULL,
    order_id        uuid    NOT NULL,
    stop_no         integer NOT NULL CHECK (stop_no >= 1),
    CONSTRAINT delivery_run_orders_pk PRIMARY KEY (organization_id, run_id, order_id),
    CONSTRAINT delivery_run_orders_one_run_uk UNIQUE (organization_id, order_id),
    CONSTRAINT delivery_run_orders_run_fk
        FOREIGN KEY (organization_id, run_id) REFERENCES delivery_runs (organization_id, id) ON DELETE CASCADE,
    CONSTRAINT delivery_run_orders_order_fk
        FOREIGN KEY (organization_id, order_id) REFERENCES orders (organization_id, order_id) ON DELETE CASCADE
);

ALTER TABLE delivery_run_orders ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery_run_orders FORCE ROW LEVEL SECURITY;
REVOKE ALL ON delivery_run_orders FROM PUBLIC;
GRANT SELECT, INSERT, DELETE ON delivery_run_orders TO app_runtime;
GRANT UPDATE (stop_no) ON delivery_run_orders TO app_runtime;
DROP POLICY IF EXISTS delivery_run_orders_tenant_isolation ON delivery_run_orders;
CREATE POLICY delivery_run_orders_tenant_isolation ON delivery_run_orders
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

CREATE TABLE IF NOT EXISTS order_line_deliveries (
    organization_id    uuid          NOT NULL,
    order_id           uuid          NOT NULL,
    line_no            integer       NOT NULL,
    delivered_quantity numeric(18,3) NOT NULL CHECK (delivered_quantity >= 0),
    recorded_at        timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT order_line_deliveries_pk PRIMARY KEY (organization_id, order_id, line_no),
    CONSTRAINT order_line_deliveries_line_fk
        FOREIGN KEY (organization_id, order_id, line_no) REFERENCES order_lines (organization_id, order_id, line_no) ON DELETE CASCADE
);

ALTER TABLE order_line_deliveries ENABLE ROW LEVEL SECURITY;
ALTER TABLE order_line_deliveries FORCE ROW LEVEL SECURITY;
REVOKE ALL ON order_line_deliveries FROM PUBLIC;
GRANT SELECT, INSERT ON order_line_deliveries TO app_runtime;   -- written once, at settlement
DROP POLICY IF EXISTS order_line_deliveries_tenant_isolation ON order_line_deliveries;
CREATE POLICY order_line_deliveries_tenant_isolation ON order_line_deliveries
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ---- 3. customer current accounts -----------------------------------------------------

ALTER TABLE current_account_movements ADD COLUMN IF NOT EXISTS customer_id uuid NULL;
ALTER TABLE current_account_movements ADD COLUMN IF NOT EXISTS source_type text NULL;
ALTER TABLE current_account_movements ADD COLUMN IF NOT EXISTS source_id   uuid NULL;

-- 0031 declared the party kind inline, so Postgres named it current_account_movements_party_kind_check; it is
-- replaced by a named one that also admits customers.
ALTER TABLE current_account_movements DROP CONSTRAINT IF EXISTS current_account_movements_party_kind_check;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_party_kind_ck') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_party_kind_ck
            CHECK (party_kind IN ('Supplier', 'Customer'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_party_customer') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_party_customer
            CHECK (party_kind <> 'Customer' OR customer_id = party_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_org_scoped_uk') THEN
        ALTER TABLE customers ADD CONSTRAINT customers_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_customer_fk') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_customer_fk
            FOREIGN KEY (organization_id, customer_id) REFERENCES customers (organization_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_source_ck') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_source_ck
            CHECK ((source_type IS NULL) = (source_id IS NULL));
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS current_account_movements_source_uk
    ON current_account_movements (organization_id, party_id, source_type, source_id) WHERE source_type IS NOT NULL;
CREATE INDEX IF NOT EXISTS current_account_movements_customer_idx
    ON current_account_movements (organization_id, customer_id, occurred_on, created_at_utc) WHERE customer_id IS NOT NULL;

-- ---- 4. document data -----------------------------------------------------------------

ALTER TABLE organizations ADD COLUMN IF NOT EXISTS legal_name          text NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS tax_id              text NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS tax_condition       text NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS gross_income_number text NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS activity_start_date date NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS fiscal_address      text NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS document_footer     text NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'organizations_document_data_ck') THEN
        ALTER TABLE organizations ADD CONSTRAINT organizations_document_data_ck CHECK (
            (legal_name IS NULL OR char_length(legal_name) <= 200)
            AND (tax_id IS NULL OR tax_id ~ '^[0-9]{11}$')
            AND (tax_condition IS NULL OR tax_condition IN ('ResponsableInscripto', 'Monotributo', 'Exento', 'ConsumidorFinal', 'NoAplica'))
            AND (gross_income_number IS NULL OR char_length(gross_income_number) <= 40)
            AND (fiscal_address IS NULL OR char_length(fiscal_address) <= 200)
            AND (document_footer IS NULL OR char_length(document_footer) <= 300));
    END IF;
END $$;

ALTER TABLE branches ADD COLUMN IF NOT EXISTS address           text NULL;
ALTER TABLE branches ADD COLUMN IF NOT EXISTS locality          text NULL;
ALTER TABLE branches ADD COLUMN IF NOT EXISTS phone             text NULL;
ALTER TABLE branches ADD COLUMN IF NOT EXISTS email             text NULL;
ALTER TABLE branches ADD COLUMN IF NOT EXISTS warehouse_address text NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_document_data_ck') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_document_data_ck CHECK (
            (address IS NULL OR char_length(address) <= 200)
            AND (locality IS NULL OR char_length(locality) <= 120)
            AND (phone IS NULL OR char_length(phone) <= 60)
            AND (email IS NULL OR char_length(email) <= 200)
            AND (warehouse_address IS NULL OR char_length(warehouse_address) <= 200));
    END IF;
END $$;

COMMIT;

-- payment-terms-and-treasury: 0046_payment_terms_and_treasury.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Payment terms of customers and the company's treasury.
-- APPLIED AFTER: 0045_order_fulfillment_and_delivery.sql.
--
-- 1. PAYMENT TERMS. A sale or delivery on a customer's current account is due after the customer's own payment terms
--    (`customers.payment_terms_days`) or, when the customer has none, after the organization's default
--    (`organizations.default_customer_payment_terms_days`, 30 days unless changed). 0 means due the same day. The
--    free-text `customers.payment_terms` stays as a note; the days are what due dates are computed from.
--
-- 2. TREASURY (tesorería): the company's money accounts, one per branch and payment method (Cash = the drawer, Card,
--    Qr), created on first use, and their append-only movements. Every POS sale paid at the counter, every payment a
--    customer makes at the POS, and every delivery paid on delivery puts money In; a void takes it back Out with a
--    Reversal (never an update or a delete). `source_type` + `source_id` make each posting idempotent (a retried
--    projection never counts money twice); a movement is reversed at most once.
--
-- RLS: both tables are org-scoped with FORCE ROW LEVEL SECURITY and the symmetric tenant-isolation policy (NULLIF
-- pooler-safety hardening from 0001 - never regress it). SELECT and INSERT only: money is never rewritten.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS treasury_movements, treasury_accounts;
--   ALTER TABLE customers DROP COLUMN IF EXISTS payment_terms_days;
--   ALTER TABLE organizations DROP COLUMN IF EXISTS default_customer_payment_terms_days;
--   COMMIT;

BEGIN;

-- ---- 1. payment terms ------------------------------------------------------------------

ALTER TABLE customers ADD COLUMN IF NOT EXISTS payment_terms_days smallint NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS default_customer_payment_terms_days smallint NOT NULL DEFAULT 30;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_payment_terms_days_ck') THEN
        ALTER TABLE customers ADD CONSTRAINT customers_payment_terms_days_ck
            CHECK (payment_terms_days IS NULL OR payment_terms_days BETWEEN 0 AND 365);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'organizations_default_payment_terms_ck') THEN
        ALTER TABLE organizations ADD CONSTRAINT organizations_default_payment_terms_ck
            CHECK (default_customer_payment_terms_days BETWEEN 0 AND 365);
    END IF;
END $$;

-- ---- 2. treasury -----------------------------------------------------------------------

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'branches_org_scoped_uk') THEN
        ALTER TABLE branches ADD CONSTRAINT branches_org_scoped_uk UNIQUE (organization_id, id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS treasury_accounts (
    organization_id uuid        NOT NULL,
    id              uuid        NOT NULL,
    branch_id       uuid        NOT NULL,
    kind            text        NOT NULL CHECK (kind IN ('Cash', 'Card', 'Qr')),
    name            text        NOT NULL CHECK (btrim(name) <> '' AND char_length(name) <= 120),
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT treasury_accounts_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT treasury_accounts_branch_kind_uk UNIQUE (organization_id, branch_id, kind),
    CONSTRAINT treasury_accounts_branch_fk
        FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id) ON DELETE CASCADE
);

ALTER TABLE treasury_accounts ENABLE ROW LEVEL SECURITY;
ALTER TABLE treasury_accounts FORCE ROW LEVEL SECURITY;
REVOKE ALL ON treasury_accounts FROM PUBLIC;
GRANT SELECT, INSERT ON treasury_accounts TO app_runtime;
DROP POLICY IF EXISTS treasury_accounts_tenant_isolation ON treasury_accounts;
CREATE POLICY treasury_accounts_tenant_isolation ON treasury_accounts
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

CREATE TABLE IF NOT EXISTS treasury_movements (
    organization_id      uuid          NOT NULL,
    id                   uuid          NOT NULL,
    account_id           uuid          NOT NULL,
    kind                 text          NOT NULL CHECK (kind IN ('Sale', 'CustomerPayment', 'DeliveryPayment', 'Reversal')),
    direction            text          NOT NULL CHECK (direction IN ('In', 'Out')),
    amount               numeric(18,2) NOT NULL CHECK (amount > 0),
    occurred_at_utc      timestamptz   NOT NULL,
    business_date        date          NOT NULL,
    concept              text          NOT NULL CHECK (btrim(concept) <> ''),
    document_reference   text          NULL,
    customer_id          uuid          NULL,
    source_type          text          NULL,
    source_id            uuid          NULL,
    reverses_movement_id uuid          NULL,
    created_by_user_id   uuid          NOT NULL,
    created_at           timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT treasury_movements_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT treasury_movements_account_fk
        FOREIGN KEY (organization_id, account_id) REFERENCES treasury_accounts (organization_id, id),
    CONSTRAINT treasury_movements_reversal_link CHECK ((kind = 'Reversal') = (reverses_movement_id IS NOT NULL)),
    CONSTRAINT treasury_movements_source_ck CHECK ((source_type IS NULL) = (source_id IS NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS treasury_movements_source_uk
    ON treasury_movements (organization_id, source_type, source_id) WHERE source_type IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS treasury_movements_one_reversal_uk
    ON treasury_movements (organization_id, reverses_movement_id) WHERE reverses_movement_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS treasury_movements_account_date_idx
    ON treasury_movements (organization_id, account_id, business_date, created_at);

ALTER TABLE treasury_movements ENABLE ROW LEVEL SECURITY;
ALTER TABLE treasury_movements FORCE ROW LEVEL SECURITY;
REVOKE ALL ON treasury_movements FROM PUBLIC;
GRANT SELECT, INSERT ON treasury_movements TO app_runtime;   -- append-only: money is never rewritten
DROP POLICY IF EXISTS treasury_movements_tenant_isolation ON treasury_movements;
CREATE POLICY treasury_movements_tenant_isolation ON treasury_movements
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;

-- treasury-accounts-and-cash-movements: 0047_treasury_accounts_and_cash_movements.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Treasury accounts entered by the administration, manual movements and transfers, cash drawer movements of the POS
-- and the cash count difference of a closed cash session.
-- APPLIED AFTER: 0046_payment_terms_and_treasury.sql.
--
-- 1. ACCOUNTS. Besides the automatic accounts of each branch (Cash = the drawer, Card, Qr), the administration creates
--    Bank accounts and Other accounts (company-wide: `branch_id` NULL, or of one branch), and each branch has one Safe
--    (caja fuerte), created by the administration or on the first POS withdrawal into it. Cash, Card, Qr and Safe stay
--    one per branch (partial unique index); Bank and Other may repeat.
--
-- 2. MOVEMENTS. New kinds, all append-only like the rest (a mistake is a Reversal, never an edit):
--    - CashCountDifference: the surplus (In) or shortage (Out) a cash session closed with, on the branch Cash account.
--    - CashWithdrawal / CashDeposit: money taken out of / put into the drawer at the POS outside a sale (an expense paid
--      from the drawer, change brought in...).
--    - Transfer: money moved between two accounts (drawer to safe, safe to bank...): an Out and an In sharing
--      `transfer_id`.
--    - ManualIn / ManualOut: money the administration records by hand (a bank deposit slip, an expense paid by the bank,
--      the initial change fund...).
--    A Reversal now flips the direction of what it reverses (an Out reversed is an In).
--
-- RLS: unchanged (org-scoped, FORCE, SELECT and INSERT only).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file (only possible while no new-kind row exists):
--   BEGIN;
--   DELETE FROM treasury_movements WHERE kind IN ('CashCountDifference','CashWithdrawal','CashDeposit','Transfer','ManualIn','ManualOut');
--   DELETE FROM treasury_accounts WHERE kind IN ('Safe','Bank','Other');
--   ALTER TABLE treasury_movements DROP COLUMN IF EXISTS transfer_id;
--   ALTER TABLE treasury_accounts DROP COLUMN IF EXISTS description;
--   ALTER TABLE treasury_accounts ALTER COLUMN branch_id SET NOT NULL;
--   COMMIT;

BEGIN;

-- ---- 1. accounts -----------------------------------------------------------------------

ALTER TABLE treasury_accounts ALTER COLUMN branch_id DROP NOT NULL;
ALTER TABLE treasury_accounts ADD COLUMN IF NOT EXISTS description text NULL;

ALTER TABLE treasury_accounts DROP CONSTRAINT IF EXISTS treasury_accounts_kind_check;
ALTER TABLE treasury_accounts DROP CONSTRAINT IF EXISTS treasury_accounts_kind_ck;
ALTER TABLE treasury_accounts ADD CONSTRAINT treasury_accounts_kind_ck
    CHECK (kind IN ('Cash', 'Card', 'Qr', 'Safe', 'Bank', 'Other'));

ALTER TABLE treasury_accounts DROP CONSTRAINT IF EXISTS treasury_accounts_branch_scope_ck;
ALTER TABLE treasury_accounts ADD CONSTRAINT treasury_accounts_branch_scope_ck
    CHECK (kind IN ('Bank', 'Other') OR branch_id IS NOT NULL);

ALTER TABLE treasury_accounts DROP CONSTRAINT IF EXISTS treasury_accounts_description_ck;
ALTER TABLE treasury_accounts ADD CONSTRAINT treasury_accounts_description_ck
    CHECK (description IS NULL OR char_length(description) <= 200);

ALTER TABLE treasury_accounts DROP CONSTRAINT IF EXISTS treasury_accounts_branch_kind_uk;
CREATE UNIQUE INDEX IF NOT EXISTS treasury_accounts_branch_kind_uk
    ON treasury_accounts (organization_id, branch_id, kind) WHERE kind IN ('Cash', 'Card', 'Qr', 'Safe');

-- ---- 2. movements ----------------------------------------------------------------------

ALTER TABLE treasury_movements ADD COLUMN IF NOT EXISTS transfer_id uuid NULL;

-- Widened only when it does not admit these kinds yet: re-running this file never narrows what a later migration widened.
ALTER TABLE treasury_movements DROP CONSTRAINT IF EXISTS treasury_movements_kind_check;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'treasury_movements_kind_ck'
                   AND pg_get_constraintdef(oid) LIKE '%CashCountDifference%') THEN
        ALTER TABLE treasury_movements DROP CONSTRAINT IF EXISTS treasury_movements_kind_ck;
        ALTER TABLE treasury_movements ADD CONSTRAINT treasury_movements_kind_ck
            CHECK (kind IN ('Sale', 'CustomerPayment', 'DeliveryPayment', 'Reversal', 'CashCountDifference', 'CashWithdrawal',
                            'CashDeposit', 'Transfer', 'ManualIn', 'ManualOut'));
    END IF;
END $$;

ALTER TABLE treasury_movements DROP CONSTRAINT IF EXISTS treasury_movements_transfer_ck;
ALTER TABLE treasury_movements ADD CONSTRAINT treasury_movements_transfer_ck
    CHECK (kind <> 'Transfer' OR transfer_id IS NOT NULL);

CREATE INDEX IF NOT EXISTS treasury_movements_transfer_idx
    ON treasury_movements (organization_id, transfer_id) WHERE transfer_id IS NOT NULL;

COMMIT;

-- treasury-account-types-and-voids: 0048_treasury_account_types_and_voids.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Treasury account types (an organization-owned catalog), account management (edit, activate/deactivate) and voided
-- or edited treasury movements.
-- APPLIED AFTER: 0047_treasury_accounts_and_cash_movements.sql.
--
-- 1. ACCOUNT TYPES (`treasury_account_types`): the organization's own catalog of kinds of money ("Efectivo", "Bancos",
--    "Tarjetas de crédito", "Billeteras virtuales"...), the same shape and rules as the other catalogs
--    (`supplier_categories`, 0030): name + key unique per organization, sort order, active/inactive, never deleted.
--    Every organization that already has treasury accounts gets the default types; an organization without types gets
--    them when its first account is created. Each account points at one type (`account_type_id`), so the owner sees the
--    company total split by type and by branch. The technical `kind` of an account stays: it is what the POS posts to
--    (the drawer = Cash, Card, Qr, and the branch Safe); the type is how the business groups its money.
--
-- 2. ACCOUNTS can be renamed, re-typed, described, and deactivated (`is_active`). UPDATE is granted on those columns
--    only.
--
-- 3. VOIDED MOVEMENTS (`treasury_movement_voids`): an administrator voids a movement (it stays, marked voided with who,
--    when and why, and stops counting in every balance) or edits it (the original is voided and points at its
--    replacement, which carries `corrects_movement_id`). Append-only like the movements: nothing is ever deleted or
--    rewritten, so the history and its audit stay complete.
--
-- RLS: org-scoped, FORCE, the symmetric tenant-isolation policy (NULLIF pooler-safety hardening from 0001).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   DROP TABLE IF EXISTS treasury_movement_voids;
--   ALTER TABLE treasury_movements DROP COLUMN IF EXISTS corrects_movement_id;
--   ALTER TABLE treasury_accounts DROP COLUMN IF EXISTS account_type_id, DROP COLUMN IF EXISTS is_active,
--       DROP COLUMN IF EXISTS updated_at;
--   DROP TABLE IF EXISTS treasury_account_types;
--   COMMIT;

BEGIN;

-- ---- 1. account types --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS treasury_account_types (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    key             text NOT NULL CHECK (btrim(key) <> ''),
    sort_order      integer NOT NULL DEFAULT 0,
    is_active       boolean NOT NULL DEFAULT true,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT treasury_account_types_org_scoped_uk UNIQUE (organization_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS treasury_account_types_org_name_uk
    ON treasury_account_types (organization_id, lower(btrim(name)));
CREATE UNIQUE INDEX IF NOT EXISTS treasury_account_types_org_key_uk
    ON treasury_account_types (organization_id, key);

ALTER TABLE treasury_account_types ENABLE ROW LEVEL SECURITY;
ALTER TABLE treasury_account_types FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON treasury_account_types FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON treasury_account_types TO app_runtime;
DROP POLICY IF EXISTS treasury_account_types_tenant_isolation ON treasury_account_types;
CREATE POLICY treasury_account_types_tenant_isolation ON treasury_account_types
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- The default types of every organization that already has treasury accounts (the others get them with their first
-- account). Same keys the API seeds.
INSERT INTO treasury_account_types (id, organization_id, name, key, sort_order)
SELECT gen_random_uuid(), owners.organization_id, defaults.name, defaults.key, defaults.sort_order
FROM (SELECT DISTINCT organization_id FROM treasury_accounts) AS owners
CROSS JOIN (VALUES
    ('Efectivo', 'efectivo', 10),
    ('Tarjetas', 'tarjetas', 20),
    ('Billeteras virtuales / QR', 'billeteras', 30),
    ('Bancos', 'bancos', 40),
    ('Otras', 'otras', 50)
) AS defaults (name, key, sort_order)
WHERE NOT EXISTS (SELECT 1 FROM treasury_account_types t WHERE t.organization_id = owners.organization_id);

-- ---- 2. accounts -------------------------------------------------------------------------

ALTER TABLE treasury_accounts ADD COLUMN IF NOT EXISTS account_type_id uuid NULL;
ALTER TABLE treasury_accounts ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true;
ALTER TABLE treasury_accounts ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'treasury_accounts_type_fk') THEN
        ALTER TABLE treasury_accounts ADD CONSTRAINT treasury_accounts_type_fk
            FOREIGN KEY (organization_id, account_type_id) REFERENCES treasury_account_types (organization_id, id);
    END IF;
END $$;

UPDATE treasury_accounts a
SET account_type_id = t.id
FROM treasury_account_types t
WHERE t.organization_id = a.organization_id
  AND a.account_type_id IS NULL
  AND t.key = CASE a.kind
      WHEN 'Cash' THEN 'efectivo' WHEN 'Safe' THEN 'efectivo' WHEN 'Card' THEN 'tarjetas'
      WHEN 'Qr' THEN 'billeteras' WHEN 'Bank' THEN 'bancos' ELSE 'otras' END;

GRANT UPDATE (name, description, account_type_id, is_active, updated_at) ON treasury_accounts TO app_runtime;

-- ---- 3. voided and edited movements ------------------------------------------------------

ALTER TABLE treasury_movements ADD COLUMN IF NOT EXISTS corrects_movement_id uuid NULL;

CREATE TABLE IF NOT EXISTS treasury_movement_voids (
    organization_id         uuid        NOT NULL,
    movement_id             uuid        NOT NULL,
    voided_at_utc           timestamptz NOT NULL DEFAULT now(),
    voided_by_user_id       uuid        NOT NULL,
    reason                  text        NOT NULL CHECK (btrim(reason) <> '' AND char_length(reason) <= 200),
    replacement_movement_id uuid        NULL,
    CONSTRAINT treasury_movement_voids_pk PRIMARY KEY (organization_id, movement_id),
    CONSTRAINT treasury_movement_voids_movement_fk
        FOREIGN KEY (organization_id, movement_id) REFERENCES treasury_movements (organization_id, id)
);

ALTER TABLE treasury_movement_voids ENABLE ROW LEVEL SECURITY;
ALTER TABLE treasury_movement_voids FORCE ROW LEVEL SECURITY;
REVOKE ALL ON treasury_movement_voids FROM PUBLIC;
GRANT SELECT, INSERT ON treasury_movement_voids TO app_runtime;   -- append-only: a void is never undone or rewritten
DROP POLICY IF EXISTS treasury_movement_voids_tenant_isolation ON treasury_movement_voids;
CREATE POLICY treasury_movement_voids_tenant_isolation ON treasury_movement_voids
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

COMMIT;

-- category-pos-rail-and-run-discard: 0049_category_pos_rail_and_run_discard.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Which categories the POS offers as filters, and discarding a delivery run that was planned wrong.
-- APPLIED AFTER: 0048_treasury_account_types_and_voids.sql.
--
-- 1. CATEGORIES ON THE POS. `categories.show_in_pos` says whether the POS category rail offers the category as a filter
--    (its products are still sold, scanned, searched and listed under "Todos" either way) and `pos_sort_order` the order
--    the rail shows it in (then by name). Every category is shown by default; "Embutidos" and "Achuras", which the POS
--    used to hide with a hard-coded list, start hidden so nothing changes for the cashier. The POS receives the
--    categories with its `price-lists` snapshot (a full snapshot on every sync) and keeps them locally.
--
-- 2. DISCARDING A PLANNED RUN. A delivery run still Planned (nothing dispatched: no remito numbered, no stock or money
--    moved) can be deleted; its stops go with it (ON DELETE CASCADE) and its orders are free again for another run. The
--    API refuses any other status and audits the deletion with the run's content. DELETE is granted for that.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   REVOKE DELETE ON delivery_runs FROM app_runtime;
--   ALTER TABLE categories DROP COLUMN IF EXISTS show_in_pos, DROP COLUMN IF EXISTS pos_sort_order;
--   COMMIT;

BEGIN;

-- The categories the POS hid by name until now (case and accents ignored) start hidden by the setting. Only when the
-- column is created: re-running this file never overwrites an administrator's later choice.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'public' AND table_name = 'categories' AND column_name = 'show_in_pos') THEN
        ALTER TABLE categories ADD COLUMN show_in_pos boolean NOT NULL DEFAULT true;
        UPDATE categories SET show_in_pos = false
        WHERE translate(lower(btrim(name)), 'áéíóú', 'aeiou') IN ('embutidos', 'achuras');
    END IF;
END $$;

ALTER TABLE categories ADD COLUMN IF NOT EXISTS pos_sort_order integer NOT NULL DEFAULT 0;

GRANT DELETE ON delivery_runs TO app_runtime;

COMMIT;

-- employees-and-payroll: 0050_employees_and_payroll.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Staff (personal) and the internal payroll (liquidación de sueldos) of each branch.
-- APPLIED AFTER: 0049_category_pos_rail_and_run_discard.sql.
--
-- SCOPE (PRD 9.19): the staff file (legajo, contact, position, branch, status), each employee's current account
-- (advances, purchases, deductions, salary owed and paid) and the INTERNAL payroll: what the owner pays each employee
-- for a period. It is NOT a legal payroll: no social security contributions, union dues or legal payslips (the
-- accountant keeps doing those; PRD 9.19 leaves them out until a legal and accounting analysis).
--
-- 1. POSITIONS (`employee_roles`, "puestos": carnicero, cajero, repartidor...): an organization catalog with the shape and
--    rules of the other catalogs (`supplier_categories`, 0030): name + key unique, order, active/inactive, never deleted.
--
-- 2. EMPLOYEES (`employees`): one per person, of one branch, with a file number (`file_number`, legajo) unique in the
--    organization, the agreed pay (`base_salary` per `pay_frequency`: Monthly, Biweekly, Weekly), and dates. An employee
--    who buys goods at the counter has a linked customer (`customer_id`): the POS sells to it on current account as to any
--    customer (stock, sale, its account), and the payroll deducts that debt from the salary. Never deleted: an employee
--    who leaves is deactivated (`is_active`, `termination_date`) so the history stays readable.
--
-- 3. THE EMPLOYEE'S CURRENT ACCOUNT: `current_account_movements` admits party_kind 'Employee' with its `employee_id`. It
--    reads like a supplier's: the balance is what the business OWES the employee. The salary of a period is an Invoice
--    (Credit, the business owes it), an advance a Payment made beforehand (Debit), the goods bought and other deductions an
--    Adjustment (Debit), and the salary paid a Payment (Debit). A negative balance is what the employee owes.
--
-- 4. PAYROLL (`payroll_runs`, `payslips`, `payslip_lines`): a run is one branch and one period, numbered per branch. It is
--    prepared as a Draft (one payslip per active employee, with their base salary, their pending advances and their
--    purchases to deduct; earnings and deductions can be added and the purchases discount percentage set), and then
--    Paid in one step: each payslip posts its salary, deductions and payment on the employee's account, settles the
--    purchases on the linked customer's account (what is deducted as a payment, the discount the owner grants as a credit
--    note) and takes the net pay out of the chosen treasury account. A Draft can be discarded; a Paid run is final.
--
-- 5. TREASURY: movement kinds `SalaryPayment` (net pay) and `EmployeeAdvance` (an advance handed out).
--
-- RLS: every table is org-scoped with FORCE ROW LEVEL SECURITY and the symmetric tenant-isolation policy (NULLIF
-- pooler-safety hardening from 0001).
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file (only while no employee movement exists):
--   BEGIN;
--   DROP TABLE IF EXISTS payslip_lines, payslips, payroll_runs;
--   ALTER TABLE current_account_movements DROP CONSTRAINT IF EXISTS current_account_movements_employee_fk,
--       DROP CONSTRAINT IF EXISTS current_account_movements_party_employee, DROP COLUMN IF EXISTS employee_id;
--   DROP TABLE IF EXISTS employees, employee_roles;
--   COMMIT;

BEGIN;

-- ---- 1. positions --------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS employee_roles (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            text NOT NULL CHECK (btrim(name) <> ''),
    key             text NOT NULL CHECK (btrim(key) <> ''),
    sort_order      integer NOT NULL DEFAULT 0,
    is_active       boolean NOT NULL DEFAULT true,
    created_at_utc  timestamptz NOT NULL DEFAULT now(),
    updated_at_utc  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT employee_roles_org_scoped_uk UNIQUE (organization_id, id)
);
CREATE UNIQUE INDEX IF NOT EXISTS employee_roles_org_name_uk ON employee_roles (organization_id, lower(btrim(name)));
CREATE UNIQUE INDEX IF NOT EXISTS employee_roles_org_key_uk ON employee_roles (organization_id, key);

ALTER TABLE employee_roles ENABLE ROW LEVEL SECURITY;
ALTER TABLE employee_roles FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON employee_roles FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON employee_roles TO app_runtime;
DROP POLICY IF EXISTS employee_roles_tenant_isolation ON employee_roles;
CREATE POLICY employee_roles_tenant_isolation ON employee_roles
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ---- 2. employees --------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS employees (
    organization_id  uuid          NOT NULL,
    id               uuid          NOT NULL,
    branch_id        uuid          NOT NULL,
    file_number      integer       NOT NULL CHECK (file_number > 0),
    first_name       text          NOT NULL CHECK (btrim(first_name) <> '' AND char_length(first_name) <= 100),
    last_name        text          NOT NULL CHECK (btrim(last_name) <> '' AND char_length(last_name) <= 100),
    document_number  text          NULL CHECK (document_number IS NULL OR document_number ~ '^[0-9]{6,11}$'),
    cuil             text          NULL CHECK (cuil IS NULL OR cuil ~ '^[0-9]{11}$'),
    role_id          uuid          NULL,
    phone            text          NULL CHECK (phone IS NULL OR char_length(phone) <= 40),
    email            text          NULL CHECK (email IS NULL OR char_length(email) <= 200),
    address          text          NULL CHECK (address IS NULL OR char_length(address) <= 200),
    hire_date        date          NULL,
    termination_date date          NULL,
    pay_frequency    text          NOT NULL DEFAULT 'Monthly' CHECK (pay_frequency IN ('Monthly', 'Biweekly', 'Weekly')),
    base_salary      numeric(18,2) NOT NULL DEFAULT 0 CHECK (base_salary >= 0),
    customer_id      uuid          NULL,
    notes            text          NULL CHECK (notes IS NULL OR char_length(notes) <= 1000),
    is_active        boolean       NOT NULL DEFAULT true,
    created_at_utc   timestamptz   NOT NULL DEFAULT now(),
    updated_at_utc   timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT employees_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT employees_id_uk UNIQUE (id),
    CONSTRAINT employees_file_number_uk UNIQUE (organization_id, file_number),
    CONSTRAINT employees_customer_uk UNIQUE (organization_id, customer_id),
    CONSTRAINT employees_branch_fk FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT employees_role_fk FOREIGN KEY (organization_id, role_id) REFERENCES employee_roles (organization_id, id),
    CONSTRAINT employees_customer_fk FOREIGN KEY (organization_id, customer_id) REFERENCES customers (organization_id, id),
    CONSTRAINT employees_dates_ck CHECK (termination_date IS NULL OR hire_date IS NULL OR termination_date >= hire_date)
);
CREATE INDEX IF NOT EXISTS employees_branch_idx ON employees (organization_id, branch_id, is_active);

ALTER TABLE employees ENABLE ROW LEVEL SECURITY;
ALTER TABLE employees FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON employees FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON employees TO app_runtime;   -- never deleted: deactivated
DROP POLICY IF EXISTS employees_tenant_isolation ON employees;
CREATE POLICY employees_tenant_isolation ON employees
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- ---- 3. the employee's current account ------------------------------------------------------

ALTER TABLE current_account_movements ADD COLUMN IF NOT EXISTS employee_id uuid NULL;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_party_kind_ck'
                   AND pg_get_constraintdef(oid) LIKE '%Employee%') THEN
        ALTER TABLE current_account_movements DROP CONSTRAINT IF EXISTS current_account_movements_party_kind_ck;
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_party_kind_ck
            CHECK (party_kind IN ('Supplier', 'Customer', 'Employee'));
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_party_employee') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_party_employee
            CHECK (party_kind <> 'Employee' OR employee_id = party_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'current_account_movements_employee_fk') THEN
        ALTER TABLE current_account_movements ADD CONSTRAINT current_account_movements_employee_fk
            FOREIGN KEY (organization_id, employee_id) REFERENCES employees (organization_id, id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS current_account_movements_employee_idx
    ON current_account_movements (organization_id, employee_id, occurred_on, created_at_utc) WHERE employee_id IS NOT NULL;

-- ---- 4. payroll ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS payroll_runs (
    organization_id    uuid        NOT NULL,
    id                 uuid        NOT NULL,
    branch_id          uuid        NOT NULL,
    run_number         integer     NOT NULL CHECK (run_number > 0),
    period_from        date        NOT NULL,
    period_to          date        NOT NULL,
    pay_frequency      text        NULL CHECK (pay_frequency IS NULL OR pay_frequency IN ('Monthly', 'Biweekly', 'Weekly')),
    status             text        NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft', 'Paid')),
    notes              text        NULL CHECK (notes IS NULL OR char_length(notes) <= 500),
    paid_on            date        NULL,
    paid_at_utc        timestamptz NULL,
    payment_account_id uuid        NULL,
    created_by_user_id uuid        NOT NULL,
    created_at_utc     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payroll_runs_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT payroll_runs_id_uk UNIQUE (id),
    CONSTRAINT payroll_runs_number_uk UNIQUE (organization_id, branch_id, run_number),
    CONSTRAINT payroll_runs_period_ck CHECK (period_to >= period_from),
    CONSTRAINT payroll_runs_paid_ck CHECK ((status = 'Paid') = (paid_at_utc IS NOT NULL)),
    CONSTRAINT payroll_runs_branch_fk FOREIGN KEY (organization_id, branch_id) REFERENCES branches (organization_id, id),
    CONSTRAINT payroll_runs_account_fk FOREIGN KEY (organization_id, payment_account_id) REFERENCES treasury_accounts (organization_id, id)
);

CREATE TABLE IF NOT EXISTS payslips (
    organization_id            uuid          NOT NULL,
    id                         uuid          NOT NULL,
    run_id                     uuid          NOT NULL,
    employee_id                uuid          NOT NULL,
    purchases_amount           numeric(18,2) NOT NULL DEFAULT 0 CHECK (purchases_amount >= 0),
    purchases_discount_percent numeric(5,2)  NOT NULL DEFAULT 0 CHECK (purchases_discount_percent BETWEEN 0 AND 100),
    CONSTRAINT payslips_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT payslips_id_uk UNIQUE (id),
    CONSTRAINT payslips_employee_uk UNIQUE (organization_id, run_id, employee_id),
    CONSTRAINT payslips_run_fk FOREIGN KEY (organization_id, run_id) REFERENCES payroll_runs (organization_id, id) ON DELETE CASCADE,
    CONSTRAINT payslips_employee_fk FOREIGN KEY (organization_id, employee_id) REFERENCES employees (organization_id, id)
);

CREATE TABLE IF NOT EXISTS payslip_lines (
    organization_id uuid          NOT NULL,
    payslip_id      uuid          NOT NULL,
    line_no         integer       NOT NULL CHECK (line_no > 0),
    kind            text          NOT NULL CHECK (kind IN ('Earning', 'Deduction')),
    source          text          NOT NULL CHECK (source IN ('BaseSalary', 'Advances', 'Manual')),
    concept         text          NOT NULL CHECK (btrim(concept) <> '' AND char_length(concept) <= 200),
    amount          numeric(18,2) NOT NULL CHECK (amount > 0),
    CONSTRAINT payslip_lines_pk PRIMARY KEY (organization_id, payslip_id, line_no),
    CONSTRAINT payslip_lines_payslip_fk FOREIGN KEY (organization_id, payslip_id) REFERENCES payslips (organization_id, id) ON DELETE CASCADE
);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['payroll_runs', 'payslips', 'payslip_lines'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('REVOKE ALL ON %I FROM PUBLIC', t);
        EXECUTE format('DROP POLICY IF EXISTS %I ON %I', t || '_tenant_isolation', t);
        EXECUTE format(
            'CREATE POLICY %I ON %I USING (organization_id = NULLIF(current_setting(''app.current_org_id'', true), '''')::uuid) '
            || 'WITH CHECK (organization_id = NULLIF(current_setting(''app.current_org_id'', true), '''')::uuid)',
            t || '_tenant_isolation', t);
    END LOOP;
END $$;
-- A Draft is edited (its payslips and lines replaced) and can be discarded; Paid is final.
GRANT SELECT, INSERT, UPDATE, DELETE ON payroll_runs, payslips, payslip_lines TO app_runtime;

-- ---- 5. treasury kinds -----------------------------------------------------------------------

-- Widened only when it does not admit these kinds yet: re-running this file never narrows what a later migration widened.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'treasury_movements_kind_ck'
                   AND pg_get_constraintdef(oid) LIKE '%SalaryPayment%') THEN
        ALTER TABLE treasury_movements DROP CONSTRAINT IF EXISTS treasury_movements_kind_ck;
        ALTER TABLE treasury_movements ADD CONSTRAINT treasury_movements_kind_ck
            CHECK (kind IN ('Sale', 'CustomerPayment', 'DeliveryPayment', 'Reversal', 'CashCountDifference', 'CashWithdrawal',
                            'CashDeposit', 'Transfer', 'ManualIn', 'ManualOut', 'SalaryPayment', 'EmployeeAdvance'));
    END IF;
END $$;

COMMIT;

-- treasury-recurrences: 0051_treasury_recurrences.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, transactional, idempotent, forward-only migration.
--
-- Recurring treasury movements: fixed expenses (electricity, gas, internet, phone, rent...) and recurring income, recorded
-- automatically on their dates.
-- APPLIED AFTER: 0050_employees_and_payroll.sql.
--
-- A recurrence (`treasury_recurrences`) says: money In or Out of an account, an amount and a concept, every N weeks,
-- months or years from a start date (the weekday, day of the month or date of the year come from it; a day the month
-- does not have becomes its last day), and when it ends: never, on a date, or after a number of occurrences. It can be
-- paused. Editing its amount, concept or account changes what comes next; what was already recorded stays. Only dates
-- from `generate_from` on are recorded: the creation day unless the past dates since the start were asked for, and the
-- day it is resumed after a pause (the dates while paused are not recorded).
--
-- Its occurrences are ordinary treasury movements (ManualIn / ManualOut) that point at it (`recurrence_id`) and carry the
-- source `TreasuryRecurrence` with an id derived from the recurrence and the date, so each date is recorded ONCE no
-- matter how often the generation runs (when the treasury is opened, and by the daily job). Like any manual movement an
-- occurrence can be edited or voided; a voided one is not recorded again.
--
-- RLS: org-scoped with FORCE ROW LEVEL SECURITY and the symmetric tenant-isolation policy (NULLIF pooler-safety
-- hardening from 0001). Recurrences are deactivated, never deleted.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   BEGIN;
--   ALTER TABLE treasury_movements DROP COLUMN IF EXISTS recurrence_id;
--   DROP TABLE IF EXISTS treasury_recurrences;
--   COMMIT;

BEGIN;

CREATE TABLE IF NOT EXISTS treasury_recurrences (
    organization_id      uuid          NOT NULL,
    id                   uuid          NOT NULL,
    account_id           uuid          NOT NULL,
    direction            text          NOT NULL CHECK (direction IN ('In', 'Out')),
    amount               numeric(18,2) NOT NULL CHECK (amount > 0),
    concept              text          NOT NULL CHECK (btrim(concept) <> '' AND char_length(concept) <= 200),
    document_reference   text          NULL CHECK (document_reference IS NULL OR char_length(document_reference) <= 60),
    frequency            text          NOT NULL CHECK (frequency IN ('Weekly', 'Monthly', 'Yearly')),
    interval_count       integer       NOT NULL DEFAULT 1 CHECK (interval_count BETWEEN 1 AND 24),
    start_date           date          NOT NULL,
    end_mode             text          NOT NULL DEFAULT 'Never' CHECK (end_mode IN ('Never', 'OnDate', 'AfterCount')),
    end_date             date          NULL,
    max_occurrences      integer       NULL CHECK (max_occurrences IS NULL OR max_occurrences BETWEEN 1 AND 1000),
    is_active            boolean       NOT NULL DEFAULT true,
    generate_from        date          NOT NULL,
    created_by_user_id   uuid          NOT NULL,
    created_at_utc       timestamptz   NOT NULL DEFAULT now(),
    updated_at_utc       timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT treasury_recurrences_pk PRIMARY KEY (organization_id, id),
    CONSTRAINT treasury_recurrences_account_fk FOREIGN KEY (organization_id, account_id) REFERENCES treasury_accounts (organization_id, id),
    CONSTRAINT treasury_recurrences_end_ck CHECK (
        (end_mode = 'Never' AND end_date IS NULL AND max_occurrences IS NULL)
        OR (end_mode = 'OnDate' AND end_date IS NOT NULL AND end_date >= start_date AND max_occurrences IS NULL)
        OR (end_mode = 'AfterCount' AND max_occurrences IS NOT NULL AND end_date IS NULL))
);

ALTER TABLE treasury_recurrences ADD COLUMN IF NOT EXISTS generate_from date NOT NULL DEFAULT CURRENT_DATE;

ALTER TABLE treasury_recurrences ENABLE ROW LEVEL SECURITY;
ALTER TABLE treasury_recurrences FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON treasury_recurrences FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON treasury_recurrences TO app_runtime;
DROP POLICY IF EXISTS treasury_recurrences_tenant_isolation ON treasury_recurrences;
CREATE POLICY treasury_recurrences_tenant_isolation ON treasury_recurrences
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

ALTER TABLE treasury_movements ADD COLUMN IF NOT EXISTS recurrence_id uuid NULL;
CREATE INDEX IF NOT EXISTS treasury_movements_recurrence_idx
    ON treasury_movements (organization_id, recurrence_id, business_date) WHERE recurrence_id IS NOT NULL;

COMMIT;

-- organization-account-standing: 0052_organization_account_standing.sql, appended verbatim per the hand-kept mirror convention.

-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- organization-account-standing (T2): the inputs of `AccountStandingRules.Evaluate` (Commerce.Domain). The standing
-- itself (Active / Overdue / Suspended) is DERIVED from these on every read and never stored, so it changes on its
-- own when the business day changes.
--   - `billing_due_on`: the date the payment to the platform is due. NULL = billing not tracked, the organization
--     stays Active; every existing organization starts this way, so nothing changes for it.
--   - `billing_grace_days`: days after the due date that still work (0-90, default 30). Enforced here as well as in
--     the domain, so bad data can never reach a rule that rejects it.
--   - `suspended_at`: set when a system administrator suspends the organization by hand; wins over any date.
--
-- APPLIED AFTER: 0051_treasury_recurrences.sql.
--
-- Columns on `organizations`, like 0015 and 0035: its tenant-isolation RLS policy and the `app_runtime` grants
-- already cover new columns. Only the system-administrator endpoints write them.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE organizations DROP CONSTRAINT organizations_billing_grace_days_ck;
--   ALTER TABLE organizations DROP COLUMN suspended_at;
--   ALTER TABLE organizations DROP COLUMN billing_grace_days;
--   ALTER TABLE organizations DROP COLUMN billing_due_on;

BEGIN;

ALTER TABLE organizations
    ADD COLUMN IF NOT EXISTS billing_due_on date NULL,
    ADD COLUMN IF NOT EXISTS billing_grace_days integer NOT NULL DEFAULT 30,
    ADD COLUMN IF NOT EXISTS suspended_at timestamptz NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'organizations_billing_grace_days_ck'
    ) THEN
        ALTER TABLE organizations
            ADD CONSTRAINT organizations_billing_grace_days_ck
            CHECK (billing_grace_days BETWEEN 0 AND 90);
    END IF;
END $$;

COMMIT;
