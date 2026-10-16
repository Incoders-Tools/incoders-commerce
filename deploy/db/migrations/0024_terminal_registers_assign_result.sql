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
