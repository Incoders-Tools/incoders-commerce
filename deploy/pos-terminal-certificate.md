# POS terminal: install the interim signing certificate

Needed once per terminal while releases are signed with the interim
self-signed certificate
([ADR-013](../docs/architecture/decisions/ADR-013-interim-self-signed-code-signing.md)).
Without it Windows refuses to install or update the POS MSIX.

You need the public file `incoders-commerce-interim.cer` (never the `.pfx`), the
certificate SHA-256 thumbprint printed by `new-dev-signing-cert.ps1`, and an
administrator account.

## Install

Open an elevated PowerShell on the terminal:

```powershell
Import-Certificate -FilePath .\incoders-commerce-interim.cer `
    -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

## Pin the certificate for the update wizard

Set the SHA-256 thumbprint printed when the certificate was created
(`SHA-256 : ...`) as a machine environment variable, then restart the POS:

```powershell
[Environment]::SetEnvironmentVariable(
    'Commerce__UpdateTrustedThumbprint', '<64 hex characters>', 'Machine')
```

Without the pin the update wizard refuses interim-signed packages, because
Windows reports a self-signed certificate as an untrusted root. To recompute it
from the `.cer`:

```powershell
$cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new('.\incoders-commerce-interim.cer')
[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData)).Replace('-', '')
```

Installing the `.cer` in `LocalMachine\TrustedPeople` (above) is only what lets
Windows install the MSIX; do **not** put it in `Trusted Root`.

## Verify

```powershell
Get-ChildItem Cert:\LocalMachine\TrustedPeople |
    Where-Object Subject -eq 'CN=Incoders Commerce (Interim)' |
    Format-List Subject, Thumbprint, NotAfter
```

Compare the thumbprint (SHA-1 in Windows tools) with the SHA-1 line printed by
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

## What the POS update wizard requires

The wizard verifies the downloaded package with the OS (`WinVerifyTrust`, the
same engine as `Get-AuthenticodeSignature`) and installs only when the status is
`Valid` or, when `Commerce__UpdateTrustedThumbprint` is set, `UntrustedRoot`
(the expected result for a self-signed certificate), and only when the signer
certificate SHA-256 equals that pin exactly. Without a pin only `Valid` with the
subject `Commerce:UpdateTrustedPublisher` (default
`CN=Incoders Commerce (Interim)`) passes. A missing certificate, a different
signer, a tampered package (`Invalid`) or an expired certificate stop at
verification with "Verificación fallida"; the wizard never installs an
untrusted package. Checked against a real signed MSIX without installing
anything: the untouched package is `UntrustedRoot` with the pin's thumbprint
(accepted with the right pin, refused with a wrong pin or no pin) and a package
with one byte flipped is `Invalid` (refused). Not yet validated on a terminal:
what `WinVerifyTrust` returns with the certificate in `TrustedPeople` (R6).
