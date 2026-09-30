<#
.SYNOPSIS
  Generates commerce-pos-release-manifest.json for one release.

.DESCRIPTION
  Reads the build-info.json written by build-pos-msix.ps1 and emits the
  manifest schema consumed by Commerce.Updater's ReleaseDiscovery (schema 1;
  see docs/pos-product-updates.md). The package URL is the GitHub Release
  asset download URL for the tag. Covered by
  tests/Commerce.Upgrade/ReleaseManifestGeneratorTests.cs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BuildInfoPath,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [int]$MinimumWindowsBuild = 19041,
    [int]$SyncContractVersion = 1,
    [int]$SchemaVersion = 1
)

$ErrorActionPreference = 'Stop'
$info = Get-Content $BuildInfoPath -Raw | ConvertFrom-Json
foreach ($field in 'file', 'version', 'channel', 'publisher', 'sha256') {
    if ([string]::IsNullOrWhiteSpace($info.$field)) { throw "build-info.json is missing '$field'." }
}

$base = "https://github.com/$Repository/releases"
$manifest = [ordered]@{
    schemaVersion   = 1
    product         = 'Commerce.Pos.Windows'
    channel         = $info.channel
    version         = $info.version
    releaseNotesUrl = "$base/tag/$Tag"
    publishedAtUtc  = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    compatibility   = [ordered]@{
        minimumWindowsBuild   = $MinimumWindowsBuild
        architectures         = @('x64')
        requiresAdministrator = $true
        syncContractVersion   = $SyncContractVersion
        schemaVersion         = $SchemaVersion
    }
    packages        = @(
        [ordered]@{
            format              = 'msix'
            architecture        = 'x64'
            minimumWindowsBuild = $MinimumWindowsBuild
            url                 = "$base/download/$Tag/$($info.file)"
            sha256              = $info.sha256
            sizeBytes           = [long]$info.sizeBytes
            publisherId         = $info.publisher
            signatureRequired   = $true
            attestationRequired = $false
        }
    )
}

$json = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $OutputPath"
