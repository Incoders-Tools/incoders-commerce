# Go-live requirements

Living checklist of what must be true before terminals run this product in
production. Add items as they are discovered; tick them with evidence (date and
link) rather than deleting them.

## Blocking

- [ ] **Commercial code-signing certificate.**
  Why: the interim self-signed certificate
  ([ADR-013](../architecture/decisions/ADR-013-interim-self-signed-code-signing.md))
  makes every terminal install a certificate by hand and offers no third-party
  trust. Decide the provider (for example Azure Trusted Signing), set the new
  PFX or credentials in the release secrets, and plan the one-time
  package-identity migration described in ADR-013.
- [ ] **Release secrets configured.**
  Why: `pos-release.yml` fails without `POS_SIGNING_PFX_BASE64` and
  `POS_SIGNING_PFX_PASSWORD`. Also flip `publication_authorized` for the channel
  in `.github/release-authorization.yml` (ADR-004).
- [ ] **Installed VM validation.**
  Why: unit tests cannot prove that a packaged POS installs, pairs, sells,
  preserves `branch.db` across an update, and rejects tampered or incompatible
  packages. Follow the checklist in
  [POS product updates](../pos-product-updates.md#installed-vm-test-loop) and
  include the packaged-app file-virtualization check for `%LocalAppData%` data.
- [ ] **Terminals trust the signing certificate.**
  Why: without the certificate in `LocalMachine\TrustedPeople`, the MSIX will not
  install. Runbook: [terminal certificate](../../deploy/pos-terminal-certificate.md).
  Not needed once the commercial certificate is in place.
- [ ] **Terminals pin the signing certificate.**
  Why: the update wizard accepts an interim-signed package only when
  `Commerce__UpdateTrustedThumbprint` (SHA-256 printed by
  `new-dev-signing-cert.ps1`) is set on the terminal; without it every interim
  update is refused. Replace or clear the pin together with
  `Commerce:UpdateTrustedPublisher` when the commercial certificate arrives
  ([ADR-013](../architecture/decisions/ADR-013-interim-self-signed-code-signing.md)).

- [ ] **Railway production provisioned.**
  Why: Cloud.Api and its database run on Railway Pro
  ([ADR-014](../architecture/decisions/ADR-014-railway-hosted-postgres.md)).
  Follow [the Railway runbook](../../deploy/railway-runbook.md) sections 1-9,
  staging first: usage alert at $20, Postgres without public access, migrations
  applied, Cloud.Api as `app_runtime`, and `/health/ready` answering 200.
- [ ] **Domain registered and mapped.**
  Why: password-reset links and POS terminals need the final origin
  ([ADR-006](../architecture/decisions/ADR-006-domain-and-subdomain-topology.md)).
  `vaca-verde.com.ar` is registered (2026-10-16). Delegate its DNS to Cloudflare,
  map `app.` and `staging.` to Cloud.Api and the apex to the public site, verify
  it in Resend, and set `PUBLIC_BASE_URL` (runbook section 6).
- [ ] **Backups enabled and a restore drill passed.**
  Why: the client's data needs a tested restore point. Scheduled backups, PITR,
  and the first off-Railway `pg_dump` must be in place in production, and a
  point-in-time restore must have been rehearsed in staging (runbook sections 7
  and 10). Record the drill's date and result here.

## Decide before go-live

- [x] **Production platform system administrator.**
  Why: `/account/bootstrap` creates the organization and its `business-admin`
  in any environment, but `users.is_system_admin` was only set by the
  Development test seed. Resolved 2026-10-17 (railway-deploy-pipeline S12):
  production has one, created from the Cloud.Api service console with
  `dotnet Commerce.Cloud.Api.dll create-system-admin --email <address>`, with no
  HTTP endpoint. The same command resets its password (runbook section 8).
- [ ] **Multi-factor authentication for system administrators** (recommended).
  Why: a system administrator can create organizations and act on every
  organization, and today a password alone protects the account. Sign-in
  attempts are limited per client and per account (5 per 15 minutes per
  account from one address, 100 from all addresses), which slows guessing but
  does not stop a leaked password. Add a
  second factor (for example TOTP) for `is_system_admin` accounts at least.

- [ ] **MSI fallback (ADR-005).**
  Why: MSIX needs Windows 10 2004 (build 19041) or later. Confirm every terminal
  is above that floor; if any is not, build and sign an MSI as well. Currently
  deferred: releases are MSIX only.
- [ ] **Timestamp authority for signatures.**
  Why: untimestamped signatures expire with the certificate.

## Done

Nothing yet.

## Status

2026-09-30: no release has been published. The owner put the first internal
release on hold until the product reaches beta readiness; this checklist
stays open until then.

