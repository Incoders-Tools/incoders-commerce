-- Creates the dedicated integration-test database and gives it the same RLS
-- policy set as the developer database.
--
-- WHY THIS FILE EXISTS
-- --------------------
-- `tests/Commerce.Integration` resets roughly three dozen fixtures with
-- `TRUNCATE TABLE ... users, branches, organizations CASCADE`. That is correct
-- for a throwaway database and destructive for `commerce_dev`, which is the
-- database `deploy/dev/run-all.ps1` points the running app at. The suite
-- therefore targets `commerce_test` (`PostgresTestFixture.Database`), and this
-- file is what makes that database exist and behave.
--
-- ROLES vs GRANTS
-- ---------------
-- `app_runtime` and `platform_readonly` are CLUSTER-level roles: init-rls.sql
-- already created them while initializing `commerce_dev`, and re-running it
-- here is a no-op for them (it guards every CREATE ROLE). The GRANTs, tables,
-- and RLS policies are per-DATABASE, which is exactly why init-rls.sql has to
-- run a second time, connected to `commerce_test`.
--
-- WHERE IT RUNS
-- -------------
--   * fresh container: mounted into /docker-entrypoint-initdb.d by
--     deploy/dev/compose.yaml, after init-rls.sql (alphabetical order).
--   * existing container (no `docker compose down` needed — there is no named
--     volume, so a down would destroy the developer's data):
--       docker compose -f deploy/dev/compose.yaml exec -T postgres \
--         psql -v ON_ERROR_STOP=1 -U commerce_owner -d commerce_dev \
--         < deploy/dev/db/init-test-db.sql
--     (`\ir` below resolves init-rls.sql relative to THIS file when the file is
--     passed with -f; when piped on stdin psql resolves it against the current
--     directory, so prefer -f where the path is available.)
--   * CI: .github/workflows/release.yml's `build` job, which is the only job
--     that runs `dotnet test`.
--
-- Idempotent: safe to run against a cluster that already has `commerce_test`.

SELECT 'CREATE DATABASE commerce_test OWNER commerce_owner'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'commerce_test')\gexec

\connect commerce_test

\ir init-rls.sql
