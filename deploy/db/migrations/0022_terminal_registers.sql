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
--   2. release the installation's live register in any OTHER branch or
--      organization (re-pairing to another branch frees the old slot);
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
--   DROP FUNCTION IF EXISTS terminal_registers_assign(uuid, uuid, uuid);
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

CREATE OR REPLACE FUNCTION terminal_registers_assign(p_organization_id uuid, p_branch_id uuid, p_installation_id uuid)
RETURNS smallint AS $$
DECLARE
    v_number   smallint;
    v_released timestamptz;
    v_exists   boolean;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(p_installation_id::text, 2));
    PERFORM pg_advisory_xact_lock(hashtextextended(p_branch_id::text, 1));

    SELECT register_number, released_at INTO v_number, v_released
      FROM terminal_registers
     WHERE organization_id = p_organization_id
       AND branch_id = p_branch_id
       AND installation_id = p_installation_id;
    v_exists := FOUND;

    UPDATE terminal_registers
       SET released_at = now()
     WHERE installation_id = p_installation_id
       AND released_at IS NULL
       AND NOT (organization_id = p_organization_id AND branch_id = p_branch_id);

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

REVOKE ALL ON FUNCTION terminal_registers_assign(uuid, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION terminal_registers_assign(uuid, uuid, uuid) TO app_runtime;

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
