# Launches the local dev stack in separate terminal windows:
# Postgres (Docker), a built Commerce.Web SPA hosted by Commerce.Cloud.Api,
# a local HTTPS proxy for browser auth-cookie testing, and Commerce.Pos.Windows
# (WPF). Run from anywhere; paths are resolved relative to this script.
#
# Usage: pwsh deploy/dev/run-all.ps1
#        pwsh deploy/dev/run-all.ps1 -NoPos      # skip the WPF POS window
#        pwsh deploy/dev/run-all.ps1 -NoWeb      # skip SPA build/copy and HTTPS proxy
#        pwsh deploy/dev/run-all.ps1 -NoApi      # skip launching Cloud.Api
#        pwsh deploy/dev/run-all.ps1 -NoBrowser  # do not open the login URL

param(
    [switch]$NoPos,
    [switch]$NoWeb,
    [switch]$NoApi,
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$webRoot = Join-Path $repoRoot "src\Commerce.Web"
$composeFile = Join-Path $PSScriptRoot "compose.yaml"

$commerceConnectionString = "Host=localhost;Port=5432;Database=commerce_dev;Username=app_runtime;Password=dev-only-password"
$platformReadConnectionString = "Host=localhost;Port=5432;Database=commerce_dev;Username=platform_readonly;Password=dev-only-platform-readonly-password"

function ConvertTo-PowerShellSingleQuotedLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + ($Value -replace "'", "''") + "'"
}

$repoRootLiteral = ConvertTo-PowerShellSingleQuotedLiteral $repoRoot
$webRootLiteral = ConvertTo-PowerShellSingleQuotedLiteral $webRoot
$commerceConnectionStringLiteral = ConvertTo-PowerShellSingleQuotedLiteral $commerceConnectionString
$platformReadConnectionStringLiteral = ConvertTo-PowerShellSingleQuotedLiteral $platformReadConnectionString

Write-Host "Repo root: $repoRoot"

# --- 1. Postgres (Docker) -------------------------------------------------
Write-Host "`nStarting local Postgres (deploy/dev/compose.yaml)..."
docker compose -f $composeFile up -d postgres
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed with exit code $LASTEXITCODE" }

Write-Host "Waiting for Postgres to be healthy..."
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    $status = docker inspect --format='{{.State.Health.Status}}' incoders-commerce-postgres-1 2>$null
    if ($status -eq "healthy") { $ready = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $ready) { throw "Postgres did not become healthy within 60s." }
Write-Host "Postgres is healthy."

# --- 2. Commerce.Web built into Commerce.Cloud.Api/wwwroot ---------------
if (-not $NoWeb) {
    Write-Host "`nPreparing Commerce.Web dependencies..."
    Push-Location $webRoot
    try {
        if (-not (Test-Path (Join-Path $webRoot "node_modules"))) {
            Write-Host "node_modules not found; running npm ci..."
            npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE" }
        }

        Write-Host "Building Commerce.Web and copying it into Commerce.Cloud.Api/wwwroot..."
        npm run test:e2e:build-backend-spa
        if ($LASTEXITCODE -ne 0) { throw "npm run test:e2e:build-backend-spa failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
}

# --- 3. Commerce.Cloud.Api -------------------------------------------------
if (-not $NoApi) {
    Write-Host "`nLaunching Commerce.Cloud.Api on http://localhost:8080 in a new window..."
    $apiCommand = @(
        "cd $repoRootLiteral",
        "Write-Host 'Commerce.Cloud.Api http://localhost:8080' -ForegroundColor Cyan",
        "`$env:ASPNETCORE_ENVIRONMENT = 'Development'",
        "`$env:ConnectionStrings__Commerce = $commerceConnectionStringLiteral",
        "`$env:ConnectionStrings__CommercePlatformRead = $platformReadConnectionStringLiteral",
        "`$env:PORT = '8080'",
        "dotnet run --project src/Commerce.Cloud.Api"
    ) -join "; "

    Start-Process pwsh -ArgumentList @("-NoExit", "-Command", $apiCommand)

    Write-Host "Waiting for Cloud.Api health on http://localhost:8080/health..."
    $apiReady = $false
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $response = Invoke-WebRequest -Uri "http://localhost:8080/health" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $apiReady = $true; break }
        }
        catch {
            Start-Sleep -Seconds 1
        }
    }
    if (-not $apiReady) { throw "Commerce.Cloud.Api did not become healthy on http://localhost:8080/health within 60s. Check the API window." }
    Write-Host "Cloud.Api is healthy."
}

# --- 4. HTTPS proxy for browser testing -----------------------------------
if (-not $NoWeb) {
    Write-Host "Launching HTTPS proxy https://localhost:5443 -> http://localhost:8080 in a new window..."
    $proxyCommand = @(
        "cd $webRootLiteral",
        "Write-Host 'HTTPS proxy https://localhost:5443 -> http://localhost:8080' -ForegroundColor Cyan",
        "npm exec -- local-ssl-proxy --source 5443 --target 8080"
    ) -join "; "

    Start-Process pwsh -ArgumentList @("-NoExit", "-Command", $proxyCommand)

    if (-not $NoBrowser) {
        Start-Sleep -Seconds 3
        Write-Host "Opening browser at https://localhost:5443/login..."
        Start-Process "https://localhost:5443/login"
    }
}

# --- 5. Commerce.Pos.Windows (WPF) ----------------------------------------
if (-not $NoPos) {
    Write-Host "Launching Commerce.Pos.Windows in a new window..."
    $posCommand = @(
        "cd $repoRootLiteral",
        "Write-Host 'Commerce.Pos.Windows' -ForegroundColor Cyan",
        "dotnet run --project src/Commerce.Pos.Windows"
    ) -join "; "

    Start-Process pwsh -ArgumentList @("-NoExit", "-Command", $posCommand)
}

Write-Host "`nAll requested processes launched."
Write-Host "Browser URL: https://localhost:5443/login"
Write-Host "API URL:     http://localhost:8080"
Write-Host "Close each process window to stop it; Postgres keeps running in Docker until you run:"
Write-Host "docker compose -f deploy/dev/compose.yaml down"
