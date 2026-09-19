# Staging Provisioning Runbook

This document is intentionally **documentation only**. No task in this SDD
change executes any of the steps below — creating Railway or Supabase
resources is a manual, out-of-repo action that this environment cannot
perform (no account/API credentials for either service), and per the honesty
precedent set in every prior unit of `commerce-deployment-orchestration`
(the pooler PoC, Unit 4's manual Windows verification), this file records
*how* to do it, not a claim that it was done.

Provisioning staging is a one-time setup. Provisioning production later is
the same procedure with different variable values and a different Railway
service/branch mapping — no code change, per design.md's "Environment
config" decision.

## 1. Create the Supabase project

1. In the Supabase dashboard, create a new project named e.g.
   `incoders-commerce-staging`. Choose a region close to Railway's chosen
   region to minimize latency between the API and the database.
2. Record the project's:
   - Direct (non-pooled) connection string, port `5432` — used only for the
     one-time migration apply (step 2 below) and future manual admin work.
   - Transaction-pooler (Supavisor) connection string, port `6543` — used by
     Cloud.Api at runtime (`ConnectionStrings__Commerce`, step 4).
   - Project ref and the `postgres` superuser password (from project
     creation), needed to run the migration in step 2.

## 2. Apply the schema/RLS migration and provision `app_runtime`

Follow `deploy/README.md`'s "Applying the migration" section exactly:
generate a fresh `app_runtime` password, substitute it into
`deploy/db/migrations/0001_init_rls.sql`, and pipe the result into `psql`
against the project's **direct** connection string from step 1. Confirm the
role and policy exist per that section's verification queries.

Store the generated `app_runtime` password in a secret manager — it becomes
a Railway variable in step 4, never a repo commit.

Apply every subsequent migration (`0002_users.sql` through the latest,
currently `0010_guest_ordering.sql`) against the same direct connection
string, in numeric order, per each migration's own section in
`deploy/README.md`. `/health/ready` verifies the full cumulative schema/RLS
shape, so a deploy that runs ahead of any one of these migrations fails
closed at readiness.

## 3. Create the Railway project and link the GitHub repo

1. In Railway, create a new project (e.g. `incoders-commerce-staging`).
2. Add a service from this GitHub repository, mapped to the branch that
   should deploy to staging (e.g. `dev` or a dedicated `staging` branch —
   whichever this repo's branching convention designates).
3. Railway auto-detects `railway.json` at the repo root (`build.builder:
   "DOCKERFILE"`, `build.dockerfilePath: "Dockerfile"`, `build.watchPatterns`)
   — no additional build configuration is needed in the Railway dashboard.
   This is native GitHub push-to-deploy: a push to the mapped branch that
   touches a watched path triggers a build and deploy automatically, with no
   GitHub Actions deploy step and no Railway CLI token in CI (design.md
   "Deploy mechanism").
4. Confirm `deploy.healthcheckPath: "/health"` in `railway.json` is picked up
   — Railway only marks a deployment healthy once `/health` responds
   successfully within `deploy.healthcheckTimeout` (30s).

## 4. Set Railway environment variables

On the Railway service created in step 3, set:

| Variable | Value | Notes |
|---|---|---|
| `ConnectionStrings__Commerce` | `Host=<project-ref>.pooler.supabase.com;Port=6543;Database=postgres;Username=app_runtime;Password=<app_runtime password from step 2>` | Points at Supabase's **transaction-mode pooler** (port `6543`), per design.md's "Pooling + tenant scope" decision and the accepted pooler PoC outcome recorded in `deploy/README.md`. Never the `service_role` key, never the table owner. |
| `ASPNETCORE_ENVIRONMENT` | `Staging` | Selects `appsettings.Staging.json` if/when one is added; falls back to `appsettings.json` otherwise. No literal `"staging"` string exists in application code — this is the only place the environment name is set (design.md "Environment config"). |
| `PORT` | *(do not set manually)* | Railway injects this automatically; `Program.cs` reads it and binds Kestrel to `0.0.0.0:$PORT`. Do not hardcode a port. |
| `RESEND_API_KEY` | *(Resend dashboard API key)* | Used by `ResendEmailSender` to deliver forgot-password reset emails. If absent, Cloud.Api falls back to `LogOnlyEmailSender` (stdout only) — safe for staging smoke tests, not for a real user-facing environment. |
| `EMAIL_FROM_ADDRESS` | *(a Resend-verified sender address)* | The `From` address on reset emails. Must belong to a domain verified in the Resend dashboard or delivery fails. |
| `PUBLIC_BASE_URL` | `https://<railway-domain>` | The public origin the reset email's link points back to (`{PUBLIC_BASE_URL}/reset-password/{token}`). |
| `ConnectionStrings__CommercePlatformRead` | `Host=<project-ref>.pooler.supabase.com;Port=6543;Database=postgres;Username=platform_readonly;Password=<platform_readonly password from 0007>` | commerce-role-taxonomy: the ONLY cross-organization read capability in the system — a distinct, column-scoped least-privilege login provisioned by `0007_platform_administration.sql` (see `deploy/README.md`'s `0006`/`0007` section). Absent, `GET /platform/organizations` fails closed with `503` and NEVER falls back to `app_runtime`. Generate its password with the same discipline as `app_runtime`'s in step 2 — a fresh, strong, per-environment secret, never a repo commit. |
| `GuestOrdering__OrganizationId` | *(Vaca Verde's organization `uuid`)* | commerce-guest-ordering: the ONE org/branch resolution point (`GuestOrderTarget.TryFromConfiguration`) for the anonymous `/public/*` surface. Absent or unparseable together with `GuestOrdering__BranchId` ⇒ `MapPublicOrderingEndpoints` is never called and every `/public/*` route is 404 — deploy with these UNSET first, then set them only when the guest surface is ready to announce (design.md "Migration / Rollout"). |
| `GuestOrdering__BranchId` | *(Vaca Verde's principal branch `uuid`)* | Paired with `GuestOrdering__OrganizationId` above — both or neither. `branches` has no `is_principal`/`is_default` column, so this value must be looked up manually (e.g. `SELECT id FROM branches WHERE organization_id = '<org-id>'`) and supplied at deploy time; it cannot be derived. |

No other environment variables are currently read by `Program.cs` or
`appsettings*.json`. If a future change adds configuration (e.g. an
`Commerce__CloudApiBaseUrl` equivalent for server-to-server calls), add it to
this table rather than hardcoding it.

## 5. Verify

1. Push a commit to the mapped branch and confirm Railway starts a build.
2. After deploy, confirm `GET https://<railway-domain>/health` returns 200.
3. Confirm `GET https://<railway-domain>/health/ready` returns 200 — this
   proves Cloud.Api successfully verified the schema/RLS/role provisioned in
   step 2 against the real Supabase project (it does not apply any DDL
   itself).
4. Point a browser at the Railway domain and confirm the SPA (embedded in
   the same image's `wwwroot`) loads and can sign in and complete a
   catalog/order flow, per `deploy/README.md`'s SPA verification notes.
5. Optionally, point a Pos.Windows installation's `Commerce:CloudApiBaseUrl`
   at the staging domain and repeat `deploy/pos-manual-verify.md`'s sync
   steps against staging instead of local Docker.

## Non-goals

- This runbook does not create any Railway or Supabase resource — every step
  above is a manual action for a human with dashboard/account access to
  perform themselves.
- Production provisioning is intentionally out of scope for this change; it
  is the same procedure with a separate Supabase project, a separate Railway
  service/branch, and separate `app_runtime` credentials.
