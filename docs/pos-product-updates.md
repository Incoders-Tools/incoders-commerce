# POS product updates and release policy

## Status

Proposed for issue #69.

This policy extends the safety decisions in:

- [ADR-004: Release signing, provenance, and safe upgrades](./architecture/decisions/ADR-004-release-signing-and-upgrades.md)
- [ADR-005: Windows administrative floor and packaging](./architecture/decisions/ADR-005-signing-and-windows-fleet.md)

It does not authorize public release publication by itself. Publication remains gated by repository release authorization.

## Goal

Installed Windows POS terminals must be able to detect, explain, and safely install product updates without interrupting sales or corrupting local branch state.

The operator-facing entry point is the existing POS footer/settings area that currently shows local version/update status.

## Non-goals for the first implementation slice

- Silent forced updates.
- Updating from raw commits on `main`.
- Installing an unverified package.
- Blocking offline sales because update detection failed.
- Supporting incompatible Windows machines by bypassing package requirements.

## Source of truth

Updates come from immutable, versioned release artifacts, not from branch tips.

Branches have these roles:

| Source | Purpose | Release channel |
| --- | --- | --- |
| `dev` | Integration builds for local/internal validation | `internal` |
| `staging` | Pilot validation when introduced | `pilot` |
| `main` | Stable releasable source | `stable` |
| GitHub Release | Downloadable product version with assets and manifest | channel declared by release metadata |

A merge to `main` makes code eligible for a stable release. It does not by itself make a terminal update available. A release becomes available only when the release pipeline publishes an immutable manifest and signed artifacts.

## Release artifact set

Each release should publish one manifest plus the package artifacts it references.

Minimum release assets:

- `commerce-pos-release-manifest.json`
- MSIX package for machines that meet the MSIX floor.
- Signed MSI package fallback for machines that require MSI.
- SHA256 checksums for every package.
- Signature/attestation evidence required by ADR-004.

The manifest is the only input the POS updater uses for update availability and compatibility decisions.

## Manifest shape

Initial manifest schema:

```json
{
  "schemaVersion": 1,
  "product": "Commerce.Pos.Windows",
  "channel": "stable",
  "version": "1.4.0",
  "releaseNotesUrl": "https://github.com/Incoders-Tools/incoders-commerce/releases/tag/v1.4.0",
  "publishedAtUtc": "2026-01-31T12:00:00Z",
  "minimumAppVersion": "1.3.0",
  "compatibility": {
    "minimumWindowsBuild": 19041,
    "architectures": ["x64"],
    "requiresAdministrator": true,
    "syncContractVersion": 1,
    "schemaVersion": 1
  },
  "packages": [
    {
      "format": "msix",
      "architecture": "x64",
      "minimumWindowsBuild": 19041,
      "url": "https://github.com/Incoders-Tools/incoders-commerce/releases/download/v1.4.0/Commerce.Pos.Windows-1.4.0-win-x64.msix",
      "sha256": "<sha256>",
      "publisherId": "<publisher>",
      "signatureRequired": true,
      "attestationRequired": true
    },
    {
      "format": "msi",
      "architecture": "x64",
      "minimumWindowsBuild": 0,
      "url": "https://github.com/Incoders-Tools/incoders-commerce/releases/download/v1.4.0/Commerce.Pos.Windows-1.4.0-win-x64.msi",
      "sha256": "<sha256>",
      "publisherId": "<publisher>",
      "signatureRequired": true,
      "attestationRequired": true
    }
  ]
}
```

Manifest versioning rules:

- Additive fields are allowed under the same `schemaVersion`.
- Breaking manifest changes require incrementing `schemaVersion`.
- A terminal that cannot understand the manifest schema treats the update state as `incompatible`, not as installable.

## Compatibility decision

Before showing an install action, the terminal compares:

- current installed app version,
- current Windows build,
- process architecture / OS architecture,
- administrator capability requirement,
- package format support,
- sync contract version,
- schema version,
- publisher and attestation requirements.

Decision outcomes:

| Outcome | UI meaning |
| --- | --- |
| `upToDate` | The terminal is already on the latest compatible version. |
| `available` | A compatible update can be installed. |
| `incompatibleWindows` | A release exists but this Windows build cannot install it. |
| `incompatibleArchitecture` | A release exists but this machine architecture is unsupported. |
| `requiresAdministrator` | Update is compatible but needs elevation/admin flow. |
| `unsupportedSchema` | The manifest is newer than this updater can understand. |
| `untrusted` | Signature, hash, publisher, or attestation cannot be verified. |
| `checkFailed` | Network or endpoint result was inconclusive. Sales continue. |

`checkFailed` never means “no update”. It means “cannot determine safely”.

## UI policy

The POS footer remains compact and non-blocking.

Footer examples:

- `Versión 1.3.2 · Actualizado`
- `Versión 1.3.2 · Update 1.4.0 disponible ⬆`
- `Versión 1.3.2 · No se pudo comprobar updates`
- `Versión 1.3.2 · Update requiere Windows 10 2004+`

The footer may expose a very small upgrade icon/button only when a compatible update is available or an admin-required compatible update can be attempted.

Clicking the icon opens an update wizard. The wizard owns detail and risk communication; the footer does not become a large update panel.

## Update wizard stages

The wizard is explicit and never silent: the operator opens it from the footer
indicator ("Actualización X disponible") and starts it with "Instalar
actualización". The stages live in `UpdateInstallWorkflow`
(`src/Commerce.Updater`); the WPF window (`UpdateWizardWindow`) only shows
progress and the typed result.

1. Show current version, target version, package format, size, and compatibility.
2. Download to a controlled staging directory (`<data>/updates`, override
   `Commerce:UpdateStagingDirectory`). Only files the downloader owns
   (`Commerce.Pos.Windows-*.msix` and `.msix.partial`) are removed first; any
   other file in the directory is left alone. A staged file that already
   matches the manifest hash is reused. HTTP range resume is not implemented
   (a retry restarts the download). A connection that delivers no bytes for 60
   seconds fails as a typed download failure (not a cancel); the operator's
   cancel is reported at the stage where it happened.
3. Verify: SHA256 against the manifest, then the package signature (OS
   `WinVerifyTrust`) and the signer subject against the CONFIGURED trusted
   publisher (`Commerce:UpdateTrustedPublisher`, default the interim
   `CN=Incoders Commerce (Interim)`; the manifest `publisherId` must match it
   too). With `Commerce:UpdateTrustedThumbprint` set (SHA-256 of the signing
   certificate) the signer must equal it and `Valid` or `UntrustedRoot` passes;
   without a pin only `Valid` with the trusted subject passes (ADR-013).
   Attestation is not verifiable yet, so
   a package that requires it is refused. Any mismatch aborts before install
   and deletes the staged file.
4. Quiesce: refused while a sale is being built (the cart holds lines); durable
   work is checked through the existing `IBranchNodeQuiescence`.
5. When the POS is not running as a packaged app (dev run), stop here with a
   clear message: download and verification completed, nothing was installed.
6. Back up `branch.db` with the existing verified SQLite backup
   (`<data>/upgrade-backups`). The backup is kept if the install fails.
7. Register the process for restart (`RegisterApplicationRestart`), record the
   pending upgrade (`pending-upgrade.json`) and install through
   `PackageManager.AddPackageAsync(..., ForceApplicationShutdown)`. Windows
   closes the POS, applies the update and relaunches it.
8. On the next start the POS compares the running version with the recorded
   target and shows "Actualización a la versión X completada" or "no se
   completó" (with the backup path) in the footer.

If any pre-install verification fails, no upgrade starts. Failure reasons are
typed (`UpdateFailureReason`): hash mismatch, signature invalid, signer or
publisher mismatch, sale in progress, terminal busy, not packaged, backup
failed, install failed, download failed, cancelled.

Update checks call the public GitHub Releases API (`stable` = non-prerelease
releases, `internal` also prereleases; `Commerce:UpdateChannel`,
`Commerce:UpdateRepository`) off the UI thread with short timeouts; any
failure is the typed "could not check" outcome and never blocks sales.
`Commerce:UpdateManifestPath` (a local manifest file) wins when set. Settings
shows the full status and has a "Buscar actualizaciones" action.

Configuration summary:

| Key | Default | Purpose |
| --- | --- | --- |
| `Commerce:UpdateChannel` | `stable` | `stable` or `internal` |
| `Commerce:UpdateRepository` | `Incoders-Tools/incoders-commerce` | GitHub repository to read releases from |
| `Commerce:UpdateManifestPath` | unset | Local manifest override (VM testing) |
| `Commerce:UpdateTrustedPublisher` | `CN=Incoders Commerce (Interim)` | Required signer subject (ADR-013 swap is config-only) |
| `Commerce:UpdateTrustedThumbprint` | unset | SHA-256 of the trusted signing certificate; required for interim-signed updates (ADR-013) |
| `Commerce:UpdateStagingDirectory` | `<data>/updates` | Where packages are staged |

Research (installing the update of the running app):

- `PackageManager.AddPackageAsync` accepts only `None` and
  `ForceApplicationShutdown`, takes a `file://` package URI and needs the
  `packageManagement` restricted capability
  ([API reference](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.addpackageasync));
  the MSIX manifest template declares it.
- For a non-UWP packaged app `RegisterApplicationRestart` must be called before
  shutdown begins to get the relaunch
  ([Update non-Store published apps from your code](https://learn.microsoft.com/en-us/windows/msix/non-store-developer-updates)).
- The POS targets `net10.0-windows10.0.19041.0` so the WinRT projection is
  available.

## Rollout policy

Large-product pattern recommended for this project:

1. **Internal**: every releasable `dev` build can publish an internal manifest for VM/manual validation.
2. **Pilot**: selected store/terminal cohort validates a candidate from `staging` when the project introduces staging.
3. **Stable**: `main` release becomes available to production terminals only after a GitHub Release is published and authorized.
4. **Progressive rollout**: stable manifest can advertise rollout percentage or allowlist cohorts later.
5. **Rollback**: never overwrite an existing release. Publish a newer version that rolls forward to the desired state, unless ADR-004 typed rollback path is explicitly required on the terminal.

## Installed VM test loop

The project should validate updates in two modes:

1. **Local dev mode**: run from source using `deploy/dev/run-all.ps1` for fast iteration.
2. **Installed terminal mode**: install a packaged POS in a Windows VM and validate the same flow a real customer sees.

Installed VM validation checklist:

- Install baseline version.
- Pair terminal and complete at least one local sale.
- Publish or host a test manifest for a newer compatible version.
- Confirm footer detects the update.
- Run wizard through download and verification.
- Confirm upgrade preserves local branch state and pending operations.
- Confirm incompatible manifest variants are rejected with clear messages.
- Confirm offline/no-network update checks do not block sales.

## First implementation slice

Implement detection before installation:

1. Add explicit POS version metadata.
2. Add an update manifest model and parser.
3. Add a manifest source abstraction:
   - local file source for VM testing,
   - HTTP/GitHub release source later.
4. Add compatibility evaluation with typed outcomes.
5. Wire footer/settings to show local version and update status.
6. Add tests for version comparison, package selection, and incompatible Windows/architecture outcomes.

The install wizard should be implemented after detection is reliable and visible on the installed VM path.

## Decisions and open questions

Decided (owner and parent defaults, 2026-09-30; see
[ADR-013](./architecture/decisions/ADR-013-interim-self-signed-code-signing.md)
and `odd/tasks/product-update-service.md`):

| Question | Decision |
| --- | --- |
| Release source | GitHub Releases, called directly by the POS. The repository is public, so no credentials are needed. A first-party mirror API stays a later option. |
| Availability scope | Global per product channel (`stable` from `main`, `internal` from prereleases cut on `dev`), not per organization or branch. |
| Package technology | MSIX first. The signed-MSI fallback of ADR-005 stays a follow-up and becomes a go-live item only if a terminal below Windows 10 2004 appears. |
| Signing | Interim self-signed certificate installed once per terminal; a commercial certificate is a go-live requirement. Swapping it needs no code change (ADR-013). |
| How a release is cut | Push a `v<semver>` tag or run `pos-release.yml` manually; the tag is the version source. Scripts: `deploy/release/`. |
| Terminal trust | [Terminal certificate runbook](../deploy/pos-terminal-certificate.md). |
| Launch blockers | [Go-live requirements](./launch/go-live-requirements.md). |

Still open:

- How administrator authorization is represented in the UI when an update needs elevation: the wizard installs per user through `AddPackageAsync` and shows no elevation flow; whether that succeeds for a machine-wide package on a standard-user account is validated on the installed VM.
- How the Windows Service branch node is packaged if it is split from the POS process; today the POS hosts it in-process.
- Whether packaged-app file virtualization affects `%LocalAppData%` data such as `branch.db`, the staged package (the deployment service must be able to read it) and the backup (validate on the installed VM; `Commerce:UpdateStagingDirectory` is the escape hatch).
