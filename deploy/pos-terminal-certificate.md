# POS terminal: install the interim signing certificate

Needed once per terminal while releases are signed with the interim
self-signed certificate
([ADR-013](../docs/architecture/decisions/ADR-013-interim-self-signed-code-signing.md)).
Without it Windows refuses to install or update the POS MSIX.

You need the public file `incoders-commerce-interim.cer` (never the `.pfx`) and
an administrator account.

## Install

Open an elevated PowerShell on the terminal:

```powershell
Import-Certificate -FilePath .\incoders-commerce-interim.cer `
    -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

## Verify

```powershell
Get-ChildItem Cert:\LocalMachine\TrustedPeople |
    Where-Object Subject -eq 'CN=Incoders Commerce (Interim)' |
    Format-List Subject, Thumbprint, NotAfter
```

Compare the thumbprint with the one printed by
`deploy/release/new-dev-signing-cert.ps1` when the certificate was created.
To check a package before installing it:

```powershell
Get-AuthenticodeSignature .\Commerce.Pos.Windows-<version>-win-x64.msix
```

`Status` is `Valid` once the certificate is installed. `UnknownError` with
"root certificate which is not trusted" means the certificate is missing.

## Remove (after the commercial certificate is adopted)

```powershell
Get-ChildItem Cert:\LocalMachine\TrustedPeople |
    Where-Object Subject -eq 'CN=Incoders Commerce (Interim)' |
    Remove-Item
```

Switching certificates changes the package publisher and needs a one-time
reinstall per terminal (see ADR-013).

## Why `TrustedPeople`

Assumption, not yet validated on a terminal: Microsoft's MSIX signing guidance
says a package signed with a self-signed certificate installs once the
certificate is in the machine's **Trusted People** store (Trusted Root also
works but grants far more trust and is not recommended). The Microsoft
documentation could not be fetched while this runbook was written; confirm the
behavior during installed VM validation (go-live requirements).
