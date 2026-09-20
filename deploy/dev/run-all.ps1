# Launches the full local dev stack in separate terminal windows:
# Postgres (Docker), Commerce.Cloud.Api, Commerce.Web (Vite dev server),
# and Commerce.Pos.Windows (WPF). Run from anywhere; paths are resolved
# relative to this script's location.
#
# Usage: pwsh deploy/dev/run-all.ps1
#        pwsh deploy/dev/run-all.ps1 -NoPos      # skip the WPF POS window
#        pwsh deploy/dev/run-all.ps1 -NoWeb       # skip the Web dev server

param(
    [switch]$NoPos,
    [switch]$NoWeb,
    [switch]$NoApi
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")

Write-Host "Repo root: $repoRoot"

# --- 1. Postgres (Docker) -------------------------------------------------
Write-Host "`nStarting local Postgres (deploy/dev/compose.yaml)..."
docker compose -f (Join-Path $PSScriptRoot "compose.yaml") up -d postgres
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

# --- 2. Commerce.Cloud.Api -------------------------------------------------
if (-not $NoApi) {
    Write-Host "`nLaunching Commerce.Cloud.Api in a new window..."
    Start-Process pwsh -ArgumentList @(
        "-NoExit", "-Command",
        "cd '$repoRoot'; Write-Host 'Commerce.Cloud.Api' -ForegroundColor Cyan; dotnet run --project src/Commerce.Cloud.Api"
    )
}

# --- 3. Commerce.Web (Vite dev server) ------------------------------------
if (-not $NoWeb) {
    Write-Host "Launching Commerce.Web dev server in a new window..."
    Start-Process pwsh -ArgumentList @(
        "-NoExit", "-Command",
        "cd '$repoRoot\src\Commerce.Web'; Write-Host 'Commerce.Web' -ForegroundColor Cyan; npm run dev"
    )
}

# --- 4. Commerce.Pos.Windows (WPF) ----------------------------------------
if (-not $NoPos) {
    Write-Host "Launching Commerce.Pos.Windows in a new window..."
    Start-Process pwsh -ArgumentList @(
        "-NoExit", "-Command",
        "cd '$repoRoot'; Write-Host 'Commerce.Pos.Windows' -ForegroundColor Cyan; dotnet run --project src/Commerce.Pos.Windows"
    )
}

Write-Host "`nAll requested processes launched in their own windows. Close each window to stop that process; Postgres keeps running in Docker until you 'docker compose -f deploy/dev/compose.yaml down'."
