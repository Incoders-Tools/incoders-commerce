-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- commerce-organization-persistence, T5 ("Organization Branding Fields").
-- Adds optional web branding to `organizations`: `logo_url` (an absolute
-- http/https URL) and `primary_color` (a `#rrggbb` hex string). Minimal
-- scope by explicit user decision (2026-09-25, "lo mas simple posible, a
-- futuro ampliamos") — no file upload, and date format/geolocation/usage
-- plan are explicitly NOT part of this change.
--
-- APPLIED AFTER: 0014_rate_component_tenancy.sql. Additive only: no
-- existing column, row, policy or grant of `organizations` (0003) changes.
-- `app_runtime`'s existing `GRANT SELECT, INSERT, UPDATE ON organizations`
-- already covers these two new columns — Postgres table-level grants apply
-- to every column, so no new GRANT is needed here.
--
-- Both new columns are validated again at the API boundary
-- (`Endpoints/Account.cs`); the CHECK constraints below are defence in
-- depth, not the primary control, matching this repo's existing pattern of
-- enforcing invariants at more than one layer.
--
-- INVERSE (rollback), shipped as a comment — NOT executed by this file:
--   ALTER TABLE organizations DROP CONSTRAINT organizations_logo_url_length_ck;
--   ALTER TABLE organizations DROP CONSTRAINT organizations_primary_color_format_ck;
--   ALTER TABLE organizations DROP COLUMN logo_url;
--   ALTER TABLE organizations DROP COLUMN primary_color;
-- Rolling back is lossless only while no row carries a value in either
-- column.

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
