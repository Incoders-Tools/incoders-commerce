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
-- different keys), and it is released at COMMIT/ROLLBACK, so the next creator
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
