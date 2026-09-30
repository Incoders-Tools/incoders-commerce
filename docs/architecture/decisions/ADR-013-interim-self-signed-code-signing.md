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
| Terminal trust (MSIX install) | Public `.cer` installed once per terminal, as administrator, in `LocalMachine\TrustedPeople` | Not needed; the OS trusts the public chain |
| Updater trust (POS wizard) | Signer pinned by SHA-256 thumbprint: `Commerce:UpdateTrustedThumbprint` on every terminal | Change or clear the pin and `Commerce:UpdateTrustedPublisher` |

## Trust in the POS update wizard: the thumbprint pin

Windows reports a self-signed certificate that is only in `TrustedPeople` as an
untrusted root when the POS asks `WinVerifyTrust` about a downloaded package
(observed for a certificate that was not installed; the same status is expected
until R6 proves otherwise on a terminal). Refusing that status would refuse
every interim-signed update, and trusting it by subject text alone would let
anyone who creates a certificate with the same subject through.

The wizard therefore decides trust like this:

| Signature status | Pin configured (`Commerce:UpdateTrustedThumbprint`) | No pin |
| --- | --- | --- |
| `Valid` | accepted only if the signer certificate SHA-256 equals the pin | accepted only if the signer subject equals `Commerce:UpdateTrustedPublisher` |
| `UntrustedRoot` | accepted only if the signer certificate SHA-256 equals the pin | refused |
| `NotSigned`, `Invalid`, `Expired`, `Unknown` | refused | refused |

- The pin is the **SHA-256** of the signing certificate (uppercase hex, colons
  and spaces tolerated), printed by `deploy/release/new-dev-signing-cert.ps1`.
  SHA-256 is used instead of the SHA-1 that Windows tools display because a
  pin is a security decision. A value that is not 64 hex characters (for
  example a SHA-1) is refused up front, never silently unmatched.
- `UntrustedRoot` cannot be produced by a tampered package: modifying any byte
  makes `WinVerifyTrust` fail the digest check and return `Invalid`, which is
  always refused (checked against a real signed MSIX with one byte flipped at
  three offsets). Expiry is its own status and is refused, never folded into
  `UntrustedRoot`. The SHA256 in the release manifest is also checked first.
- The certificate stays in `LocalMachine\TrustedPeople` only so Windows will
  install the MSIX. It does **not** need to be in `Trusted Root`, which would
  trust everything that certificate signs for the whole machine.
- **Swapping to the commercial certificate** is a configuration change: set
  `Commerce:UpdateTrustedPublisher` to the new subject and either set the pin to
  the new certificate's SHA-256 or clear it (a publicly trusted chain yields
  `Valid`, which the subject check accepts). Rotating the interim certificate
  is a new pin on every terminal together with the new `.cer`.

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
  manifest `publisherId` with the terminal's trusted-publisher setting
  (`Commerce:UpdateTrustedPublisher`) and the signer of a downloaded package
  with the pin (`Commerce:UpdateTrustedThumbprint`) or, without a pin, with that
  same subject. Changing certificates changes configuration, not code. During the
  interim period the pin must be set on every terminal; without it the wizard
  refuses interim-signed packages.
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
