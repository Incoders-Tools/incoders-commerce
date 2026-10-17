# Railway runbook: staging and production

How to provision and operate Cloud.Api and its PostgreSQL database on Railway Pro,
per [ADR-014](../docs/architecture/decisions/ADR-014-railway-hosted-postgres.md).
Every step is a manual action in the Railway dashboard, the Railway CLI, or a DNS
panel. This file records how to do it, not that it was done; tick the go-live
items in [go-live requirements](../docs/launch/go-live-requirements.md) with
evidence instead.

One Railway project holds two environments with the same shape:

| Environment | Deploys from | ADR-004 channel | Platform origin |
|---|---|---|---|
| `staging` | `staging` branch | `pilot` | `https://app.staging.vaca-verde.com.ar` |
| `production` | `main` branch | `stable` | `https://app.vaca-verde.com.ar` |

Each environment contains two services: **Cloud.Api** (the API, which also serves
the web SPA from its `wwwroot`) and **Postgres**. They talk over the
environment's private network, and staging cannot reach production. Production
also runs **vaca-verde-site**, Vaca Verde's public website, on
`vaca-verde.com.ar` and `www.vaca-verde.com.ar` (section 6c). The hostnames come
from the [ADR-006 amendment](../docs/architecture/decisions/ADR-006-domain-and-subdomain-topology.md).

## 0. Prerequisites

- **Domain.** `vaca-verde.com.ar`, registered at NIC Argentina under the owner's
  account (done 2026-10-16). Section 6 delegates its DNS to Cloudflare.
- **Email.** A Resend account with the domain verified, so that password-reset
  emails can be sent from it. Without `RESEND_API_KEY`, Cloud.Api only logs the
  emails.
- **Tools on the operator's machine.** The Railway CLI (`railway login`),
  PowerShell 7 (`pwsh`), and the PostgreSQL 16 client tools (`psql`, `pg_dump`,
  `pg_restore`).
- **A password manager** for every secret below. No secret is ever committed.

## 1. Plan and cost control

1. Subscribe the workspace to **Pro** ($20/month, including $20 of usage).
2. On the workspace **Usage** page, set a **custom email alert at $20** and leave
   the **hard limit empty**. The same setting from the CLI:

   ```powershell
   railway usage limit set --target workspace --soft 20
   ```

   The hard limit takes every workload offline when reached, including
   production. Set one only as a deliberate owner decision, and only well above
   normal spend (Railway suggests 3-5 times).

Expected usage at Vaca Verde's load (POS sync plus 2-3 web users):

| Item, per environment | Estimate |
|---|---|
| Cloud.Api (about 200 MB RAM, mostly idle CPU) | $2.5-3.5 |
| Postgres (about 150 MB RAM plus a 1 GB volume) | $1.5-2.5 |
| Backups, PITR, and egress | $0.5-1 |
| **Total per environment** | **$4.5-7** |

Two environments come to about $9-14, inside the $20 credit. Resources are
billed per minute at $10/GB-month of RAM, $20/vCPU-month, $0.15/GB-month of
volume, and $0.05/GB of egress. Private-network traffic is free.

## 2. Project and environments

1. Create a project named `incoders-commerce`. It starts with a `production`
   environment.
2. Create a second environment named `staging`.
3. Link the repository checkout once: `railway link`, then select the project.
   Every CLI command below takes `--environment staging` or
   `--environment production`.

Repeat sections 3 to 9 once per environment, staging first.

## 3. Postgres service

1. In the environment, add **Database → PostgreSQL** and name the service
   `Postgres`.
2. In **Settings → Source**, confirm the image is
   `ghcr.io/railwayapp-templates/postgres-ssl:18`. Use the major-version tag
   only: point-in-time recovery refuses minor tags such as `:18.6`.
3. In **Settings → Networking**, **remove the TCP proxy** (public access). The
   template enables it by default. Without it, the database is reachable only
   from the private network and through `railway connect`'s SSH tunnel.
4. Leave the database name as `railway`.

The `postgres` user Railway creates is a **superuser, and a superuser bypasses
RLS**. Use it only to apply migrations. Cloud.Api must never connect as
`postgres`.

## 4. Migrations (automatic) and the runtime roles

Migrations are not applied by hand. Every Cloud.Api deployment first runs
`Commerce.Migrator` as Railway's **pre-deploy command** (`railway.json`), in a
separate container on the private network
([ADR-016](../docs/architecture/decisions/ADR-016-migrations-at-deploy-and-rollback.md)):

- It applies pending files from `deploy/db/migrations` in order and records each
  one (name and checksum) in `public.schema_migrations`. Applied files are
  skipped, so every deploy is idempotent.
- It refuses to run if an applied file was edited, and holds an advisory lock
  so two deployments never migrate at once. A deployment that finds the lock
  taken prints `waiting for the migration lock` (at most every 30 seconds) and
  gives up after 10 minutes: it exits 1 with nothing applied, so that
  deployment is cancelled. To change the wait, set
  `MIGRATOR_LOCK_TIMEOUT_SECONDS` (whole seconds, at least 1) on Cloud.Api; the
  pre-deploy command shares its variables. Only the wait for the lock is
  bounded; a migration that is running is never cut off.
- **If anything fails, the deployment is cancelled and the previous version
  keeps serving.** The deploy log shows which file failed.

The first deployment of an environment creates every table and both runtime
roles (`app_runtime`, `platform_readonly`). Their passwords come from
Cloud.Api's variables (section 5), so generate them first and store them in the
password manager. Base64 output contains no quote characters, so it is safe in
the SQL literal (the migrator refuses a value that contains `'`, naming the
variable but never printing the value):

```powershell
$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

**Running the migrator by hand** (for example to inspect a failure) uses the
same program through a tunnel. In one terminal, open the tunnel; it prints the
port and the `postgres` password, and stays open until Ctrl+C:

```powershell
railway connect Postgres --environment staging --tunnel-only
```

In a second terminal at the repository root:

```powershell
$env:ConnectionStrings__Migrations = 'Host=localhost;Port=<tunnel port>;Database=railway;Username=postgres;Password=<postgres password>'
$env:APP_RUNTIME_PASSWORD = '<app_runtime password>'
$env:PLATFORM_READONLY_PASSWORD = '<platform_readonly password>'
dotnet run --project src/Commerce.Migrator -- --migrations deploy/db/migrations
```

To check the roles through the same tunnel (with `psql` installed):
`SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname IN ('app_runtime', 'platform_readonly');`
returns two rows with `f` and `f`.

**Rotating a role password:** the migration that created the role does not run
again, so rotate it explicitly. Run
`ALTER ROLE app_runtime WITH PASSWORD '<new>';` through the tunnel, then update
both `APP_RUNTIME_PASSWORD` and `ConnectionStrings__Commerce` (section 5). Do
the same for `platform_readonly`.

## 5. Cloud.Api service

1. Add a service from the GitHub repository and name it `Cloud.Api`. Railway
   reads `railway.json` (Dockerfile build, pre-deploy migrations, `/health`
   healthcheck, restart on failure); nothing else needs configuring for the
   build. Set the variables (step 3) before the first deployment, because the
   migrator needs them.
2. In **Settings → Source**, set the trigger branch: `staging` in the staging
   environment, `main` in production. A push to that branch that touches a
   watched path deploys that environment only.
3. Set the variables:

| Variable | Value |
|---|---|
| `ConnectionStrings__Commerce` | `Host=postgres.railway.internal;Port=5432;Database=railway;Username=app_runtime;Password=<app_runtime password>` |
| `ConnectionStrings__CommercePlatformRead` | `Host=postgres.railway.internal;Port=5432;Database=railway;Username=platform_readonly;Password=<platform_readonly password>`. When it is absent, `GET /account/organizations` answers 503. It never falls back to `app_runtime`. |
| `ConnectionStrings__Migrations` | `Host=postgres.railway.internal;Port=5432;Database=railway;Username=postgres;Password=${{Postgres.PGPASSWORD}}`. A reference variable, so the superuser password is never copied. Only the migrator reads it; Cloud.Api never does. |
| `APP_RUNTIME_PASSWORD`, `PLATFORM_READONLY_PASSWORD` | The two passwords from section 4. They must match the passwords in the two connection strings above. |
| `ASPNETCORE_ENVIRONMENT` | `Staging` or `Production`. Never `Development`: that enables the test-seed endpoints. |
| `PUBLIC_BASE_URL` | The environment's public origin, with no trailing slash. Password-reset links are built from it. Use the Railway domain until the custom domain is verified (section 6). |
| `RESEND_API_KEY` | The Resend API key. |
| `EMAIL_FROM_ADDRESS` | A sender on the Resend-verified domain, for example `no-reply@vaca-verde.com.ar`. |
| `GuestOrdering__OrganizationId`, `GuestOrdering__BranchId` | Set both, or neither. Leave them unset until the public ordering surface is announced; while they are unset, every `/public/*` route answers 404. |
| `Payments__ManuallyRecordedApprovalEnabled` | Leave unset (`POST /payments` answers 503) until staff-recorded payments go live, then set it to `true`. |
| `Business__TimeZone` | Optional. The default is `America/Argentina/Buenos_Aires`. |
| `PORT` | Do not set it. Railway injects it. |

The Postgres host is the same name in both environments, but it resolves only
inside the environment the service runs in.

## 6. Domain

Do 6a once; do 6b in each environment; do 6c and 6d in production only.

### 6a. Delegate DNS from NIC Argentina to Cloudflare (once)

**Done 2026-10-16:** `vaca-verde.com.ar` is delegated to `adi.ns.cloudflare.com`
and `sullivan.ns.cloudflare.com` (checked through 8.8.8.8 and 1.1.1.1). The
Cloudflare zone was created with Bot Preference Sync off and no records.

NIC Argentina only points a domain at nameservers; the records live at a DNS
provider. Cloudflare's free plan is used because it flattens a CNAME at the apex,
which `vaca-verde.com.ar` needs to point at Railway.

1. Create a Cloudflare account, choose **Add a domain**, enter
   `vaca-verde.com.ar`, and pick the **Free** plan. Cloudflare shows two
   nameservers (`<name>.ns.cloudflare.com`).
2. Sign in to nic.ar with the account that registered the domain, open the
   domain, and use its **delegation** option ("Delegar") to replace the
   nameservers with Cloudflare's two.
3. Wait until Cloudflare marks the domain **Active** (it emails; usually within a
   few hours, up to 48).

### 6b. Platform domain on Cloud.Api (each environment)

1. In Cloud.Api's **Settings → Networking**, add the environment's platform
   hostname: `app.vaca-verde.com.ar` in production,
   `app.staging.vaca-verde.com.ar` in staging. Railway shows a `CNAME` and a `TXT`
   record.
2. Create **both** records in Cloudflare's DNS panel, with the CNAME's proxy
   status set to **DNS only** (grey cloud), so Railway issues and renews the
   certificate itself. The hostname answers 404 until the `TXT` record is
   verified.
3. When Railway shows a green check, set `PUBLIC_BASE_URL` to
   `https://app.vaca-verde.com.ar` (or the staging hostname) and point every POS
   terminal of that environment at the same origin
   (`Commerce:CloudApiBaseUrl`).

### 6c. Vaca Verde's public website (production)

`sites/vaca-verde` is a static site that sends clients to `/order` and staff to
`/login` on the platform. It ships with its own `Dockerfile`, `Caddyfile`, and
`railway.json`.

1. In the production environment, add a service from the GitHub repository and
   name it `vaca-verde-site`.
2. In **Settings → Source**, set the root directory to `sites/vaca-verde`, the
   trigger branch to `main`, and the config file path to
   `/sites/vaca-verde/railway.json` (Railway does not look for it under the root
   directory by itself).
3. Set the variable `VITE_APP_URL` to `https://app.vaca-verde.com.ar`. The build
   fails on purpose without it.
4. Add the custom domains `vaca-verde.com.ar` and `www.vaca-verde.com.ar`, and
   create their records in Cloudflare as in 6b. Cloudflare flattens the apex
   CNAME automatically.

The site costs about $0.3-0.5/month of usage (a small Caddy server).

### 6d. Email sender (production)

1. In Resend, add the domain `vaca-verde.com.ar`. Resend lists DNS records
   (DKIM, SPF, and return-path records).
2. Create them in Cloudflare (DNS only) and wait for Resend to show the domain
   as verified.
3. Set `EMAIL_FROM_ADDRESS` (for example `no-reply@vaca-verde.com.ar`) and
   `RESEND_API_KEY` on Cloud.Api, then request a password reset to confirm the
   email arrives and its link points at `https://app.vaca-verde.com.ar`.

Another client's domain later becomes one more custom domain on this same
Cloud.Api service, not another deployment ([ADR-015](../docs/architecture/decisions/ADR-015-shared-multi-client-platform.md)).
Cloud.Api does not resolve an organization from the request host yet, so that
feature has to exist first.

## 7. Backups

Configure all three layers in each environment. Deleting a volume deletes its
volume backups, so only the third layer survives losing the volume or the
account.

1. **Scheduled volume backups.** On the Postgres service's **Backups** tab,
   select **Daily**, **Weekly**, and **Monthly** (kept 6 days, about a month,
   and about 3 months).
2. **Point-in-time recovery.** On the same tab, click **Enable PITR**. Railway
   creates a `Postgres-PITR` bucket and redeploys the database. The restore
   window starts at the first base backup taken after enabling, and grows to
   about 4 weeks. Check that archiving is healthy:

   ```powershell
   railway postgres pitr status --service Postgres --environment production
   ```

3. **An off-Railway dump, monthly and before every release that adds a
   migration.** With the tunnel from section 4 open:

   ```powershell
   $env:PGPASSWORD = '<postgres password printed by the tunnel>'
   pg_dump 'host=localhost port=<tunnel port> dbname=railway user=postgres' `
       --format=custom --no-owner `
       --file "commerce-production-$(Get-Date -Format yyyyMMdd-HHmm).dump"
   ```

   Store the file encrypted outside Railway, for example in the owner's cloud
   drive, and keep at least the last 3.

Before applying a release's migrations, also take a named manual backup:

```powershell
railway postgres pitr backup create --service Postgres --environment production --name pre-<release>
```

## 8. First identities

**The organization and its first `business-admin`** come from the one-time
bootstrap flow, which works in every environment. The token reaches only the
server log, never the HTTP response, and each step answers `409` once the
organization has any user.

```powershell
$origin = '<origin>'
$organizationId = [guid]::NewGuid()
Invoke-RestMethod -Method Post "$origin/account/bootstrap/request-token" `
    -ContentType 'application/json' -Body (@{ organizationId = $organizationId } | ConvertTo-Json)

# Copy the token from the line "Bootstrap token for organization <id>: <token>":
railway logs --service Cloud.Api --environment production

Invoke-RestMethod -Method Post "$origin/account/bootstrap" -ContentType 'application/json' -Body (@{
    organizationId   = $organizationId
    token            = '<token from the log>'
    organizationName = 'Vaca Verde'
    branchName       = 'Ruta 51'
    email            = '<business-admin email>'
    password         = '<business-admin password>'
} | ConvertTo-Json)
```

The token is single-use, and a failed bootstrap burns it: request a new one
and repeat. Sign in with the `business-admin` credentials to confirm.

**The platform system administrator** (`is_system_admin`) has no supported
path outside Development. `deploy/dev/provision-admin.ps1` promotes one
through the Development-only `/internal/test-seed/user` endpoint and refuses
non-loopback APIs. The `/platform/bootstrap` genesis flow that
`deploy/README.md` describes no longer exists in the code. Whether production
needs a system administrator, and how to create one, is an open decision in
[go-live requirements](../docs/launch/go-live-requirements.md).

## 9. Verify the environment

1. `GET <origin>/health` answers 200 (liveness).
2. `GET <origin>/health/ready` answers 200. This proves Cloud.Api reached
   Postgres over the private network as `app_runtime` and found the full
   schema and RLS shape. Cloud.Api never applies DDL itself.
3. The SPA loads at the origin, and a staff user can sign in.
4. A POS terminal pointed at the origin pairs and syncs
   (`deploy/pos-manual-verify.md`).

## 10. Restore drill and incident restore

Run the drill in staging before go-live, and again after any change to this
runbook's backup steps. Record the date and result in the go-live checklist.

1. Write a recognizable row in staging (for example, create a test customer)
   and note the time `T`.
2. Restore to a time just before `T` in a new sibling service:

   ```powershell
   railway postgres pitr restore --service Postgres --environment staging --at '<T minus one minute, RFC 3339>' --new-service-name Postgres-restore-drill
   ```

3. Open a tunnel to `Postgres-restore-drill` and confirm the test row is absent
   and the rest of the data is present.
4. Delete `Postgres-restore-drill`.

For a real incident in production, restore the same way. Then switch Cloud.Api
to the restored service: replace `postgres.railway.internal` with the restored
service's private domain in **all three** connection strings
(`ConnectionStrings__Commerce`, `ConnectionStrings__CommercePlatformRead`, and
`ConnectionStrings__Migrations`, whose password reference becomes the
restored service's `PGPASSWORD`), and redeploy. If `ConnectionStrings__Migrations`
kept pointing at the old service, the next deploy would migrate the wrong
database. The runtime roles and their passwords come back with the data, and so
does the `schema_migrations` ledger. Keep the original service until the
restored data is confirmed, then delete it.

To recover from a scheduled volume backup instead, use the **Backups** tab:
an in-place restore overwrites the current data.
