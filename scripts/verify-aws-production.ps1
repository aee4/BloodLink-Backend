param(
    [string]$ApiUrl = "https://wvsrmqrfc0.execute-api.eu-north-1.amazonaws.com",
    [string]$Region = "eu-north-1"
)

$ErrorActionPreference = "Stop"
$checks = [System.Collections.Generic.List[object]]::new()
$bootstrapSecretJson = $null
$bootstrapPassword = $null
$adminAccessToken = $null
$refreshToken = $null
$rotatedAccessToken = $null
$rotatedRefreshToken = $null
$smokeAdminPassword = $null
$randomBytes = $null
$rng = $null

function Add-Check([string]$Name, [bool]$Passed) {
    $checks.Add([pscustomobject]@{ Check = $Name; Result = $(if ($Passed) { "PASS" } else { "FAIL" }) })
}

function Invoke-ApiRequest(
    [string]$Method,
    [string]$Path,
    [string]$Token = "",
    [object]$Body = $null,
    [string]$Origin = "",
    [string]$RequestedMethod = "",
    [string]$RequestedHeaders = ""
) {
    $headers = @{}
    if ($Token) { $headers["Authorization"] = "Bearer $Token" }
    if ($Origin) { $headers["Origin"] = $Origin }
    if ($RequestedMethod) { $headers["Access-Control-Request-Method"] = $RequestedMethod }
    if ($RequestedHeaders) { $headers["Access-Control-Request-Headers"] = $RequestedHeaders }

    $parameters = @{
        Method = $Method
        Uri = "$($ApiUrl.TrimEnd('/'))$Path"
        Headers = $headers
        UseBasicParsing = $true
        TimeoutSec = 45
    }
    if ($null -ne $Body) {
        $parameters.Body = ConvertTo-Json -InputObject $Body -Depth 8 -Compress
        $parameters.ContentType = "application/json"
    }

    try {
        $response = Invoke-WebRequest @parameters
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Headers = $response.Headers
            Content = [string]$response.Content
        }
    } catch {
        $response = $_.Exception.Response
        if ($null -eq $response) { throw "HTTPS request failed without a response." }
        $content = ""
        try {
            $reader = [IO.StreamReader]::new($response.GetResponseStream())
            $content = $reader.ReadToEnd()
            $reader.Dispose()
        } catch {
            $content = ""
        }

        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Headers = $response.Headers
            Content = $content
        }
    }
}

function Get-HeaderValue($Headers, [string]$Name) {
    foreach ($key in $Headers.Keys) {
        if ([string]$key -ieq $Name) { return [string]$Headers[$key] }
    }
    return ""
}

try {
    $health = Invoke-ApiRequest -Method GET -Path "/health"
    Add-Check "HTTPS health endpoint" ($health.StatusCode -eq 200)

    $readiness = Invoke-ApiRequest -Method GET -Path "/health/ready"
    Add-Check "Database readiness" ($readiness.StatusCode -eq 200)

    $swagger = Invoke-ApiRequest -Method GET -Path "/swagger/v1/swagger.json"
    Add-Check "Production Swagger disabled" ($swagger.StatusCode -eq 404)

    $frontendOrigin = "https://d2z1pcfp95dfwd.cloudfront.net"
    $corsAllowed = Invoke-ApiRequest -Method OPTIONS -Path "/health" -Origin $frontendOrigin `
        -RequestedMethod POST -RequestedHeaders "authorization,content-type"
    $allowedOrigin = Get-HeaderValue $corsAllowed.Headers "Access-Control-Allow-Origin"
    $allowedMethods = @((Get-HeaderValue $corsAllowed.Headers "Access-Control-Allow-Methods").Split(',') |
        ForEach-Object { $_.Trim().ToUpperInvariant() })
    $allowedHeaders = @((Get-HeaderValue $corsAllowed.Headers "Access-Control-Allow-Headers").Split(',') |
        ForEach-Object { $_.Trim().ToLowerInvariant() })
    Add-Check "CORS preflight allows exact CloudFront origin" ($corsAllowed.StatusCode -in @(200, 204) -and $allowedOrigin -eq $frontendOrigin)
    Add-Check "CORS preflight supports POST, Authorization, and Content-Type" (
        $allowedMethods -contains "POST" -and $allowedHeaders -contains "authorization" -and $allowedHeaders -contains "content-type")
    Add-Check "CORS never emits wildcard or credentials" (
        $allowedOrigin -ne "*" -and [string]::IsNullOrWhiteSpace((Get-HeaderValue $corsAllowed.Headers "Access-Control-Allow-Credentials")))
    $normalCors = Invoke-ApiRequest -Method GET -Path "/health" -Origin $frontendOrigin
    Add-Check "Normal response allows exact CloudFront origin" (
        $normalCors.StatusCode -eq 200 -and (Get-HeaderValue $normalCors.Headers "Access-Control-Allow-Origin") -eq $frontendOrigin)
    Add-Check "Missing-Origin request remains available" (
        $health.StatusCode -eq 200 -and [string]::IsNullOrWhiteSpace((Get-HeaderValue $health.Headers "Access-Control-Allow-Origin")))
    foreach ($untrustedOrigin in @(
        "https://example.com",
        "https://evil.example",
        "http://d2z1pcfp95dfwd.cloudfront.net",
        "https://d2z1pcfp95dfwd.cloudfront.net.evil.example"
    )) {
        $corsDenied = Invoke-ApiRequest -Method OPTIONS -Path "/health" -Origin $untrustedOrigin `
            -RequestedMethod POST -RequestedHeaders "authorization,content-type"
        $deniedOrigin = Get-HeaderValue $corsDenied.Headers "Access-Control-Allow-Origin"
        Add-Check "CORS rejects untrusted origin $untrustedOrigin" ([string]::IsNullOrWhiteSpace($deniedOrigin))
    }

    $bootstrapSecretJson = aws secretsmanager get-secret-value --region $Region --secret-id bloodlink/prod/bootstrap --query SecretString --output text
    if ($LASTEXITCODE -ne 0) { throw "Could not load bootstrap configuration for live authentication checks." }
    $bootstrap = $bootstrapSecretJson | ConvertFrom-Json
    $bootstrapAdmin = $bootstrap.bloodLink.bootstrapAdmin
    Add-Check "Bootstrap disabled" ($bootstrapAdmin.enabled -eq $false)

    $login = Invoke-ApiRequest -Method POST -Path "/api/v1/auth/login" -Body @{ email = $bootstrapAdmin.email; password = $bootstrapAdmin.password }
    Add-Check "SystemAdmin login" ($login.StatusCode -eq 200)
    if ($login.StatusCode -ne 200) { throw "Live SystemAdmin login failed." }

    $loginBody = $login.Content | ConvertFrom-Json
    $adminAccessToken = [string]$loginBody.accessToken
    $refreshToken = [string]$loginBody.refreshToken
    $adminRoles = @($loginBody.user.roles)
    Add-Check "Intended SystemAdmin role" ($adminRoles -contains "SystemAdmin" -and $null -eq $loginBody.user.facilityId)

    $me = Invoke-ApiRequest -Method GET -Path "/api/v1/auth/me" -Token $adminAccessToken
    Add-Check "Authenticated profile" ($me.StatusCode -eq 200)

    $unauthenticated = Invoke-ApiRequest -Method GET -Path "/api/v1/auth/me"
    Add-Check "Protected endpoint requires authentication" ($unauthenticated.StatusCode -eq 401)

    $facilityPageBefore = (Invoke-ApiRequest -Method GET -Path "/api/v1/system/facilities?page=1&pageSize=100" -Token $adminAccessToken).Content | ConvertFrom-Json
    $facilityCountBefore = @($facilityPageBefore.items).Count
    $wrongRole = Invoke-ApiRequest -Method GET -Path "/api/v1/facilities/me" -Token $adminAccessToken
    Add-Check "Wrong-role request returns 403" ($wrongRole.StatusCode -eq 403)
    $facilityPageAfterDenied = (Invoke-ApiRequest -Method GET -Path "/api/v1/system/facilities?page=1&pageSize=100" -Token $adminAccessToken).Content | ConvertFrom-Json
    $facilityCountAfterDenied = @($facilityPageAfterDenied.items).Count
    Add-Check "Denied request performs no facility write" ($facilityCountBefore -eq $facilityCountAfterDenied)

    $missingId = [guid]::NewGuid().ToString()
    $missing = Invoke-ApiRequest -Method GET -Path "/api/v1/system/facilities/$missingId" -Token $adminAccessToken
    Add-Check "Missing private record returns 404" ($missing.StatusCode -eq 404)
    Add-Check "Missing-record response is generic" ($missing.Content -notmatch [regex]::Escape($missingId) -and $missing.Content -notmatch "(?i)stack trace|SqlException|Server=.*Database=")

    $invalidRegistration = Invoke-ApiRequest -Method POST -Path "/api/v1/facilities/register" -Body @{}
    Add-Check "Invalid registration input returns 400" ($invalidRegistration.StatusCode -eq 400)
    Add-Check "Validation response hides internals" ($invalidRegistration.Content -notmatch "(?i)stack trace|SqlException|Server=.*Database=" -and $invalidRegistration.Content -notmatch [regex]::Escape([string]$bootstrapAdmin.password))

    $suffix = [guid]::NewGuid().ToString("N")
    $randomBytes = [byte[]]::new(32)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($randomBytes)
    $smokeAdminPassword = [Convert]::ToBase64String($randomBytes) + "Aa1!"
    $registration = Invoke-ApiRequest -Method POST -Path "/api/v1/facilities/register" -Body @{
        name = "BloodLink Deployment Smoke $suffix"
        facilityType = 0
        registrationNumber = "SMOKE-$suffix"
        region = "Greater Accra"
        city = "Accra"
        address = "Deployment verification"
        contactEmail = "smoke-$suffix@example.invalid"
        contactPhone = "0200000000"
        adminFirstName = "Deployment"
        adminLastName = "Smoke"
        adminEmail = "admin-$suffix@example.invalid"
        adminPhoneNumber = "0200000001"
        adminPassword = $smokeAdminPassword
    }
    Add-Check "Facility registration returns 201" ($registration.StatusCode -eq 201)
    if ($registration.StatusCode -ne 201) { throw "Facility registration smoke check failed." }

    $registeredFacility = $registration.Content | ConvertFrom-Json
    $facilityId = [string]$registeredFacility.id
    $persisted = Invoke-ApiRequest -Method GET -Path "/api/v1/system/facilities/$facilityId" -Token $adminAccessToken
    Add-Check "Registration persists in database" ($persisted.StatusCode -eq 200)

    $invalidTransition = Invoke-ApiRequest -Method POST -Path "/api/v1/system/facilities/$facilityId/restore" -Token $adminAccessToken -Body @{}
    Add-Check "Invalid lifecycle transition returns 409" ($invalidTransition.StatusCode -eq 409)

    $rejected = Invoke-ApiRequest -Method POST -Path "/api/v1/system/facilities/$facilityId/reject" -Token $adminAccessToken -Body @{ reason = "Deployment smoke verification cleanup" }
    Add-Check "Smoke facility safely rejected" ($rejected.StatusCode -eq 204)
    $rejectedRecord = Invoke-ApiRequest -Method GET -Path "/api/v1/system/facilities/$facilityId" -Token $adminAccessToken
    $rejectedBody = $rejectedRecord.Content | ConvertFrom-Json
    Add-Check "Rejected facility state persists" ($rejectedRecord.StatusCode -eq 200 -and $rejectedBody.status -eq 2)

    $refresh = Invoke-ApiRequest -Method POST -Path "/api/v1/auth/refresh" -Body @{ refreshToken = $refreshToken }
    Add-Check "Refresh rotation succeeds" ($refresh.StatusCode -eq 200)
    if ($refresh.StatusCode -ne 200) { throw "Refresh rotation smoke check failed." }
    $rotated = $refresh.Content | ConvertFrom-Json
    $rotatedAccessToken = [string]$rotated.accessToken
    $rotatedRefreshToken = [string]$rotated.refreshToken

    $replay = Invoke-ApiRequest -Method POST -Path "/api/v1/auth/refresh" -Body @{ refreshToken = $refreshToken }
    Add-Check "Refresh replay is rejected" ($replay.StatusCode -eq 401)

    $logout = Invoke-ApiRequest -Method POST -Path "/api/v1/auth/logout" -Token $rotatedAccessToken
    Add-Check "Logout revokes session" ($logout.StatusCode -eq 204)
    $afterLogout = Invoke-ApiRequest -Method POST -Path "/api/v1/auth/refresh" -Body @{ refreshToken = $rotatedRefreshToken }
    Add-Check "Revoked refresh token is rejected" ($afterLogout.StatusCode -eq 401)

    $checks | Format-Table -AutoSize
    if (@($checks | Where-Object { $_.Result -eq "FAIL" }).Count -gt 0) { exit 1 }
} finally {
    if ($null -ne $rng) { $rng.Dispose() }
    if ($null -ne $randomBytes) { [Array]::Clear($randomBytes, 0, $randomBytes.Length) }
    if ($null -ne $bootstrapAdmin) { $bootstrapAdmin.password = $null }
    $bootstrapSecretJson = $null
    $bootstrapPassword = $null
    $adminAccessToken = $null
    $refreshToken = $null
    $rotatedAccessToken = $null
    $rotatedRefreshToken = $null
    $smokeAdminPassword = $null
    Remove-Variable bootstrap, bootstrapAdmin, login, loginBody, refresh, rotated, registration, registeredFacility, facilityPageBefore, facilityPageAfterDenied, missing, invalidRegistration, persisted, rejectedRecord -ErrorAction SilentlyContinue
}
