@echo off
rem Double-click entry point for the local stack. Checks tooling, starts Docker
rem Desktop when its engine is down, refreshes dependencies, then hands off to
rem run-all.ps1, which picks free ports and launches every process.
rem Arguments are forwarded to run-all.ps1, e.g.: run-all.bat -NoPos
setlocal

for %%I in ("%~dp0..\..") do set "REPO_ROOT=%%~fI"
set "WEB_ROOT=%REPO_ROOT%\src\Commerce.Web"
set "LAUNCHER=%REPO_ROOT%\deploy\dev\run-all.ps1"
set "DOCKER_DESKTOP_EXE=%ProgramFiles%\Docker\Docker\Docker Desktop.exe"
set "DOCKER_WAIT_LIMIT=180"

title Incoders Commerce - local stack launcher
cls
echo ============================================================
echo  Incoders Commerce - Local stack
echo ============================================================
echo.
echo This starts everything needed for local testing:
echo   0. Prerequisites, Docker engine and project dependencies
echo   1. Postgres via Docker Compose          (port 5432 or next free)
echo   2. Commerce.Cloud.Api                   (port 8080 or next free)
echo   3. Browser-ready HTTPS app on /login    (port 5443 or next free)
echo   4. Commerce.Pos.Windows, pointed at the API above
echo.
echo You do NOT need Vite port 5173 for sign-in testing.
echo.

if not exist "%LAUNCHER%" (
  echo ERROR: Launcher script not found: %LAUNCHER%
  goto :fail
)

rem --- Tooling -----------------------------------------------------------------
echo [1/4] Checking tooling...

where pwsh >nul 2>nul
if errorlevel 1 (
  echo ERROR: PowerShell 7 ^(pwsh^) is not available on PATH.
  echo Install it with: winget install Microsoft.PowerShell
  goto :fail
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: dotnet is not available on PATH.
  echo Install the .NET 10 SDK with: winget install Microsoft.DotNet.SDK.10
  goto :fail
)

dotnet --list-sdks 2>nul | findstr /b "10." >nul
if errorlevel 1 (
  echo ERROR: The .NET 10 SDK is required ^(projects target net10.0^). Installed SDKs:
  dotnet --list-sdks
  echo Install it with: winget install Microsoft.DotNet.SDK.10
  goto :fail
)

where node >nul 2>nul
if errorlevel 1 (
  echo ERROR: Node.js is not available on PATH.
  echo Install Node.js 22 LTS with: winget install OpenJS.NodeJS.LTS
  goto :fail
)

rem Vite 8 requires Node ^20.19 or ^22.12 or newer.
node -e "const [a,b]=process.versions.node.split('.').map(Number);process.exit((a===20&&b>=19)||(a===22&&b>=12)||a>=23?0:1)"
if errorlevel 1 (
  for /f %%v in ('node -v') do echo ERROR: Node.js %%v is too old. Vite 8 needs 20.19+ or 22.12+.
  goto :fail
)

where npm >nul 2>nul
if errorlevel 1 (
  echo ERROR: npm is not available on PATH.
  goto :fail
)

where docker >nul 2>nul
if errorlevel 1 (
  echo ERROR: Docker is not installed or not on PATH.
  echo Install Docker Desktop with: winget install Docker.DockerDesktop
  goto :fail
)

for /f "delims=" %%v in ('dotnet --version') do echo   dotnet  %%v
for /f "delims=" %%v in ('node -v') do echo   node    %%v
for /f "delims=" %%v in ('pwsh -NoProfile -Command "$PSVersionTable.PSVersion.ToString()"') do echo   pwsh    %%v
echo.

rem --- Docker engine -------------------------------------------------------------
echo [2/4] Checking Docker engine...
docker info >nul 2>nul
if not errorlevel 1 goto :docker_ready

echo Docker engine is not running. Starting Docker Desktop...
docker desktop start --detach >nul 2>nul
if not errorlevel 1 goto :docker_wait_start

rem Older Docker Desktop builds have no "docker desktop" CLI plugin.
if not exist "%DOCKER_DESKTOP_EXE%" (
  echo ERROR: Could not start Docker Desktop and it was not found at:
  echo   %DOCKER_DESKTOP_EXE%
  echo Start Docker Desktop manually and run this launcher again.
  goto :fail
)
start "" "%DOCKER_DESKTOP_EXE%"

:docker_wait_start
set /a DOCKER_WAITED=0

:docker_wait
docker info >nul 2>nul
if not errorlevel 1 goto :docker_ready
if %DOCKER_WAITED% GEQ %DOCKER_WAIT_LIMIT% (
  echo ERROR: Docker engine did not become ready within %DOCKER_WAIT_LIMIT%s.
  echo Open Docker Desktop, check for errors or pending updates, then run this launcher again.
  goto :fail
)
echo   waiting for Docker engine... %DOCKER_WAITED%s
timeout /t 3 /nobreak >nul
set /a DOCKER_WAITED+=3
goto :docker_wait

:docker_ready
for /f "delims=" %%v in ('docker info --format "{{.ServerVersion}}"') do echo   Docker engine %%v is running.
echo.

rem --- Dependencies --------------------------------------------------------------
echo [3/4] Checking project dependencies...

rem Reinstall node_modules when it is missing or older than package-lock.json
rem (for example after a pull that changed dependencies). npm ci rewrites
rem node_modules\.package-lock.json, so a fresh install is not repeated.
pwsh -NoProfile -Command "$lock = Get-Item -LiteralPath '%WEB_ROOT%\package-lock.json'; $installed = '%WEB_ROOT%\node_modules\.package-lock.json'; if ((Test-Path -LiteralPath $installed) -and (Get-Item -LiteralPath $installed).LastWriteTime -ge $lock.LastWriteTime) { exit 0 } else { exit 1 }"
if not errorlevel 1 goto :npm_ready

echo node_modules is missing or out of date; running npm ci...
pushd "%WEB_ROOT%"
call npm ci
set "NPM_EXIT=%ERRORLEVEL%"
popd
if not "%NPM_EXIT%"=="0" (
  echo ERROR: npm ci failed with exit code %NPM_EXIT%.
  goto :fail
)

:npm_ready
echo   Commerce.Web node_modules is up to date.

echo   Restoring .NET packages...
dotnet restore "%REPO_ROOT%\src\Commerce.Cloud.Api\Commerce.Cloud.Api.csproj" --verbosity quiet
if errorlevel 1 (
  echo ERROR: dotnet restore failed for Commerce.Cloud.Api.
  goto :fail
)
dotnet restore "%REPO_ROOT%\src\Commerce.Pos.Windows\Commerce.Pos.Windows.csproj" --verbosity quiet
if errorlevel 1 (
  echo ERROR: dotnet restore failed for Commerce.Pos.Windows.
  goto :fail
)
echo   .NET packages restored.
echo.

rem --- Launch ------------------------------------------------------------------
echo [4/4] Starting stack. This window will stay open and print the final URLs.
echo.
cd /d "%REPO_ROOT%"
pwsh -NoProfile -ExecutionPolicy Bypass -File "%LAUNCHER%" %*
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo ERROR: Launcher failed with exit code %EXITCODE%.
  goto :fail
)

echo.
echo ============================================================
echo  Stack launched - use the URLs printed above
echo ============================================================
echo.
echo Leave the opened API / HTTPS proxy / POS windows running while testing.
echo Close those windows to stop app processes.
echo Postgres keeps running in Docker until you run:
echo   docker compose -f "%REPO_ROOT%\deploy\dev\compose.yaml" down
echo.
pause
exit /b 0

:fail
echo.
echo Nothing else will be started from this launcher until the error above is fixed.
echo.
pause
exit /b 1
