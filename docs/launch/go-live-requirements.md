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

## Decide before go-live

- [ ] **MSI fallback (ADR-005).**
  Why: MSIX needs Windows 10 2004 (build 19041) or later. Confirm every terminal
  is above that floor; if any is not, build and sign an MSI as well. Currently
  deferred: releases are MSIX only.
- [ ] **Timestamp authority for signatures.**
  Why: untimestamped signatures expire with the certificate.

## Done

Nothing yet.
