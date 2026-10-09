# Launches the local dev stack in separate terminal windows:
# Postgres (Docker), a built Commerce.Web SPA hosted by Commerce.Cloud.Api,
# a local HTTPS proxy for browser auth-cookie testing, and Commerce.Pos.Windows
# (WPF). Run from anywhere; paths are resolved relative to this script.
# For a double-click entry point that also checks tooling, starts Docker
# Desktop and refreshes dependencies, use deploy/dev/run-all.bat.
#
# Each port is a PREFERENCE, not a requirement: when it is already taken (a
# native Postgres on 5432, another app on 8080, a previous API window left
# open), the next free port is used instead and every consumer -- connection
# strings, proxy target, POS, browser URL -- is pointed at it. A Postgres
# container from this compose project that is already running keeps the port
# it was published on.
#
# Usage: pwsh deploy/dev/run-all.ps1
#        pwsh deploy/dev/run-all.ps1 -NoPos      # skip the WPF POS window
#        pwsh deploy/dev/run-all.ps1 -NoWeb      # skip SPA build/copy and HTTPS proxy
#        pwsh deploy/dev/run-all.ps1 -NoApi      # skip launching Cloud.Api (uses -ApiPort as-is)
#        pwsh deploy/dev/run-all.ps1 -NoProvision # skip ensuring the local sign-in accounts
#        pwsh deploy/dev/run-all.ps1 -NoBrowser  # do not open the login URL
#        pwsh deploy/dev/run-all.ps1 -ApiPort 9080 -ProxyPort 9443 -PostgresPort 15432

param(
    [switch]$NoPos,
    [switch]$NoWeb,
    [switch]$NoApi,
    [switch]$NoBrowser,
    [switch]$NoProvision,
    [int]$PostgresPort = 5432,
    [int]$ApiPort = 8080,
    [int]$ProxyPort = 5443
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$webRoot = Join-Path $repoRoot "src\Commerce.Web"
$composeFile = Join-Path $PSScriptRoot "compose.yaml"
$postgresContainer = "incoders-commerce-postgres-1"

# How many ports after the preferred one are tried before giving up.
$portSearchRange = 20

function ConvertTo-PowerShellSingleQuotedLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + ($Value -replace "'", "''") + "'"
}

# A port counts as taken when something is listening on it, or when it cannot
# be bound at all. The bind probe also catches ports Windows reserves for
# Hyper-V/WSL (`netsh interface ipv4 show excludedportrange protocol=tcp`),
# which have no listener but still refuse every bind.
function Test-PortFree {
    param([Parameter(Mandatory = $true)][int]$Port)

    if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
        return $false
    }

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $Port)
    try {
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        $listener.Stop()
    }
}

function Get-PortOwnerDescription {
    param([Parameter(Mandatory = $true)][int]$Port)

    $connection = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $connection) { return "reserved by Windows or not bindable" }

    $process = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
    if ($process) { return "$($process.ProcessName), PID $($process.Id)" }
    return "PID $($connection.OwningProcess)"
}

function Resolve-FreePort {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$Preferred,
        [int[]]$Exclude = @()
    )

    for ($candidate = $Preferred; $candidate -le $Preferred + $portSearchRange; $candidate++) {
        if ($Exclude -contains $candidate) { continue }
        if (-not (Test-PortFree $candidate)) { continue }

        if ($candidate -ne $Preferred) {
            Write-Warning "$Name port $Preferred is in use ($(Get-PortOwnerDescription $Preferred)); using $candidate instead."
        }
        return $candidate
    }

    throw "$Name`: no free port between $Preferred and $($Preferred + $portSearchRange)."
}

# Host port the running Postgres container of this compose project is
# published on, or $null when it is not running.
function Get-RunningPostgresPort {
    $state = docker inspect --format '{{.State.Running}}' $postgresContainer 2>$null
    if ($LASTEXITCODE -ne 0 -or $state -ne "true") { return $null }

    $mapping = docker port $postgresContainer 5432/tcp 2>$null | Select-Object -First 1
    if ($mapping -match ':(\d+)$') { return [int]$Matches[1] }
    return $null
}

Write-Host "Repo root: $repoRoot"

# --- 1. Postgres (Docker) -------------------------------------------------
$runningPostgresPort = Get-RunningPostgresPort
if ($runningPostgresPort) {
    $resolvedPostgresPort = $runningPostgresPort
    Write-Host "`nLocal Postgres is already running on port $resolvedPostgresPort."
}
else {
    $resolvedPostgresPort = Resolve-FreePort -Name "Postgres" -Preferred $PostgresPort
}

if ($resolvedPostgresPort -ne 5432) {
    Write-Warning "Postgres is on port $resolvedPostgresPort, not 5432. The app is configured for it, but tests/Commerce.Integration still expects 5432."
}

# compose.yaml publishes ${COMMERCE_POSTGRES_PORT:-5432}. Setting it to the
# port the container already uses keeps `up` from recreating it.
$env:COMMERCE_POSTGRES_PORT = "$resolvedPostgresPort"
Write-Host "`nStarting local Postgres on port $resolvedPostgresPort (deploy/dev/compose.yaml)..."
docker compose -f $composeFile up -d postgres
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed with exit code $LASTEXITCODE" }

Write-Host "Waiting for Postgres to be healthy..."
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    $status = docker inspect --format='{{.State.Health.Status}}' $postgresContainer 2>$null
    if ($status -eq "healthy") { $ready = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $ready) { throw "Postgres did not become healthy within 60s." }
Write-Host "Postgres is healthy."

# --- Ports for the API and the HTTPS proxy --------------------------------
# With -NoApi the API is expected to be running already, so its port is taken
# as given instead of being moved away from it.
if ($NoApi) {
    $resolvedApiPort = $ApiPort
}
else {
    $resolvedApiPort = Resolve-FreePort -Name "Cloud.Api" -Preferred $ApiPort -Exclude @($resolvedPostgresPort)
}

$apiBaseUrl = "http://localhost:$resolvedApiPort"

if (-not $NoWeb) {
    $resolvedProxyPort = Resolve-FreePort -Name "HTTPS proxy" -Preferred $ProxyPort -Exclude @($resolvedPostgresPort, $resolvedApiPort)
    $browserUrl = "https://localhost:$resolvedProxyPort/login"
}

$commerceConnectionString = "Host=localhost;Port=$resolvedPostgresPort;Database=commerce_dev;Username=app_runtime;Password=dev-only-password"
$platformReadConnectionString = "Host=localhost;Port=$resolvedPostgresPort;Database=commerce_dev;Username=platform_readonly;Password=dev-only-platform-readonly-password"

$repoRootLiteral = ConvertTo-PowerShellSingleQuotedLiteral $repoRoot
$webRootLiteral = ConvertTo-PowerShellSingleQuotedLiteral $webRoot
$commerceConnectionStringLiteral = ConvertTo-PowerShellSingleQuotedLiteral $commerceConnectionString
$platformReadConnectionStringLiteral = ConvertTo-PowerShellSingleQuotedLiteral $platformReadConnectionString
$apiBaseUrlLiteral = ConvertTo-PowerShellSingleQuotedLiteral $apiBaseUrl

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
    Write-Host "`nLaunching Commerce.Cloud.Api on $apiBaseUrl in a new window..."
    $apiCommand = @(
        "cd $repoRootLiteral",
        "Write-Host 'Commerce.Cloud.Api $apiBaseUrl' -ForegroundColor Cyan",
        "`$env:ASPNETCORE_ENVIRONMENT = 'Development'",
        "`$env:ConnectionStrings__Commerce = $commerceConnectionStringLiteral",
        "`$env:ConnectionStrings__CommercePlatformRead = $platformReadConnectionStringLiteral",
        "`$env:PORT = '$resolvedApiPort'",
        "dotnet run --project src/Commerce.Cloud.Api"
    ) -join "; "

    Start-Process pwsh -ArgumentList @("-NoExit", "-Command", $apiCommand)

    Write-Host "Waiting for Cloud.Api health on $apiBaseUrl/health..."
    $apiReady = $false
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $response = Invoke-WebRequest -Uri "$apiBaseUrl/health" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $apiReady = $true; break }
        }
        catch {
            Start-Sleep -Seconds 1
        }
    }
    if (-not $apiReady) { throw "Commerce.Cloud.Api did not become healthy on $apiBaseUrl/health within 60s. Check the API window." }
    Write-Host "Cloud.Api is healthy."

    # Ensure the sign-in accounts exist on every launch. provision-admin.ps1
    # is idempotent, so this is a no-op once they are there. Without it, any
    # reset of the database leaves the developer unable to sign in until they
    # remember to provision by hand, which is exactly the friction this
    # launcher exists to remove. Non-fatal on purpose: a missing or
    # incomplete deploy/dev/.env should not stop a stack that is otherwise up.
    if (-not $NoProvision) {
        Write-Host "`nEnsuring local sign-in accounts (deploy/dev/provision-admin.ps1)..."
        try {
            & (Join-Path $PSScriptRoot 'provision-admin.ps1') -ApiBaseUrl $apiBaseUrl
        }
        catch {
            Write-Warning "Could not ensure the local accounts: $($_.Exception.Message)"
            Write-Warning "The stack is running; provision them with: pwsh deploy/dev/provision-admin.ps1 -ApiBaseUrl $apiBaseUrl"
        }
    }
}

# --- 4. HTTPS proxy for browser testing -----------------------------------
if (-not $NoWeb) {
    Write-Host "Launching HTTPS proxy https://localhost:$resolvedProxyPort -> $apiBaseUrl in a new window..."
    $proxyCommand = @(
        "cd $webRootLiteral",
        "Write-Host 'HTTPS proxy https://localhost:$resolvedProxyPort -> $apiBaseUrl' -ForegroundColor Cyan",
        "npm exec -- local-ssl-proxy --source $resolvedProxyPort --target $resolvedApiPort"
    ) -join "; "

    Start-Process pwsh -ArgumentList @("-NoExit", "-Command", $proxyCommand)

    if (-not $NoBrowser) {
        Start-Sleep -Seconds 3
        Write-Host "Opening browser at $browserUrl..."
        Start-Process $browserUrl
    }
}

# --- 5. Commerce.Pos.Windows (WPF) ----------------------------------------
if (-not $NoPos) {
    Write-Host "Launching Commerce.Pos.Windows in a new window..."
    # PosHostBuilder reads Commerce:CloudApiBaseUrl; without it the POS falls
    # back to http://localhost:8080 and would miss an API moved to another port.
    $posCommand = @(
        "cd $repoRootLiteral",
        "Write-Host 'Commerce.Pos.Windows -> $apiBaseUrl' -ForegroundColor Cyan",
        "`$env:Commerce__CloudApiBaseUrl = $apiBaseUrlLiteral",
        "dotnet run --project src/Commerce.Pos.Windows"
    ) -join "; "

    Start-Process pwsh -ArgumentList @("-NoExit", "-Command", $posCommand)
}

Write-Host "`nAll requested processes launched."
if (-not $NoWeb) { Write-Host "Browser URL: $browserUrl" }
Write-Host "API URL:     $apiBaseUrl"
Write-Host "API health:  $apiBaseUrl/health"
Write-Host "Postgres:    localhost:$resolvedPostgresPort"
Write-Host "Close each process window to stop it; Postgres keeps running in Docker until you run:"
Write-Host "docker compose -f deploy/dev/compose.yaml down"
