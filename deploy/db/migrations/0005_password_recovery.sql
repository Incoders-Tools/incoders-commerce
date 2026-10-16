-- Repo-owned, idempotent per-environment migration.
--
-- Adds `password_reset_tokens` (commerce-password-recovery design.md
-- "Interfaces / Contracts") plus `users.session_version`. Append-only:
-- 0001-0004 are NOT modified.
--
-- token_hash is sha256(token) hex. The token is 32 random bytes, returned to
-- the caller exactly once by the reset-request flow and NEVER stored. Plain
-- SHA-256 is correct here and is NOT an oversight (0004's exact argument):
-- the input is 256 bits of uniform randomness, so a slow KDF would add
-- per-request latency for zero security gain.
--
-- RLS shape is ASYMMETRIC, the `device_credentials` precedent verbatim:
-- confirm must resolve the token row BEFORE any tenant scope is known.
CREATE TABLE IF NOT EXISTS password_reset_tokens (
    token_hash      text PRIMARY KEY,
    user_id         uuid NOT NULL,          -- no FK: matches users' existing no-FK status
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

-- Asymmetric, the device_credentials precedent: confirm resolves the token
-- BEFORE any tenant scope exists.
DROP POLICY IF EXISTS password_reset_tokens_lookup ON password_reset_tokens;
CREATE POLICY password_reset_tokens_lookup ON password_reset_tokens FOR SELECT USING (true);

DROP POLICY IF EXISTS password_reset_tokens_issue ON password_reset_tokens;
CREATE POLICY password_reset_tokens_issue ON password_reset_tokens
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Unscoped UPDATE is permitted ONLY when the resulting row is consumed, making
-- an unscoped un-consume or field rewrite structurally unrepresentable.
DROP POLICY IF EXISTS password_reset_tokens_consume ON password_reset_tokens;
CREATE POLICY password_reset_tokens_consume ON password_reset_tokens
    FOR UPDATE USING (true) WITH CHECK (consumed_at IS NOT NULL);

DROP POLICY IF EXISTS password_reset_tokens_purge ON password_reset_tokens;
CREATE POLICY password_reset_tokens_purge ON password_reset_tokens
    FOR DELETE USING (expires_at < now() - interval '7 days');

ALTER TABLE users ADD COLUMN IF NOT EXISTS session_version integer NOT NULL DEFAULT 0;
