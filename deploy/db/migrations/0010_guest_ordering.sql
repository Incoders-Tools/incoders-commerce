-- Repo-owned, idempotent per-environment migration.
--
-- commerce-guest-ordering design.md "Verification state shape": a persisted
-- `guest_order_verifications` row, mirroring the `password_reset_tokens`
-- (0005) idiom for idiom — a 6-digit code stored ONLY as a SHA-256 hex hash
-- (code_hash), 10-minute expiry, attempt-bounded (CHECK attempt_count <= 5,
-- "the 5th attempt burns the row"), and a confirmed row becomes a
-- single-use ticket consumed immediately before CloudOrderStore.Submit so
-- one confirmation admits exactly one order (confirmed_at / consumed_at /
-- consumed_order_id).
--
-- RLS is asymmetric, the password_reset_tokens/device_credentials
-- precedent verbatim for SELECT: confirm must resolve the verification row
-- by id BEFORE any tenant scope is known, so guest_order_verifications_lookup
-- is unscoped. UNLIKE password_reset_tokens (a single terminal
-- consumed_at transition), a verification row passes through MULTIPLE
-- pre-terminal states before any org scope is available to the caller
-- (wrong-code attempt increments, then confirm) — the UPDATE policy is
-- therefore intentionally permissive at the RLS layer
-- (guest_order_verifications_update: USING(true) WITH CHECK(true)); the
-- attempt_count <= 5 CHECK constraint and the SHA-256 code-hash lookup
-- (10^6 code space) bound the abuse surface at the domain/DB-constraint
-- layer, not RLS. This mirrors the accepted asymmetric-RLS shape already
-- used by device_credentials' unscoped-revoke policy — an unscoped
-- capability is deliberate here, not an oversight.
--
-- NEVER regress the `NULLIF(current_setting('app.current_org_id',true),'')::uuid`
-- pooler-safety hardening from 0001. No DELETE grant to `app_runtime` — the
-- `customers`/`platform_admins`/`password_reset_tokens` precedent: rows are
-- purged only by an owner-run housekeeping job (deferred, follow-up), not by
-- the application role.

CREATE TABLE IF NOT EXISTS guest_order_verifications (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    document_id       text NOT NULL,
    contact_channel   text NOT NULL CHECK (contact_channel IN ('Email')), -- widened, not reshaped, for SMS later
    contact_address   text NOT NULL,
    code_hash         text NOT NULL,          -- SHA-256 hex. The code itself is never stored.
    attempt_count     integer NOT NULL DEFAULT 0 CHECK (attempt_count <= 5),
    requested_at      timestamptz NOT NULL DEFAULT now(),
    expires_at        timestamptz NOT NULL,   -- issued + 10 minutes
    confirmed_at      timestamptz NULL,       -- ticket becomes usable
    consumed_at       timestamptz NULL,       -- exactly ONE order per confirmation
    consumed_order_id uuid NULL               -- audit trail for the admitted order
);
CREATE INDEX IF NOT EXISTS guest_order_verifications_contact_idx
    ON guest_order_verifications (organization_id, contact_address, requested_at DESC);

ALTER TABLE guest_order_verifications ENABLE ROW LEVEL SECURITY;
ALTER TABLE guest_order_verifications FORCE ROW LEVEL SECURITY;
REVOKE ALL ON guest_order_verifications FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON guest_order_verifications TO app_runtime; -- no DELETE

-- Unscoped: confirm resolves the row by id BEFORE any tenant scope exists,
-- the password_reset_tokens_lookup precedent verbatim.
DROP POLICY IF EXISTS guest_order_verifications_lookup ON guest_order_verifications;
CREATE POLICY guest_order_verifications_lookup ON guest_order_verifications FOR SELECT USING (true);

DROP POLICY IF EXISTS guest_order_verifications_issue ON guest_order_verifications;
CREATE POLICY guest_order_verifications_issue ON guest_order_verifications
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Intentionally permissive (see header note): attempt-increment, confirm,
-- and consume are three DIFFERENT pre-terminal update shapes, all reachable
-- before any org scope is known to the caller — unlike password_reset_tokens'
-- single WITH CHECK(consumed_at IS NOT NULL) terminal transition.
DROP POLICY IF EXISTS guest_order_verifications_update ON guest_order_verifications;
CREATE POLICY guest_order_verifications_update ON guest_order_verifications
    FOR UPDATE USING (true) WITH CHECK (true);

-- Inverse (rollback), shipped as comments — NOT executed by this file:
--   DROP TABLE guest_order_verifications;
-- Lossy only for in-flight verifications (minutes of state, re-requestable);
-- no order data is persisted anywhere today (design.md "Migration / Rollout").
