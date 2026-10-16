# Railway runbook: staging and production

How to provision and operate Cloud.Api and its PostgreSQL database on Railway Pro,
per [ADR-014](../docs/architecture/decisions/ADR-014-railway-hosted-postgres.md).
Every step is a manual action in the Railway dashboard, the Railway CLI, or a DNS
panel. This file records how to do it, not that it was done; tick the go-live
items in [go-live requirements](../docs/launch/go-live-requirements.md) with
evidence instead.

One Railway project holds two environments with the same shape:

| Environment | Deploys from | ADR-004 channel | Origin (suggested) |
|---|---|---|---|
| `staging` | `staging` branch | `pilot` | `https://staging.vacaverde.com.ar` |
| `production` | `main` branch | `stable` | `https://vacaverde.com.ar` |

Each environment contains two services: **Cloud.Api** (the API, which also serves
the web SPA from its `wwwroot`) and **Postgres**. They talk over the
environment's private network, and staging cannot reach production.

## 0. Prerequisites

- **Domain.** Register `vacaverde.com.ar` at NIC Argentina (an annual fee, paid in
  pesos) under the owner's account
  ([ADR-006](../docs/architecture/decisions/ADR-006-domain-and-subdomain-topology.md)).
  Use a DNS provider that supports CNAME flattening (ALIAS/ANAME) at the apex,
  for example Cloudflare's free plan, and delegate the domain's nameservers to it.
  Railway needs a CNAME at the apex.
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
   `ghcr.io/railwayapp-templates/postgres-ssl:16`. Use the major-version tag
   only: point-in-time recovery refuses minor tags such as `:16.10`.
3. In **Settings → Networking**, **remove the TCP proxy** (public access). The
   template enables it by default. Without it, the database is reachable only
   from the private network and through `railway connect`'s SSH tunnel.
4. Leave the database name as `railway`.

The `postgres` user Railway creates is a **superuser, and a superuser bypasses
RLS**. Use it only to apply migrations. Cloud.Api must never connect as
`postgres`.

## 4. Apply migrations and create the runtime roles

1. Generate two independent passwords, one for `app_runtime` and one for
   `platform_readonly`, and store both in the password manager. Base64 output
   contains no quote characters, so it is safe inside the SQL literal:

   ```powershell
   [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
   ```

2. In one terminal, open a tunnel to the database. It prints a local host, port,
   and the `postgres` password, and stays open until you press Ctrl+C:

   ```powershell
   railway connect Postgres --environment staging --tunnel-only
   ```

3. In a second `pwsh` terminal at the repository root, apply every migration in
   order. The two role passwords are substituted in memory and never written to
   disk:

   ```powershell
   $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
   $env:PGCLIENTENCODING = 'UTF8'
   $env:PGPASSWORD = '<postgres password printed by the tunnel>'
   $target = 'host=localhost port=<tunnel port> dbname=railway user=postgres'
   $secrets = @{
       '__APP_RUNTIME_PASSWORD__'       = '<app_runtime password>'
       '__PLATFORM_READONLY_PASSWORD__' = '<platform_readonly password>'
   }
   foreach ($file in Get-ChildItem deploy/db/migrations/*.sql | Sort-Object Name) {
       $sql = Get-Content $file.FullName -Raw -Encoding UTF8
       foreach ($key in $secrets.Keys) { $sql = $sql.Replace($key, $secrets[$key]) }
       $sql | psql $target -v ON_ERROR_STOP=1 -q
       if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($file.Name)" }
       "applied $($file.Name)"
   }
   ```

   Every migration is idempotent, so a later release runs the same loop and the
   migrations already applied are no-ops. Do not use `--single-transaction`:
   some migrations commit their own transactions.

4. Verify the result in the same terminal:

   ```powershell
   psql $target -c "SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname IN ('app_runtime', 'platform_readonly');"
   ```

   Two rows, both with `rolsuper = f` and `rolbypassrls = f`.

## 5. Cloud.Api service

1. Add a service from the GitHub repository and name it `Cloud.Api`. Railway
   reads `railway.json` (Dockerfile build, `/health` healthcheck, restart on
   failure); nothing else needs configuring for the build.
2. In **Settings → Source**, set the trigger branch: `staging` in the staging
   environment, `main` in production. A push to that branch that touches a
   watched path deploys that environment only.
3. Set the variables:

| Variable | Value |
|---|---|
| `ConnectionStrings__Commerce` | `Host=postgres.railway.internal;Port=5432;Database=railway;Username=app_runtime;Password=<app_runtime password>` |
| `ConnectionStrings__CommercePlatformRead` | `Host=postgres.railway.internal;Port=5432;Database=railway;Username=platform_readonly;Password=<platform_readonly password>`. When it is absent, `GET /platform/organizations` answers 503. It never falls back to `app_runtime`. |
| `ASPNETCORE_ENVIRONMENT` | `Staging` or `Production`. Never `Development`: that enables the test-seed endpoints. |
| `PUBLIC_BASE_URL` | The environment's public origin, with no trailing slash. Password-reset links are built from it. Use the Railway domain until the custom domain is verified (section 6). |
| `RESEND_API_KEY` | The Resend API key. |
| `EMAIL_FROM_ADDRESS` | A sender on the Resend-verified domain, for example `no-reply@vacaverde.com.ar`. |
| `GuestOrdering__OrganizationId`, `GuestOrdering__BranchId` | Set both, or neither. Leave them unset until the public ordering surface is announced; while they are unset, every `/public/*` route answers 404. |
| `Payments__ManuallyRecordedApprovalEnabled` | Leave unset (`POST /payments` answers 503) until staff-recorded payments go live, then set it to `true`. |
| `Business__TimeZone` | Optional. The default is `America/Argentina/Buenos_Aires`. |
| `PORT` | Do not set it. Railway injects it. |

The Postgres host is the same name in both environments, but it resolves only
inside the environment the service runs in.

## 6. Custom domain

1. In Cloud.Api's **Settings → Networking**, add the environment's custom domain
   (see the table at the top). Pro allows 20 custom domains per service.
2. Create **both** records Railway shows, the `CNAME` and the `TXT`, at the DNS
   provider. The domain answers 404 until the `TXT` record is verified. If
   Cloudflare proxies the record (orange cloud), set its SSL/TLS mode to **Full**,
   not Full (Strict).
3. When the domain shows a green check, update `PUBLIC_BASE_URL` to it and
   point every POS terminal's `Commerce:CloudApiBaseUrl` at the same origin.

A client who later needs their own website gets another custom domain on this
same service, not another deployment. Cloud.Api does not yet resolve an
organization from the request host, so that client needs that feature first
(ADR-014).

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
service's private domain in both connection strings, and redeploy. The runtime
roles and their passwords come back with the data. Keep the original service
until the restored data is confirmed, then delete it.

To recover from a scheduled volume backup instead, use the **Backups** tab:
an in-place restore overwrites the current data.
