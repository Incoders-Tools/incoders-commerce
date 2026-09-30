# ADR-013: Interim self-signed code signing

## Status

Accepted (owner decision, 2026-09-30). Amends the signing expectations of
[ADR-004](./ADR-004-release-signing-and-upgrades.md) and
[ADR-005](./ADR-005-signing-and-windows-fleet.md) until a commercial
certificate replaces it.

## Decision

Release MSIX packages are signed with a **self-signed code-signing certificate**
(`CN=Incoders Commerce (Interim)`) until go-live. A commercial, publicly trusted
certificate (for example Azure Trusted Signing) is a **go-live requirement**
tracked in [go-live requirements](../../launch/go-live-requirements.md).

The swap must be a configuration change, not a code change:

| Element | Interim | After the swap |
| --- | --- | --- |
| Signing material | PFX in secrets `POS_SIGNING_PFX_BASE64` + `POS_SIGNING_PFX_PASSWORD` | New PFX (or signing-service credentials) in the same secrets |
| Publisher identity | Read from the PFX subject by `deploy/release/build-pos-msix.ps1` | Same script, new subject |
| Manifest `publisherId` | Written by `deploy/release/new-release-manifest.ps1` from the build | Same |
| Terminal trust | Public `.cer` installed once per terminal, as administrator | Not needed; the OS trusts the public chain |

## Why

- The owner wants to start cutting installable releases now; a commercial
  certificate is a purchase with an identity-validation lead time.
- The fleet is small and administrator-managed (ADR-005 already requires an
  administrator to install), so a one-time per-terminal certificate install is
  acceptable in the interim.
- Signing and publisher verification stay mandatory (ADR-004): the interim
  certificate weakens *who vouches for the publisher*, not *whether the
  updater verifies*.

## Consequences

- **Per-terminal install.** Every terminal needs the public certificate in the
  `LocalMachine\TrustedPeople` store before an MSIX from this pipeline installs.
  See the [terminal runbook](../../../deploy/pos-terminal-certificate.md).
- **Publisher must equal the certificate subject.** The MSIX `Identity/Publisher`
  must match the signing certificate subject exactly or Windows rejects the
  package. The build script derives it from the PFX so the two cannot drift.
- **Updater publisher check is configuration-driven.** The updater compares the
  Authenticode signer subject of a downloaded package with the manifest
  `publisherId` and with the terminal's trusted-publisher setting
  (`Commerce:UpdateTrustedPublisher`, implemented in the install wizard as configuration, not a constant). Changing
  certificates changes that value, not code.
- **Publisher change is a package-identity change.** Windows treats a different
  publisher as a different package family, so an in-place upgrade from the
  interim package to one signed by the commercial certificate is not possible.
  The swap needs a one-time migration per terminal: back up `branch.db`,
  uninstall, install the new package, restore. Cutting over before terminals go
  live avoids the migration; plan for it otherwise.
- **No timestamp.** Interim signatures are not timestamped and stop validating
  when the certificate expires (3 years). `build-pos-msix.ps1` accepts
  `-TimestampUrl` once a timestamp authority is chosen.
- **Private key handling.** The PFX is never committed (`*.pfx` is git-ignored)
  and lives only in a password manager and the repository secret. Rotation is a
  new PFX plus a new `.cer` on terminals.
- **Removal.** After the commercial switch, remove the interim certificate from
  every terminal (see the runbook, "Remove").
