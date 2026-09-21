# Deploy notes

## Local development administrator

Use one local identity for Web and Desktop administration:

1. Copy `deploy/dev/.env.example` to the ignored `deploy/dev/.env` and set unique values for both variables.
2. Start the complete local stack: `docker compose -f deploy/dev/compose.yaml --profile full up -d`.
3. Run `pwsh -File deploy/dev/provision-admin.ps1`.
4. Sign in to Web and Desktop with the **same** email and password from `deploy/dev/.env`.

Desktop pairing happens before its admin window opens. The provisioning script verifies that the same identity can pair a Desktop terminal; keep credentials only in the ignored `.env` file, never in compose or init SQL.

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

## Unit 5 — One-time-per-environment schema/RLS migration

`deploy/db/migrations/0001_init_rls.sql` is the repo-owned, idempotent
migration that must be applied ONCE per real environment (staging now,
production later) directly against that environment's own Supabase project —
never auto-applied by Cloud.Api. `Program.cs`'s `/health/ready` only
*verifies* the table/RLS/policy already exist and fails readiness otherwise
(design.md "Schema/RLS application").

### Applying the migration

1. In the Supabase dashboard for the target project, copy the **direct**
   (non-pooled, port `5432`) Postgres connection string — not the transaction
   pooler (`6543`) one used at runtime by Cloud.Api. Applying DDL through the
   pooler is unnecessary and the direct connection is the standard tool for
   one-off admin operations.
2. Generate a fresh, strong password for the `app_runtime` role (a password
   manager or `openssl rand -base64 32`); this becomes a Railway environment
   secret in the next step, never a repo commit.
3. Substitute the migration's `__APP_RUNTIME_PASSWORD__` placeholder with
   that password and pipe the result into `psql`, without ever writing the
   substituted file to disk:

   ```bash
   # macOS/Linux/WSL:
   sed "s/__APP_RUNTIME_PASSWORD__/$APP_RUNTIME_PASSWORD/" \
     deploy/db/migrations/0001_init_rls.sql \
     | psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres"
   ```

   ```powershell
   # Windows PowerShell:
   (Get-Content deploy/db/migrations/0001_init_rls.sql) `
     -replace '__APP_RUNTIME_PASSWORD__', $env:APP_RUNTIME_PASSWORD |
     psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres"
   ```

4. Confirm success: `psql ... -c "SELECT rolname FROM pg_roles WHERE rolname = 'app_runtime';"`
   returns one row, and `SELECT polname FROM pg_policies WHERE tablename = 'sync_inbox';`
   returns `sync_inbox_tenant_isolation`.
5. Re-running the same command later (e.g. to rotate the password, or as a
   drift check) is safe — every statement in the migration is idempotent.

The `app_runtime` connection string (`Host=<pooler-host>;Port=6543;Database=postgres;Username=app_runtime;Password=<the-password-from-step-2>`)
becomes that environment's `ConnectionStrings__Commerce` Railway variable —
see `deploy/staging-runbook.md` for the full per-environment provisioning
checklist and the complete list of required Railway variables.

### commerce-user-credentials — `0002_users.sql`

`deploy/db/migrations/0002_users.sql` adds the `users` and `user_directory`
tables (real credential persistence, replacing the walking-skeleton sign-in).
It has NO password placeholder to substitute — `app_runtime` already exists
from `0001` — so it is applied directly:

```bash
psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres" \
  -f deploy/db/migrations/0002_users.sql
```

**Migrate-before-deploy ordering, same as `0001`**: apply `0002_users.sql`
to the target environment's database BEFORE deploying the Cloud.Api image
that expects it. `/health/ready` verifies `users`/`user_directory`
(FORCE RLS + policies) in addition to `sync_inbox`, so a deploy that runs
ahead of the migration fails closed at readiness rather than serving
requests against a missing schema. After migrating, deploy, then read the
one-time bootstrap token from `railway logs` (`POST /account/bootstrap/request-token`
followed by `POST /account/bootstrap` — see
`openspec/changes/commerce-user-credentials/design.md` "Data Flow") to
create the first admin user and confirm sign-in.

Idempotency was confirmed locally by applying `0002_users.sql` twice against
`deploy/dev/compose.yaml`'s Postgres container — the second apply is a clean
no-op (`CREATE TABLE IF NOT EXISTS`, `DROP POLICY IF EXISTS` before
`CREATE POLICY`), exactly like `0001`.

### commerce-organization-persistence — `0003_organizations_branches.sql`

`deploy/db/migrations/0003_organizations_branches.sql` adds the
`organizations` and `branches` tables — the persisted tenancy roots that
`/account/bootstrap` now creates transactionally alongside the first admin.
It has NO password placeholder to substitute — `app_runtime` already exists
from `0001` — so it is applied directly:

```bash
psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres" \
  -f deploy/db/migrations/0003_organizations_branches.sql
```

**Migrate-before-deploy ordering, same as `0001`/`0002`**: apply
`0003_organizations_branches.sql` to the target environment's database
BEFORE deploying the Cloud.Api image that expects it. `/health/ready`
verifies `organizations`/`branches` (FORCE RLS + policies) in addition to
`sync_inbox`/`users`/`user_directory`, so a deploy that runs ahead of the
migration fails closed at readiness rather than serving requests against a
missing schema.

Idempotency was confirmed locally by applying `0003_organizations_branches.sql`
twice against `deploy/dev/compose.yaml`'s Postgres container — the second
apply is a clean no-op, exactly like `0001`/`0002`.

### commerce-password-recovery — `0005_password_recovery.sql`

`deploy/db/migrations/0005_password_recovery.sql` adds the
`password_reset_tokens` table (single-use, hash-at-rest, 1-hour-expiry reset
tokens) and a `users.session_version` column used to invalidate existing
cookie sessions on any password change. It has NO password placeholder to
substitute — `app_runtime` already exists from `0001` — so it is applied
directly:

```bash
psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres" \
  -f deploy/db/migrations/0005_password_recovery.sql
```

**Migrate-before-deploy ordering, same as `0001`–`0004`**: apply
`0005_password_recovery.sql` to the target environment's database BEFORE
deploying the Cloud.Api image that expects it. `/health/ready` verifies
`password_reset_tokens` (FORCE RLS + policies) in addition to every prior
table, so a deploy that runs ahead of the migration fails closed at readiness
rather than serving requests against a missing schema.

**Deploying this change signs out every existing session once**: every
cookie issued before this change lacks the `session_ver` claim added by this
slice and is rejected at the next request — a one-time, self-healing,
fail-closed side effect. Call this out to users/support before deploying.

Idempotency was confirmed locally by applying `0005_password_recovery.sql`
twice against `deploy/dev/compose.yaml`'s Postgres container — the second
apply is a clean no-op, exactly like `0001`–`0004`.

Before smoke-testing one real reset, confirm the required Railway variables
below are set: `RESEND_API_KEY`, `EMAIL_FROM_ADDRESS` (a Resend-verified
sender domain), and `PUBLIC_BASE_URL` (the public origin the reset email
links back to). If `RESEND_API_KEY` is absent, Cloud.Api falls back to
`LogOnlyEmailSender`, which writes the reset link to stdout instead of
sending real email — safe for local/dev, not for production.

### commerce-role-taxonomy — `0006_role_taxonomy.sql` and `0007_platform_administration.sql`

Two separate, sequential files land together for this change (design.md
"Migration file split"):

- `deploy/db/migrations/0006_role_taxonomy.sql` — a ONE-SHOT, transactional
  data rewrite: every persisted `"admin"` role entry is renamed to
  `"business-admin"`, with the exact same permission set. It temporarily
  toggles `users` to `NO FORCE ROW LEVEL SECURITY` for the migration owner
  ONLY, inside one transaction, and self-asserts (`RAISE EXCEPTION`) that no
  `"admin"` entry survives before committing. It has NO password placeholder.
- `deploy/db/migrations/0007_platform_administration.sql` — additive DDL:
  `platform_admins`, `audit_log`, and the `platform_readonly` login. It DOES
  carry a password placeholder, `__PLATFORM_READONLY_PASSWORD__`, following
  `0001`'s `__APP_RUNTIME_PASSWORD__` convention exactly.

Apply both, in order, against the direct (non-pooled, port `5432`)
connection — never the pooler:

```bash
# 0006: no placeholder to substitute.
psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres" \
  -f deploy/db/migrations/0006_role_taxonomy.sql

# 0007: generate a FRESH password for platform_readonly (never reuse
# app_runtime's), same handling as step 2 of the 0001 walkthrough above.
sed "s/__PLATFORM_READONLY_PASSWORD__/$PLATFORM_READONLY_PASSWORD/" \
  deploy/db/migrations/0007_platform_administration.sql \
  | psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres"
```

```powershell
# Windows PowerShell, 0007:
(Get-Content deploy/db/migrations/0007_platform_administration.sql) `
  -replace '__PLATFORM_READONLY_PASSWORD__', $env:PLATFORM_READONLY_PASSWORD |
  psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres"
```

**Migrate-before-deploy ordering, same as `0001`–`0005`**: apply BOTH files
to the target environment's database BEFORE deploying the Cloud.Api image
that expects them. `/health/ready` verifies `platform_admins`/`audit_log`
(FORCE RLS + policies) in addition to every prior table, so a deploy that
runs ahead of the migration fails closed at readiness rather than serving
requests against a missing schema.

Idempotency: `0007` was confirmed locally by applying it twice against
`deploy/dev/compose.yaml`'s Postgres container — a clean no-op, exactly like
`0001`–`0005`. `0006` is NOT idempotent in the usual `CREATE ... IF NOT
EXISTS` sense — it is a one-shot data rewrite — but re-running it is still
safe: the second run's `UPDATE` matches zero rows (no `"admin"` entries
remain), and the post-condition assertion still passes.

**New Railway variable**: `ConnectionStrings__CommercePlatformRead` — the
`platform_readonly` login's connection string
(`Host=<pooler-host>;Port=6543;Database=postgres;Username=platform_readonly;Password=<the-password-from-0007>`).
Absent, `GET /platform/organizations` fails closed with `503` and NEVER
falls back to the shared `app_runtime` pool — see
`deploy/staging-runbook.md` for the full per-environment checklist.

**Platform-genesis runbook** (run ONCE, immediately after the first deploy
that includes this change — the window does not close itself until the
first platform admin exists):

1. `POST /platform/bootstrap/request-token` (anonymous, empty body).
2. Read the plaintext token from `railway logs` — it is NEVER returned over
   HTTP, identical to `/account/bootstrap`'s existing token delivery.
3. `POST /platform/bootstrap` with that token plus the new platform admin's
   email/password. The response is an empty-body `202` either way; a second
   genesis attempt is rejected twice over — once by the token registry
   (already consumed) and, even if called directly against the store, again
   by the database's `NOT EXISTS` INSERT policy.
4. Confirm `POST /platform/sign-in` with those credentials returns `200` and
   sets the `commerce.platform` cookie.

**`/account/bootstrap` deprecation note**: the anonymous
`/account/bootstrap/request-token` + `/account/bootstrap` pair remains
byte-identical (apart from the `"admin"` → `"business-admin"` literal) and
still works — it is not removed by this change. It is, however, now the
**deprecated operator path**: `POST /platform/organizations` (an
authenticated platform-admin action, reusing the exact same
`TryCreateBootstrapAsync` transaction) is the intended path for creating new
organizations going forward. Prefer it for every new organization; the
anonymous pair remains only for environments that have not yet run platform
genesis.

### commerce-customer-identity — `0008_customer_registry.sql`

`deploy/db/migrations/0008_customer_registry.sql` adds `customers` and
`customer_ordering_access` (the `Customer` commercial-party aggregate),
`users.customer_id` (nullable FK), and the `users_customer_has_no_roles`
CHECK — the database half of the "a customer login cannot exercise staff
permissions" guard (the in-memory half is
`UserAccount.EffectivePermissions`). It has NO password placeholder to
substitute — `app_runtime` already exists from `0001` — so it is applied
directly:

```bash
psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres" \
  -f deploy/db/migrations/0008_customer_registry.sql
```

**Verified deviation from the original proposal**: there is no `orders`
table in this repo (orders live in `CloudOrderStore`'s in-memory dictionary),
so `0008` contains NO `ALTER TABLE orders` statement and deletes no rows —
see the migration file's own header comment and design.md's "Verified
deviation 1" for the full rationale. The invariant is enforced in
`CloudOrderSubmissionService.SubmitAsync` instead.

**Migrate-before-deploy ordering, same as `0001`–`0007`**: apply
`0008_customer_registry.sql` to the target environment's database BEFORE
deploying the Cloud.Api image that expects it. `/health/ready` verifies
`customers`/`customer_ordering_access` (FORCE RLS + policies) in addition to
every prior table, so a deploy that runs ahead of the migration fails closed
at readiness rather than serving requests against a missing schema.

`ALTER TABLE users ADD CONSTRAINT users_customer_has_no_roles` validates
existing rows; every current row has `customer_id IS NULL`, so it passes
trivially — no `NOT VALID`/`VALIDATE` split is needed.

Idempotency was confirmed locally by applying `0008_customer_registry.sql`
twice against `deploy/dev/compose.yaml`'s Postgres container — the second
apply is a clean no-op, exactly like `0001`–`0007`.

**Inverse (rollback)**, shipped as comments in the migration file itself —
NOT executed automatically:

```sql
ALTER TABLE users DROP CONSTRAINT users_customer_has_no_roles;
ALTER TABLE users DROP COLUMN customer_id;
DROP TABLE customer_ordering_access;
DROP TABLE customers;
```

Rolling back re-opens the authorization defect this change fixes (the
mandatory security fix landing in Unit 3); prefer forward-fix. If only the
`users` constraint/column is a problem, drop it alone and keep the aggregate.

### commerce-pricing-engine — `0009_catalog_and_pricing.sql` (Parts A + B + C)

`deploy/db/migrations/0009_catalog_and_pricing.sql` adds **Part A**
(`products`, `presentations` with `identification_code` + its partial
per-organization unique index), **Part B** (`price_lists`,
`price_list_entries`, append-only, no `effective_to`), and **Part C**
(`supplier_price_mappings`, `price_import_batches`, `price_import_rows` —
the Excel supplier-price-import pipeline: `Staged -> Committed | Rejected`
plus `Failed`, no `Uploaded` state, `price_import_rows` append-only). It has
no password placeholder to substitute, applied the same way as `0008`:

```bash
psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres" \
  -f deploy/db/migrations/0009_catalog_and_pricing.sql
```

**Verified deviation from the proposal** (design.md "Verified deviation —
there is no persisted catalog"): `Product`/`Presentation` were, before this
migration, transient objects constructed from the request body on every
`Catalog.cs` call — there was no `products`/`presentations` table at all.
Catalog persistence was added to this change's scope for exactly this
reason: nothing downstream (identification code, price entries, import row
matching) can exist without it.

**Order-of-deploy hazard** (design.md "Migration / Rollout"): once this
image is live, `POST /orders` will deny every line whose presentation has no
effective price (Work Unit 4, later in this change) — no `price_list_entries`
row can exist before an admin publishes one. Create the organization's
default price list and publish a price for every ordered presentation
immediately after deploy, before announcing ordering, once Work Units 3-5
land.

**Migrate-before-deploy ordering, same as `0001`–`0008`**: apply `0009`
before deploying the Cloud.Api image that expects it. `/health/ready`
verifies `products`/`presentations`/`price_lists`/`price_list_entries`/
`supplier_price_mappings`/`price_import_batches`/`price_import_rows`
(FORCE RLS + policies) in addition to every prior table.

**Untrusted-input guards** (design.md "Import state machine and
untrusted-file handling"): the import endpoint validates OpenXML magic
bytes, file size (≤5 MB), and used-range row count (≤5 000, checked before
cell materialization) before any cell is read, and reads cached cell values
only — never triggering formula recalculation. A file that fails validation
creates no `price_import_batches` row.

Idempotency was confirmed locally by applying `0009_catalog_and_pricing.sql`
twice against `deploy/dev/compose.yaml`'s Postgres container.

**Inverse (rollback)**, shipped as comments in the migration file itself —
NOT executed automatically:

```sql
DROP TABLE price_import_rows;
DROP TABLE price_import_batches;
DROP TABLE supplier_price_mappings;
DROP TABLE price_list_entries;
DROP TABLE price_lists;
DROP TABLE presentations;
DROP TABLE products;
```

Lossy once real price-carrying orders exist (design.md "Rollback Plan");
prefer forward-fix after first real use.

### commerce-guest-ordering — `0010_guest_ordering.sql`

`deploy/db/migrations/0010_guest_ordering.sql` adds `guest_order_verifications`
(design.md "Verification state shape"): a 6-digit code stored only as a
SHA-256 hex hash, `expires_at` (10 minutes), `attempt_count` bounded at 5 by a
CHECK constraint, and `confirmed_at`/`consumed_at`/`consumed_order_id` so a
single confirmed row admits exactly one order. It has no password placeholder
to substitute, applied the same way as `0008`/`0009`:

```bash
psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres" \
  -f deploy/db/migrations/0010_guest_ordering.sql
```

**Migrate-before-deploy ordering, same as `0001`–`0009`**: apply `0010`
before deploying the Cloud.Api image that expects it. `/health/ready`
verifies `guest_order_verifications` (FORCE RLS + policies) in addition to
every prior table, so a deploy that runs ahead of the migration fails closed
at readiness rather than serving requests against a missing schema.

Idempotency was confirmed locally by applying `0010_guest_ordering.sql` twice
against `deploy/dev/compose.yaml`'s Postgres container — the second apply is
a clean no-op, exactly like `0001`–`0009`.

**Two new Railway variables gate the public guest-ordering surface**
(design.md "Migration / Rollout"): `GuestOrdering__OrganizationId` and
`GuestOrdering__BranchId` (Vaca Verde and its principal branch). Deploy the
image with them **unset** first — the app runs normally, `/public/*` returns
404, and nothing about staff or registered-customer behaviour changes. Set
them only when the guest surface is ready to announce; unsetting them again
is the **narrowest rollback** — the entire public surface disappears within
one restart, leaving the domain origin field and the customer session intact.

Guest verification email reuses the existing `IEmailSender` seam from
commerce-password-recovery — no new Railway variable is needed for delivery
itself; the same `RESEND_API_KEY`/`EMAIL_FROM_ADDRESS` pair already documented
above under `0005_password_recovery.sql` covers it. If `RESEND_API_KEY` is
absent, verification codes are logged via `LogOnlyEmailSender` instead of
sent — safe for local/dev, not for production.

**Inverse (rollback)**, shipped as comments in the migration file itself —
NOT executed automatically:

```sql
DROP TABLE guest_order_verifications;
```

Lossy only for in-flight verifications (minutes of state, re-requestable);
no order data is persisted anywhere today (design.md "Migration / Rollout").

### commerce-payments — `0011_payments.sql`

`deploy/db/migrations/0011_payments.sql` adds `payment_entries` (the
append-only payment ledger; design.md "Interfaces / Contracts") and
`customers.billing_instrument_reference` (a new, separately-named nullable
column — `customers.payment_terms` is untouched). Applied the same way as
`0008`–`0010`:

```bash
psql "postgresql://postgres:<db-password>@<project-ref>.supabase.co:5432/postgres" \
  -f deploy/db/migrations/0011_payments.sql
```

**Migrate-before-deploy ordering, same as `0001`–`0010`**: apply `0011`
before deploying the Cloud.Api image that expects it. `/health/ready`
verifies `payment_entries` (FORCE RLS + policy) in addition to every prior
table, so a deploy that runs ahead of the migration fails closed at
readiness rather than serving requests against a missing schema.

Idempotency was confirmed locally by applying `0011_payments.sql` twice
against `deploy/dev/compose.yaml`'s Postgres container — the second apply is
a clean no-op, exactly like `0001`–`0010`.

**No payment-provider secret exists, and this is deliberate** (proposal.md
"Out of Scope" — provider selection is a future ADR): `Payments__ManuallyRecordedApprovalEnabled`
is the ONLY payments-related setting, and its absence is fail-CLOSED, not
fail-open — `IPaymentApprovalGateway` binds `UnavailablePaymentApproval`
(every `POST /payments` returns 503, never a silent approval) unless it is
explicitly set to `true`. This is the deliberate inverse of the
`RESEND_API_KEY` precedent (commerce-password-recovery), where absence falls
back safely to a no-op logger — money is not a notification, so absence here
never substitutes a safe default; it substitutes an explicit refusal.

**Inverse (rollback)**, shipped as comments in the migration file itself —
NOT executed automatically:

```sql
ALTER TABLE customers DROP CONSTRAINT customers_instrument_not_pan_shaped;
ALTER TABLE customers DROP COLUMN billing_instrument_reference;
DROP TABLE payment_entries;
```

Lossy only after first real use (settlement history has no prior model to
fall back to; design.md "Migration / Rollout") — prefer forward-fix then.
**Narrowest rollback**: stop mapping `MapPaymentEndpoints()` in `Program.cs`
— the domain stays dormant and the table stays empty/inert, with no
migration to revert.

## Local full stack via Docker Compose

`deploy/dev/compose.yaml` gained a `full` profile (Unit 5) that also
containerizes Cloud.Api, building the same repo-root `Dockerfile` Unit 2
already created, alongside the existing `postgres` + `pgbouncer` services:

```bash
docker compose -f deploy/dev/compose.yaml --profile full up
```

This serves Cloud.Api (with the SPA it embeds in `wwwroot`) at
`http://localhost:8080`, backed by the same containerized Postgres, with zero
`dotnet run` / `npm run dev` needed. The default (no `--profile full`)
behavior — Postgres + PgBouncer only, for the fast `dotnet run` inner loop —
is unchanged; `full` is strictly additive.

Commerce.Pos.Windows never requires Docker: it only needs an already-running
Cloud.Api (local, containerized, or staging) to sync against.

### commerce-admin-console — `0012_admin_console.sql`

Apply `0012_admin_console.sql` after the existing migration lineage. It migrates legacy platform administrator identities into the reserved Incoders Platform organization, preserves the password hashes, and removes the legacy `platform_admins` table. After rollout, reset each migrated system administrator password through the unified `/account` identity flow; do not run the retired platform-genesis workflow.
