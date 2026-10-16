<#
.SYNOPSIS
  Creates the INTERIM self-signed code-signing certificate (ADR-013).

.DESCRIPTION
  Writes a password-protected PFX (private key, for the release pipeline) and
  a .cer (public key, for terminals) to -OutputDir. The certificate is created
  in CurrentUser\My only long enough to export it, then removed, so nothing
  stays in any machine or user store.

  Prints the certificate SHA-256 thumbprint. Set it on every terminal as
  Commerce__UpdateTrustedThumbprint (config key Commerce:UpdateTrustedThumbprint):
  the POS update wizard then accepts only a package signed by exactly this
  certificate (ADR-013).

  Never commit the output. *.pfx and *.cer are git-ignored; keep the PFX in a
  password manager and in the POS_SIGNING_PFX_BASE64 repository secret only.

.EXAMPLE
  $env:POS_SIGNING_PFX_PASSWORD = '<strong password>'
  pwsh deploy/release/new-dev-signing-cert.ps1 -OutputDir C:\secure\incoders-signing
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [string]$Subject = 'CN=Incoders Commerce (Interim)',
    [string]$PasswordEnvVar = 'POS_SIGNING_PFX_PASSWORD',
    [int]$ValidYears = 3,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$password = [Environment]::GetEnvironmentVariable($PasswordEnvVar)
if ([string]::IsNullOrWhiteSpace($password)) {
    throw "Set the PFX password in the '$PasswordEnvVar' environment variable first."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$pfxPath = Join-Path $OutputDir 'incoders-commerce-interim.pfx'
$cerPath = Join-Path $OutputDir 'incoders-commerce-interim.cer'
if (((Test-Path $pfxPath) -or (Test-Path $cerPath)) -and -not $Force) {
    throw "Certificate files already exist in '$OutputDir'. Pass -Force to replace them."
}

# 1.3.6.1.5.5.7.3.3 = Code Signing EKU; CA:false basic constraint.
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $Subject `
    -FriendlyName 'Incoders Commerce (Interim) code signing' `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

# SHA-256 of the DER certificate: the value the POS pins. (Windows tools show
# the SHA-1 "Thumbprint"; the POS deliberately pins the stronger SHA-256.)
$sha256 = [System.BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData)).Replace('-', '')

try {
    $secure = ConvertTo-SecureString -String $password -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
}
finally {
    Remove-Item -Path ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -Force -ErrorAction SilentlyContinue
}

Write-Host "Subject    : $($cert.Subject)"
Write-Host "Thumbprint : $($cert.Thumbprint)   (SHA-1, as shown by Windows tools)"
Write-Host "SHA-256    : $sha256"
Write-Host "Pin        : Commerce__UpdateTrustedThumbprint=$sha256   (set on every terminal)"
Write-Host "Expires    : $($cert.NotAfter.ToString('u'))"
Write-Host "PFX        : $pfxPath   (private; secret POS_SIGNING_PFX_BASE64)"
Write-Host "CER        : $cerPath   (public; install on terminals, see deploy/pos-terminal-certificate.md)"
Write-Host ""
Write-Host "Secret value: [Convert]::ToBase64String([IO.File]::ReadAllBytes('$pfxPath'))"
