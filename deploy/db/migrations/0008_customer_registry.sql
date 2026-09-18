-- Repo-owned, idempotent per-environment migration.
--
-- Adds `customers` and `customer_ordering_access` — the `Customer` commercial
-- party aggregate (commerce-customer-identity proposal.md / design.md
-- "Interfaces / Contracts"). Append-only: 0001-0007 are NOT modified.
--
-- VERIFIED DEVIATION FROM THE PROPOSAL (design.md "Verified deviation 1"):
-- there is NO `orders` table anywhere in this repo — orders live in
-- `CloudOrderStore`'s in-memory `Dictionary<Guid, Order>` and vanish on
-- restart. The proposal's "orders.customer_id becomes NOT NULL and strictly
-- FK-enforced; delete pre-change order rows" step is THEREFORE NOT APPLICABLE
-- and is DELIBERATELY OMITTED here: there is no column to alter, no
-- constraint to add, and no row to delete. Shipping an `ALTER TABLE orders`
-- statement against a nonexistent table would abort this entire migration on
-- its first statement. The underlying invariant ("an order cannot be accepted
-- for a CustomerId with no matching customer row in the caller's
-- organization") is enforced in `CloudOrderSubmissionService.SubmitAsync`
-- instead of by a database constraint — see design.md's Architecture
-- Decisions table.
--
-- `customers` follows 0003's/0004's exact symmetric tenant-isolation policy
-- shape, including the `NULLIF(..., '')::uuid` pooler-safety hardening from
-- 0001. NEVER regress that fix.
--
-- `customer_ordering_access` follows the `device_credentials` (0004)
-- precedent exactly: `credential_hash text PRIMARY KEY` =
-- sha256(credential.ToString()) hex — the input is a 122-bit uniformly random
-- Guid, so plain SHA-256 is correct and a slow KDF would add per-order-submit
-- latency for zero security gain. RLS is ASYMMETRIC, the same class of
-- problem as `device_credentials_lookup`: the resolver looks up by hash
-- before any tenant scope is meaningfully comparable, and the org comparison
-- happens in `CustomerCatalogAccessService.Evaluate` against the row's own
-- `organization_id` — a cross-org credential denies with the SAME reason
-- string as an unknown one, so a probe cannot distinguish the two. The
-- revoke policy makes an unscoped un-revoke structurally unrepresentable,
-- exactly like `device_credentials_revoke`.
--
-- `users.customer_id` is a nullable FK. `users_customer_has_no_roles` is the
-- defense-in-depth database half of the "denied by construction, not
-- convention" guard — the in-memory half is
-- `UserAccount.EffectivePermissions` short-circuiting to `Permission.None`
-- whenever `CustomerId` is set, regardless of `Roles`.

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
    neighborhood        text NULL,          -- optional: real Argentine addressing
    locality            text NULL,
    province            text NULL,
    postal_code         text NULL,
    delivery_notes      text NULL,
    discount_percentage numeric(5,2) NULL,  -- ADR-010 seed; unused until Phase C
    payment_terms       text NULL,          -- free text until Phase C/E
    notes               text NULL,          -- staff-only
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
    credential_hash text PRIMARY KEY,       -- sha256(credential.ToString()) hex, the 0004 precedent
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

-- A customer login can never hold a staff role, enforced by the DATABASE as
-- well as by UserAccount.EffectivePermissions. Every existing row has
-- customer_id IS NULL, so validation against existing data passes trivially
-- (no NOT VALID/VALIDATE split needed).
ALTER TABLE users DROP CONSTRAINT IF EXISTS users_customer_has_no_roles;
ALTER TABLE users ADD CONSTRAINT users_customer_has_no_roles
    CHECK (customer_id IS NULL OR roles = '[]'::jsonb);

ALTER TABLE customers                ENABLE ROW LEVEL SECURITY;
ALTER TABLE customers                FORCE  ROW LEVEL SECURITY;
ALTER TABLE customer_ordering_access ENABLE ROW LEVEL SECURITY;
ALTER TABLE customer_ordering_access FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON customers, customer_ordering_access FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON customers                TO app_runtime;  -- no DELETE
GRANT SELECT, INSERT, UPDATE ON customer_ordering_access TO app_runtime;  -- no DELETE

-- Symmetric, identical in shape to branches_tenant_isolation, including the
-- NULLIF(..., '')::uuid pooler-safety hardening from 0001. NEVER regress it.
DROP POLICY IF EXISTS customers_tenant_isolation ON customers;
CREATE POLICY customers_tenant_isolation ON customers
    USING      (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Asymmetric, the device_credentials precedent: lookup is by credential hash;
-- the organization comparison is made against the ROW's own organization_id in
-- CustomerCatalogAccessService.Evaluate, so a cross-org credential denies with
-- the same "not-found" reason as an unknown one.
DROP POLICY IF EXISTS customer_ordering_access_lookup ON customer_ordering_access;
CREATE POLICY customer_ordering_access_lookup ON customer_ordering_access
    FOR SELECT USING (true);
DROP POLICY IF EXISTS customer_ordering_access_issue ON customer_ordering_access;
CREATE POLICY customer_ordering_access_issue ON customer_ordering_access
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);
-- Unscoped UPDATE permitted ONLY when the result is revoked: an unscoped
-- un-revoke is unrepresentable, not merely untested.
DROP POLICY IF EXISTS customer_ordering_access_revoke ON customer_ordering_access;
CREATE POLICY customer_ordering_access_revoke ON customer_ordering_access
    FOR UPDATE USING (true) WITH CHECK (NOT is_enabled);

-- Inverse (rollback), shipped as comments — NOT executed by this file:
--   ALTER TABLE users DROP CONSTRAINT users_customer_has_no_roles;
--   ALTER TABLE users DROP COLUMN customer_id;
--   DROP TABLE customer_ordering_access;
--   DROP TABLE customers;
-- NOTE: rolling back re-opens the authorization defect this change fixes.
-- Prefer forward-fix. If only the users constraint/column is a problem, drop
-- it alone and keep the aggregate.
