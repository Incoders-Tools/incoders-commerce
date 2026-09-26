# Restores the two local development identities in one command:
#
#   1. the platform system administrator (seeded through the Development-only
#      /internal/test-seed/user seam with `systemAdmin = true`, then REPAIRED
#      idempotently: `is_system_admin = true` and every organization role /
#      branch-scope assignment removed — the sysadmin is the platform owner,
#      never a client's `business-admin` inside an organization), and
#   2. the first organization plus its `business-admin`, created the real way —
#      authenticated as that system administrator, through
#      POST /account/organizations, which writes organization, branch, user and
#      audit entry in one transaction.
#
# IDEMPOTENT BY DESIGN. An earlier revision threw on the first 409 and stopped.
# That is the wrong behavior for the one situation this script exists for:
# putting the accounts back after they were lost. Recovery is rarely all-or-
# nothing — the sysadmin survives and the org admin does not, or the reverse —
# and a script that refuses to run unless the database is completely empty
# forces exactly the manual work it was written to remove.
#
# So each identity is ENSURED rather than created: on 409 the script proves the
# account that already exists is the one described by deploy/dev/.env, by
# signing in as it and (for the sysadmin) re-applying the repair. It never
# rewrites a password to make a mismatch disappear; a stored password that
# differs from .env fails loudly, naming the account and the file, because
# silently "fixing" someone's account is worse than stopping.
#
# Reads every credential from the ignored deploy/dev/.env (see .env.example).
# Refuses any non-loopback API: this is local-development provisioning only.
#
# Usage: pwsh -File deploy/dev/provision-admin.ps1
#        pwsh -File deploy/dev/provision-admin.ps1 -ApiBaseUrl http://127.0.0.1:8080

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

# The sysadmin promotion below interpolates an email into SQL, because
# `docker compose exec psql -c` takes a statement, not bound parameters. The
# value comes from a local .env the developer owns, but "trusted source" is not
# a reason to leave a quote-injection open in a file that runs psql as a
# superuser. Anything that is not a conservative address is rejected outright
# rather than escaped.
function Assert-SafeEmail {
    param(
        [Parameter(Mandatory)] [string]$Value,
        [Parameter(Mandatory)] [string]$Name
    )

    if ($Value -notmatch "^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$") {
        throw "$Name in deploy/dev/.env is not a plain email address. Use one, with no quotes or whitespace."
    }

    return $Value.Trim().ToLowerInvariant()
}

function Send-LocalJsonRequest {
    param(
        [Parameter(Mandatory)] [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [object]$Body,
        [string[]]$Cookies
    )

    $json = $Body | ConvertTo-Json -Compress
    $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $Path)
    $request.Content = $content
    if ($Cookies -and $Cookies.Count -gt 0) {
        $request.Headers.TryAddWithoutValidation('Cookie', ($Cookies -join '; ')) | Out-Null
    }

    try {
        $response = $Client.SendAsync($request).GetAwaiter().GetResult()
        $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        # Program.cs sets CookieSecurePolicy.Always on the sign-in cookie, so
        # HttpClient's own CookieContainer refuses to send it back over plain
        # http://localhost. The authenticated call below therefore carries the
        # Set-Cookie values through by hand instead of relying on the handler.
        $setCookies = @()
        $headerValues = $null
        if ($response.Headers.TryGetValues('Set-Cookie', [ref]$headerValues)) {
            foreach ($headerValue in $headerValues) {
                $setCookies += ($headerValue -split ';')[0].Trim()
            }
        }

        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Body = $responseBody
            Cookies = $setCookies
        }
    }
    finally {
        $request.Dispose()
    }
}

function Invoke-LocalPsql {
    param(
        [Parameter(Mandatory)] [string]$RepositoryRoot,
        [Parameter(Mandatory)] [string]$Sql
    )

    Push-Location $RepositoryRoot
    try {
        $output = & docker compose -f deploy/dev/compose.yaml exec -T postgres `
            psql -v ON_ERROR_STOP=1 -U commerce_owner -d commerce_dev -c $Sql 2>&1
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = ($output -join "`n")
        }
    }
    finally {
        Pop-Location
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

# The platform system administrator used to be configured as
# COMMERCE_DEV_ADMIN_*, which reads like the administrator OF an
# organization and is not. A stale value under that name silently promoted
# the wrong account to is_system_admin. Refuse to run rather than guess.
foreach ($retired in @(
    @{ Old = 'COMMERCE_DEV_ADMIN_EMAIL';    New = 'COMMERCE_DEV_SYSADMIN_EMAIL' },
    @{ Old = 'COMMERCE_DEV_ADMIN_PASSWORD'; New = 'COMMERCE_DEV_SYSADMIN_PASSWORD' })) {
    if ($envValues.ContainsKey($retired.Old)) {
        throw "deploy/dev/.env still sets $($retired.Old). Rename it to $($retired.New): it configures the PLATFORM system administrator, not an organization's admin. Remove the old key so it cannot be applied by mistake."
    }
}

# Keys the script does not read are almost always a misunderstanding about
# which identity is which, so name them instead of ignoring them.
$unknownOrgSysadminKeys = @($envValues.Keys | Where-Object { $_ -like 'COMMERCE_DEV_ORGANIZATION_SYSADMIN*' })
if ($unknownOrgSysadminKeys.Count -gt 0) {
    throw "deploy/dev/.env sets $($unknownOrgSysadminKeys -join ', '), which this script does not read. There is no per-organization system administrator: COMMERCE_DEV_SYSADMIN_* is the platform-wide one, and COMMERCE_DEV_ORGANIZATION_ADMIN_* is that organization's business-admin. Remove these keys."
}

$email = Assert-SafeEmail -Value (Get-RequiredEnvValue -Values $envValues -Name 'COMMERCE_DEV_SYSADMIN_EMAIL') -Name 'COMMERCE_DEV_SYSADMIN_EMAIL'
$password = Get-RequiredEnvValue -Values $envValues -Name 'COMMERCE_DEV_SYSADMIN_PASSWORD'
$organizationName = Get-RequiredEnvValue -Values $envValues -Name 'COMMERCE_DEV_ORGANIZATION_NAME'
$organizationAdminEmail = Assert-SafeEmail -Value (Get-RequiredEnvValue -Values $envValues -Name 'COMMERCE_DEV_ORGANIZATION_ADMIN_EMAIL') -Name 'COMMERCE_DEV_ORGANIZATION_ADMIN_EMAIL'
$organizationAdminPassword = Get-RequiredEnvValue -Values $envValues -Name 'COMMERCE_DEV_ORGANIZATION_ADMIN_PASSWORD'

# Optional: the organization's first branch. The API defaults to "Main".
$organizationBranchName = 'Main'
if ($envValues.ContainsKey('COMMERCE_DEV_ORGANIZATION_BRANCH_NAME') -and -not [string]::IsNullOrWhiteSpace($envValues['COMMERCE_DEV_ORGANIZATION_BRANCH_NAME'])) {
    $organizationBranchName = $envValues['COMMERCE_DEV_ORGANIZATION_BRANCH_NAME'].Trim()
}

if ($organizationAdminEmail -eq $email) {
    throw 'COMMERCE_DEV_ORGANIZATION_ADMIN_EMAIL must differ from COMMERCE_DEV_SYSADMIN_EMAIL. The system administrator and the organization business-admin are two separate identities.'
}

$client = [System.Net.Http.HttpClient]::new()
$client.BaseAddress = [Uri]::new($apiUri.GetLeftPart([System.UriPartial]::Authority).TrimEnd('/') + '/')

try {
    # --- 1. System administrator -----------------------------------------
    # B1 (odd/tasks/frontend-modernization.md, product review backlog): the
    # sysadmin is the PLATFORM owner, business-admin is a CLIENT's user
    # inside an organization — they must never be conflated. `systemAdmin =
    # $true` asks /internal/test-seed/user's fixed seam for a sysadmin with
    # ZERO organization roles and ZERO branch scope, rather than the
    # business-admin grant it used to hand out unconditionally.
    $systemAdminExisted = $false
    $organizationId = [guid]::NewGuid()
    $seedResponse = Send-LocalJsonRequest -Client $client -Path 'internal/test-seed/user' -Body @{
        organizationId = $organizationId
        email = $email
        password = $password
        systemAdmin = $true
    }

    if ($seedResponse.StatusCode -eq 200) {
        $systemAdminExisted = $false
    }
    elseif ($seedResponse.StatusCode -eq 409) {
        $systemAdminExisted = $true
    }
    else {
        throw "Development test seed failed with HTTP $($seedResponse.StatusCode). Confirm the full local compose stack is running in Development."
    }

    # Idempotent REPAIR, run unconditionally on BOTH the created and the
    # already-present path, and regardless of whether the running API build
    # is new enough to understand `systemAdmin` (an older build silently
    # ignores the unknown JSON field via the seam's own defaulted parameter
    # and falls back to granting the ordinary business-admin shape). This is
    # the one statement that repairs that stray grant too: is_system_admin
    # is set true, and every organization role and branch-scope assignment
    # is removed — a sysadmin holds NO organization roles, ever, and this
    # runs every time so a stray grant from before this fix (or from an old
    # API build) never survives a re-run.
    # `users.email` holds the normalized address (PostgresUserAccountStore.
    # Normalize: trim + lowercase), which is what Assert-SafeEmail produced,
    # and `user_directory.email_normalized` is globally unique, so matching
    # by email alone identifies exactly the account this script owns.
    $repair = Invoke-LocalPsql -RepositoryRoot $repositoryRoot -Sql "UPDATE users SET is_system_admin = true, roles = '[]'::jsonb, branch_scope = '{}' WHERE email = '$email' RETURNING id;"
    if ($repair.ExitCode -ne 0) {
        throw 'Local database repair failed. Confirm the postgres service is running and healthy.'
    }
    if ($repair.Output -notmatch '\(1 row\)') {
        throw "The local account '$email' was not found in commerce_dev after seeding. Confirm deploy/dev/compose.yaml's postgres is the database Cloud.Api is pointed at."
    }

    # --- 2. Verify the system administrator signs in ----------------------
    $signInResponse = Send-LocalJsonRequest -Client $client -Path 'account/sign-in' -Body @{ email = $email; password = $password }
    if ($signInResponse.StatusCode -ne 200) {
        if ($systemAdminExisted) {
            throw "'$email' already exists locally but COMMERCE_DEV_SYSADMIN_PASSWORD does not sign it in (HTTP $($signInResponse.StatusCode)). Set the password that account actually has in deploy/dev/.env, or choose a different COMMERCE_DEV_SYSADMIN_EMAIL. This script never overwrites an existing password."
        }
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

    $systemAdminCookies = $signInResponse.Cookies
    if (-not $systemAdminCookies -or $systemAdminCookies.Count -eq 0) {
        throw 'Sign-in succeeded but returned no session cookie; the organization could not be created as the system administrator.'
    }

    # --- 3. Verify Desktop pairing ----------------------------------------
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
            'paired' { $pairingNote = 'web sign-in and Desktop pairing verified' }
            'branch-selection-required' { $pairingNote = 'web sign-in verified; Desktop requires branch selection' }
            default { throw "Desktop pairing returned unexpected status '$($pairing.status)'." }
        }
    }
    catch {
        if ($_.Exception.Message -like 'Desktop pairing returned unexpected status*') {
            throw
        }
        throw 'Desktop pairing verification returned an invalid response.'
    }

    $systemAdminState = if ($systemAdminExisted) { 'already present' } else { 'created' }
    Write-Output "System administrator $systemAdminState : $email ($pairingNote)."

    # --- 4. Organization and its first business-admin ---------------------
    # POST /account/organizations is the real path: authorized as the system
    # administrator, it writes organization, branch, business-admin user and
    # audit entry in a single transaction (Account.cs organizationGroup).
    $createOrganizationResponse = Send-LocalJsonRequest -Client $client -Path 'account/organizations' -Cookies $systemAdminCookies -Body @{
        organizationName = $organizationName
        branchName = $organizationBranchName
        adminEmail = $organizationAdminEmail
        adminPassword = $organizationAdminPassword
    }

    switch ($createOrganizationResponse.StatusCode) {
        201 {
            try {
                $createdOrganization = $createOrganizationResponse.Body | ConvertFrom-Json
                $createdOrganizationId = [guid]$createdOrganization.organizationId
            }
            catch {
                throw 'Organization creation returned an invalid response body.'
            }
            Write-Output "Organization created: '$organizationName' ($createdOrganizationId) with business-admin $organizationAdminEmail."
        }
        409 {
            # Idempotent path: the organization admin survived whatever removed
            # the other account. Prove it is the identity .env describes rather
            # than assuming it, and never rewrite its password.
            $organizationSignIn = Send-LocalJsonRequest -Client $client -Path 'account/sign-in' -Body @{
                email = $organizationAdminEmail
                password = $organizationAdminPassword
            }
            if ($organizationSignIn.StatusCode -ne 200) {
                throw "'$organizationAdminEmail' already exists locally but COMMERCE_DEV_ORGANIZATION_ADMIN_PASSWORD does not sign it in (HTTP $($organizationSignIn.StatusCode)). Set the password that account actually has in deploy/dev/.env, or choose a different COMMERCE_DEV_ORGANIZATION_ADMIN_EMAIL. This script never overwrites an existing password."
            }
            Write-Output "Organization admin already present: $organizationAdminEmail (sign-in verified; no organization was created)."
        }
        401 {
            throw 'The system administrator session was rejected by POST /account/organizations. The sign-in cookie was not accepted; confirm Cloud.Api is running in Development on this loopback origin.'
        }
        403 {
            throw "'$email' signed in but is not authorized to create organizations. The is_system_admin promotion did not take effect for this session."
        }
        default {
            throw "Organization creation failed with HTTP $($createOrganizationResponse.StatusCode). Body: $($createOrganizationResponse.Body)"
        }
    }

    Write-Output 'Local development identities are ready. Sign in to Web and Desktop with the values in deploy/dev/.env.'
}
finally {
    $client.Dispose()
}
