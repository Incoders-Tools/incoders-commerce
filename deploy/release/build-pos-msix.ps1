<#
.SYNOPSIS
  Publishes the POS self-contained, packs it as an MSIX and signs it.

.DESCRIPTION
  1. dotnet publish Commerce.Pos.Windows (win-x64, self-contained). The POS
     hosts the branch node in-process, so this is the only project to ship.
  2. Renders AppxManifest.template.xml; the Publisher is read from the PFX
     subject so the package and certificate can never disagree.
  3. MakeAppx pack, then SignTool sign (SHA256).
  4. Writes <OutputDir>\Commerce.Pos.Windows-<version>-win-x64.msix and prints
     its SHA256; also writes a build-info.json used by the release workflow.

  Version mapping (semver tag -> MSIX 4-part version, and manifest version):
    1.4.0            -> 1.4.0.0   (manifest "1.4.0")
    1.4.0-internal.3 -> 1.4.0.3   (manifest "1.4.0.3")
  The trailing number of a prerelease suffix becomes the 4th part, so
  ReleaseDiscovery's System.Version comparison orders internal builds.

.EXAMPLE
  $env:POS_SIGNING_PFX_PASSWORD = '...'
  pwsh deploy/release/build-pos-msix.ps1 -Version 1.4.0-internal.1 -Channel internal `
       -PfxPath C:\secure\incoders-commerce-interim.pfx -OutputDir .\artifacts
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateSet('stable', 'internal')][string]$Channel = 'internal',
    [Parameter(Mandatory = $true)][string]$PfxPath,
    [string]$PasswordEnvVar = 'POS_SIGNING_PFX_PASSWORD',
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [string]$IdentityName = 'Incoders.Commerce.Pos',
    [string]$PublisherDisplayName = 'Incoders',
    [string]$TimestampUrl = '',
    [string]$SdkBinPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Convert-ToPackageVersion([string]$SemVer) {
    $m = [regex]::Match($SemVer.TrimStart('v'), '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?(\+.*)?$')
    if (-not $m.Success) { throw "Version '$SemVer' is not a valid semver (expected 1.2.3 or 1.2.3-internal.4)." }
    $tail = [regex]::Match($m.Groups[4].Value, '(\d+)$')
    $rev = if ($tail.Success) { [int]$tail.Groups[1].Value } else { 0 }
    if ($rev -gt 65535) { throw "Prerelease number $rev exceeds the MSIX 4th-part limit (65535)." }
    $core = "$($m.Groups[1].Value).$($m.Groups[2].Value).$($m.Groups[3].Value)"
    return [pscustomobject]@{
        Msix     = "$core.$rev"
        Manifest = if ($m.Groups[4].Success) { "$core.$rev" } else { $core }
    }
}

function Find-SdkTool([string]$Name) {
    $roots = @()
    if ($SdkBinPath) { $roots += $SdkBinPath }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        $roots += Get-ChildItem $kits -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64' }
    }
    foreach ($root in $roots) {
        $candidate = Join-Path $root $Name
        if (Test-Path $candidate) { return $candidate }
    }
    throw "$Name not found. Install the Windows SDK or pass -SdkBinPath."
}

$password = [Environment]::GetEnvironmentVariable($PasswordEnvVar)
if ([string]::IsNullOrWhiteSpace($password)) { throw "Set the PFX password in the '$PasswordEnvVar' environment variable." }
if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }

$versions = Convert-ToPackageVersion $Version
$pfx = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $PfxPath, $password, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
$publisher = $pfx.Subject
$pfx.Dispose()

$makeAppx = Find-SdkTool 'makeappx.exe'
$signTool = Find-SdkTool 'signtool.exe'

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$work = Join-Path $OutputDir "work-$($versions.Msix)"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$layout = Join-Path $work 'layout'
New-Item -ItemType Directory -Force -Path (Join-Path $layout 'Assets') | Out-Null

Write-Host "==> dotnet publish (win-x64, self-contained) version $($versions.Manifest)"
& dotnet publish (Join-Path $repoRoot 'src\Commerce.Pos.Windows\Commerce.Pos.Windows.csproj') `
    -c Release -r win-x64 --self-contained true -o $layout `
    "-p:Version=$($versions.Manifest)" "-p:AssemblyVersion=$($versions.Msix)" "-p:FileVersion=$($versions.Msix)" `
    "-p:InformationalVersion=$($versions.Manifest)" -p:IncludeSourceRevisionInformation=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

Write-Host '==> Logo assets (derived from app.ico)'
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path (Join-Path $repoRoot 'src\Commerce.Pos.Windows') 'app.ico'
# Read the largest image straight from the ICO directory: PNG-compressed
# entries (typical for 256px) make System.Drawing.Icon.ToBitmap() throw.
$ico = [IO.File]::ReadAllBytes($iconPath)
$count = [BitConverter]::ToUInt16($ico, 4)
$best = $null
for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + 16 * $i
    $w = if ($ico[$o] -eq 0) { 256 } else { [int]$ico[$o] }
    $entry = [pscustomobject]@{ Width = $w; Size = [BitConverter]::ToUInt32($ico, $o + 8); Offset = [BitConverter]::ToUInt32($ico, $o + 12) }
    if ($null -eq $best -or $entry.Width -gt $best.Width) { $best = $entry }
}
$isPng = $ico[$best.Offset] -eq 0x89 -and $ico[$best.Offset + 1] -eq 0x50
if ($isPng) {
    $source = [System.Drawing.Bitmap]::new([IO.MemoryStream]::new($ico, [int]$best.Offset, [int]$best.Size))
}
else {
    $source = [System.Drawing.Icon]::new($iconPath, $best.Width, $best.Width).ToBitmap()
}
foreach ($asset in @(@('Square44x44Logo', 44), @('Square150x150Logo', 150), @('StoreLogo', 50))) {
    $size = $asset[1]
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose()
    $bmp.Save((Join-Path $layout "Assets\$($asset[0]).png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
$source.Dispose()

Write-Host '==> AppxManifest.xml'
$template = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.template.xml') -Raw
$xmlEscape = { param($s) [System.Security.SecurityElement]::Escape($s) }
$rendered = $template.
    Replace('{{IDENTITY_NAME}}', (& $xmlEscape $IdentityName)).
    Replace('{{PUBLISHER}}', (& $xmlEscape $publisher)).
    Replace('{{PUBLISHER_DISPLAY_NAME}}', (& $xmlEscape $PublisherDisplayName)).
    Replace('{{MSIX_VERSION}}', $versions.Msix)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $rendered, [Text.UTF8Encoding]::new($false))

$msixName = "Commerce.Pos.Windows-$($versions.Manifest)-win-x64.msix"
$msixPath = Join-Path $OutputDir $msixName
if (Test-Path $msixPath) { Remove-Item $msixPath -Force }

Write-Host '==> MakeAppx pack'
& $makeAppx pack /d $layout /p $msixPath /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)." }

Write-Host '==> SignTool sign (SHA256)'
$signArgs = @('sign', '/fd', 'SHA256', '/f', $PfxPath, '/p', $password)
if ($TimestampUrl) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
$signArgs += $msixPath
& $signTool @signArgs | Out-Host
if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)." }

$sha = (Get-FileHash $msixPath -Algorithm SHA256).Hash.ToLowerInvariant()
$info = [ordered]@{
    file            = $msixName
    version         = $versions.Manifest
    msixVersion     = $versions.Msix
    channel         = $Channel
    publisher       = $publisher
    sha256          = $sha
    sizeBytes       = (Get-Item $msixPath).Length
}
$info | ConvertTo-Json | Set-Content (Join-Path $OutputDir 'build-info.json') -Encoding utf8
Remove-Item $work -Recurse -Force

Write-Host ''
Write-Host "MSIX     : $msixPath"
Write-Host "SHA256   : $sha"
Write-Host "Publisher: $publisher"
