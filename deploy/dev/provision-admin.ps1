[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://localhost:8080'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-RequiredEnvValue {
    param(
        [Parameter(Mandatory)] [hashtable]$Values,
        [Parameter(Mandatory)] [string]$Name
    )

    if (-not $Values.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace($Values[$Name])) {
        throw "$Name must be set to a non-blank value in deploy/dev/.env."
    }

    return $Values[$Name].Trim()
}

function Read-LocalEnvFile {
    param([Parameter(Mandatory)] [string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing local credential file: $Path. Copy deploy/dev/.env.example to deploy/dev/.env and set unique values."
    }

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) {
            continue
        }

        $separator = $trimmed.IndexOf('=')
        if ($separator -lt 1) {
            throw "Invalid .env entry in $Path. Expected NAME=value."
        }

        $values[$trimmed.Substring(0, $separator).Trim()] = $trimmed.Substring($separator + 1).Trim()
    }

    return $values
}

function Send-LocalJsonRequest {
    param(
        [Parameter(Mandatory)] [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [object]$Body
    )

    $json = $Body | ConvertTo-Json -Compress
    $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
    try {
        $response = $Client.PostAsync($Path, $content).GetAwaiter().GetResult()
        $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Body = $responseBody
        }
    }
    finally {
        $content.Dispose()
    }
}

$apiUri = [Uri]$ApiBaseUrl
if ($apiUri.Scheme -notin @('http', 'https') -or -not [string]::IsNullOrEmpty($apiUri.Query) -or -not [string]::IsNullOrEmpty($apiUri.Fragment)) {
    throw 'ApiBaseUrl must be an http(s) origin without a query or fragment.'
}

$loopbackHosts = @('localhost', '127.0.0.1', '::1')
if ($apiUri.Host.ToLowerInvariant() -notin $loopbackHosts) {
    throw 'Refusing a non-loopback API. This provisioning workflow is local-development only.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$envFile = Join-Path $PSScriptRoot '.env'
$envValues = Read-LocalEnvFile -Path $envFile
$email = Get-RequiredEnvValue -Values $envValues -Name 'COMMERCE_DEV_ADMIN_EMAIL'
$password = Get-RequiredEnvValue -Values $envValues -Name 'COMMERCE_DEV_ADMIN_PASSWORD'

$client = [System.Net.Http.HttpClient]::new()
$client.BaseAddress = [Uri]::new($apiUri.GetLeftPart([System.UriPartial]::Authority).TrimEnd('/') + '/')

try {
    $organizationId = [guid]::NewGuid()
    $seedResponse = Send-LocalJsonRequest -Client $client -Path 'internal/test-seed/user' -Body @{
        organizationId = $organizationId
        email = $email
        password = $password
    }

    if ($seedResponse.StatusCode -eq 409) {
        throw "The local account '$email' already exists. No user was promoted; choose a new email in deploy/dev/.env to provision a separate local admin."
    }
    if ($seedResponse.StatusCode -ne 200) {
        throw "Development test seed failed with HTTP $($seedResponse.StatusCode). Confirm the full local compose stack is running in Development."
    }

    try {
        $seededUser = $seedResponse.Body | ConvertFrom-Json
        $userId = [guid]$seededUser.userId
    }
    catch {
        throw 'Development test seed returned an invalid user identifier; no promotion was attempted.'
    }

    Push-Location $repositoryRoot
    try {
        $promotionSql = "UPDATE users SET is_system_admin = true WHERE id = '$userId'::uuid RETURNING id;"
        $promotionOutput = & docker compose -f deploy/dev/compose.yaml exec -T postgres psql -v ON_ERROR_STOP=1 -U commerce_owner -d commerce_dev -c $promotionSql 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw 'Local database promotion failed. Confirm the postgres service is running and healthy.'
        }
        if (($promotionOutput -join "`n") -notmatch [regex]::Escape($userId.ToString())) {
            throw 'Local database promotion did not return the seeded user identifier.'
        }
    }
    finally {
        Pop-Location
    }

    $signInResponse = Send-LocalJsonRequest -Client $client -Path 'account/sign-in' -Body @{ email = $email; password = $password }
    if ($signInResponse.StatusCode -ne 200) {
        throw "Sign-in verification failed with HTTP $($signInResponse.StatusCode)."
    }
    try {
        $signedIn = $signInResponse.Body | ConvertFrom-Json
        if (-not [bool]$signedIn.isSystemAdmin) {
            throw 'Sign-in verification did not report isSystemAdmin: true.'
        }
    }
    catch {
        if ($_.Exception.Message -eq 'Sign-in verification did not report isSystemAdmin: true.') {
            throw
        }
        throw 'Sign-in verification returned an invalid response.'
    }

    $pairResponse = Send-LocalJsonRequest -Client $client -Path 'device/pair' -Body @{
        email = $email
        password = $password
        installationId = [guid]::NewGuid()
        branchId = $null
    }
    if ($pairResponse.StatusCode -ne 200) {
        throw "Desktop pairing verification failed with HTTP $($pairResponse.StatusCode)."
    }
    try {
        $pairing = $pairResponse.Body | ConvertFrom-Json
        switch ($pairing.status) {
            'paired' { Write-Output "Local development administrator ready: $email (web sign-in and Desktop pairing verified)." }
            'branch-selection-required' { Write-Output "Local development administrator ready: $email (web sign-in verified; Desktop requires branch selection)." }
            default { throw "Desktop pairing returned unexpected status '$($pairing.status)'." }
        }
    }
    catch {
        if ($_.Exception.Message -like 'Desktop pairing returned unexpected status*') {
            throw
        }
        throw 'Desktop pairing verification returned an invalid response.'
    }
}
finally {
    $client.Dispose()
}
