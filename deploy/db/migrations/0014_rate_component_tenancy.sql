-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- commerce-price-composition, review round 1 (findings
-- R1-latent-cross-tenant-price-list-id and
-- R3-case-insensitive-dup-read-failure). Closes two gaps left by `0013`,
-- which is committed and forward-only and is therefore NOT edited.
--
-- APPLIED AFTER: 0013_rate_components.sql. Also touches `price_lists` from
-- 0009, additively: it gains one redundant unique key so it can be the
-- target of a tenant-composite reference. No column, row, policy or grant of
-- 0009 changes.
--
-- INVERSE (rollback), shipped as comments — NOT executed by this file:
--   ALTER TABLE rate_components     DROP CONSTRAINT rate_components_set_org_fk;
--   ALTER TABLE rate_component_sets DROP CONSTRAINT rate_component_sets_price_list_org_fk;
--   ALTER TABLE rate_components     ADD  CONSTRAINT rate_components_set_id_fkey
--       FOREIGN KEY (set_id) REFERENCES rate_component_sets (id) ON DELETE CASCADE;
--   ALTER TABLE rate_component_sets ADD  CONSTRAINT rate_component_sets_price_list_id_fkey
--       FOREIGN KEY (price_list_id) REFERENCES price_lists (id) ON DELETE CASCADE;
--   DROP INDEX rate_components_one_code_per_set_ci;
--   ALTER TABLE rate_components ADD CONSTRAINT rate_components_one_code_per_set UNIQUE (set_id, code);
--   DROP INDEX rate_component_sets_list_day_uk;
--   CREATE UNIQUE INDEX rate_component_sets_list_day_uk
--       ON rate_component_sets (price_list_id, effective_from) WHERE price_list_id IS NOT NULL;
--   ALTER TABLE rate_component_sets DROP CONSTRAINT rate_component_sets_org_scoped_uk;
--   ALTER TABLE price_lists         DROP CONSTRAINT price_lists_org_scoped_uk;
-- Rolling back reopens both gaps. It is lossless only while no row exists
-- that the stricter constraints would have refused — which, after this
-- migration, is every row.

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
