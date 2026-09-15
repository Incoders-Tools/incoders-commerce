# Deploy notes

## Unit 2 — Transaction-pooler proof-of-concept outcome

**Result: PASS (for the tested local approximation) — transaction-mode pooling accepted, with a documented gap.**

`design.md`'s "Pooling + tenant scope" decision requires a runnable PoC before
trusting `set_config('app.current_org_id', v, true)` (`SET LOCAL` semantics)
under a transaction-mode connection pooler, rather than assuming it from
reasoning alone. A real Supabase project is manual, out-of-repo provisioning
and was not available in this environment, so this PoC ran against the
**strongest available local approximation**: PgBouncer (`pool_mode =
transaction`) in front of the same Postgres used by `deploy/dev/compose.yaml`
(see the `pgbouncer` service and `pgbouncer-userlist.txt` added in Unit 2).

### What was proven

- `tests/Commerce.Integration/PoolerScopingTests.cs`
  (`ConcurrentPooledTransactions_NeverObserveAnotherOrganizationsRows`): 12
  organizations concurrently applying and repeatedly reading through the same
  pooled endpoint (port 6543), each opening its own explicit
  `PostgresCloudInboxStore` transaction, observed **zero cross-organization
  rows** across all runs (verified stable over multiple consecutive full
  suite runs).
- `tests/Commerce.Integration/PostgresCloudInboxStoreTests.cs`: deny/allow
  parity between `PostgresCloudInboxStore` (real Npgsql adapter) and the
  in-memory `CloudInboxStore` test double, against the SAME live schema, both
  directly (port 5432) and through the pooler.
- A genuine RLS hardening bug was found and fixed during this PoC: PostgreSQL
  does not revert a custom placeholder GUC back to "unset"/`NULL` after a
  transaction-local `set_config(..., true)` commits — it reverts to `''`
  (empty string) for the rest of that session/pooled-connection lifetime. The
  original policy (`organization_id = current_setting(...)::uuid`) would
  raise a Postgres cast error instead of a clean zero-row deny if any query
  ever ran on a reused connection without re-applying scope first. The policy
  in `deploy/dev/db/init-rls.sql` now uses
  `NULLIF(current_setting(...), '')::uuid` so an unscoped query fails closed
  (zero rows) instead of erroring. `PostgresCloudInboxStore` always sets
  scope as the first statement of every transaction, so this path is not hit
  in production, but the policy is hardened regardless of caller discipline.
- A first PgBouncer configuration attempt hardcoded a single backend role
  (`commerce_owner`, a Postgres **superuser** in the stock `postgres` Docker
  image) for every client connection. That silently bypassed RLS for
  everyone and looked exactly like a real cross-tenant leak in the test
  output until diagnosed. The fix (`deploy/dev/pgbouncer-userlist.txt` +
  omitting a fixed `DATABASES_USER`) makes PgBouncer pass each client's own
  authenticated role through to Postgres, matching Supabase's real per-role
  Supavisor connection shape. This is recorded here because it is exactly
  the class of pooler-configuration mistake this PoC exists to catch.

### What remains unproven

Supavisor (Supabase's actual transaction-mode pooler) is a different
implementation from PgBouncer. TLS termination behavior, connection draining
under real production load, and Supavisor-specific release-back-to-pool
timing were **not** exercised here and remain unproven pending a real
Supabase staging project. Per design.md's explicit fallback: if a future run
against the real Supavisor pooler reveals leakage or unreliable `SET LOCAL`
behavior, fall back to session-mode or the direct (non-pooled) connection
port, with the same explicit-transaction-scoping code unchanged.

### Chosen connection mode

**Transaction-mode pooling accepted** for staging, pending re-validation of
this exact test against the real Supabase Supavisor endpoint before
production sign-off. `ConnectionStrings__Commerce` points at the pooler
endpoint (Supabase port `6543`) in each environment's Railway variables.

### Running the PoC locally

```bash
docker compose -f deploy/dev/compose.yaml up -d
dotnet test tests/Commerce.Integration/Commerce.Integration.csproj --filter "PoolerScopingTests|PostgresCloudInboxStoreTests"
```

If `deploy/dev/compose.yaml` is not running, these tests print a `SKIPPED`
line naming the unreachable target and return without asserting pass/fail —
they never silently report false confidence, and they never hard-fail a
runner that was never given the infrastructure to begin with.

## Unit 5 (not yet implemented)

Per-environment schema/RLS migration application (`psql` runbook),
`app_runtime` role provisioning against a real Supabase project, and the
`docker compose --profile full` end-to-end stack are Unit 5 scope and are
not part of this document yet.
