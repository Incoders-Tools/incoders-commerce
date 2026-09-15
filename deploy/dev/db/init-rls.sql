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
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_runtime') THEN
        CREATE ROLE app_runtime NOLOGIN;
    END IF;
END
$$;

REVOKE ALL ON sync_inbox FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON sync_inbox TO app_runtime;

-- Tenant isolation policy: every row read/write is scoped to the
-- authenticated claim's organization, never a caller-submitted value.
CREATE POLICY sync_inbox_tenant_isolation ON sync_inbox
    USING (organization_id = current_setting('app.current_org_id', true)::uuid)
    WITH CHECK (organization_id = current_setting('app.current_org_id', true)::uuid);
