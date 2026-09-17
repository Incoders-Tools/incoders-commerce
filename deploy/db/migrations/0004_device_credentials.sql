-- Repo-owned, idempotent per-environment migration.
--
-- Adds `device_credentials` — the durable record of a POS terminal's paired
-- identity (commerce-pos-installation-identity design.md "Credential shape").
-- Append-only: 0001-0003 are NOT modified.
--
-- token_hash is sha256(secret) hex. The secret is 32 random bytes, returned
-- to the terminal exactly once by POST /device/pair and NEVER stored. Plain
-- SHA-256 is correct here and is NOT an oversight: the input is a 256-bit
-- uniformly random secret, so a slow KDF (as users.password_hash correctly
-- uses for LOW-entropy passwords) would add per-request latency to every
-- sync for zero security gain.
--
-- This row IS the installation registration: InstallationIdentityService and
-- Commerce.Domain.Tenancy.Installation are deleted; every field they modelled
-- (installation_id, branch_id, replaces_installation_id/replaces_credential_id,
-- revoked) now lives here, durable across process restarts.
--
-- RLS shape is ASYMMETRIC, exactly the `user_directory` precedent: credential
-- verification must resolve the row BEFORE any tenant scope is known.
CREATE TABLE IF NOT EXISTS device_credentials (
    token_hash             text PRIMARY KEY,
    id                     uuid NOT NULL UNIQUE,
    organization_id        uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    branch_id              uuid NOT NULL REFERENCES branches (id) ON DELETE CASCADE,
    installation_id        uuid NOT NULL,
    issued_to_user_id      uuid NOT NULL,          -- no FK: matches users' existing no-FK status
    replaces_credential_id uuid NULL,               -- hardware-replacement lineage
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

-- Asymmetric, same class of problem as user_directory: verification resolves
-- the credential BEFORE any tenant scope exists, so the org id cannot be
-- known yet.
DROP POLICY IF EXISTS device_credentials_lookup ON device_credentials;
CREATE POLICY device_credentials_lookup ON device_credentials
    FOR SELECT USING (true);

DROP POLICY IF EXISTS device_credentials_issue ON device_credentials;
CREATE POLICY device_credentials_issue ON device_credentials
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Unscoped UPDATE is permitted ONLY when the resulting row is revoked. This
-- is exactly what cross-org re-pairing needs (revoke the org-A row while
-- scoped to org B) and makes an unscoped un-revoke or field rewrite
-- structurally unrepresentable, not just untested.
DROP POLICY IF EXISTS device_credentials_revoke ON device_credentials;
CREATE POLICY device_credentials_revoke ON device_credentials
    FOR UPDATE USING (true) WITH CHECK (is_revoked);
