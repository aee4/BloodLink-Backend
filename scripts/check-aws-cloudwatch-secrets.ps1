param([string]$Region = "eu-north-1")

$ErrorActionPreference = "Stop"
$secretValues = [System.Collections.Generic.List[string]]::new()
$messages = [System.Collections.Generic.List[string]]::new()
$failure = $false

function Get-SecretJson([string]$Name) {
    $json = aws secretsmanager get-secret-value --region $Region --secret-id $Name --query SecretString --output text
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect configured secret metadata." }
    return ($json | ConvertFrom-Json)
}

try {
    $database = Get-SecretJson "bloodlink/prod/database"
    $authentication = Get-SecretJson "bloodlink/prod/authentication"
    $bootstrap = Get-SecretJson "bloodlink/prod/bootstrap"

    $connectionString = [string]$database.connectionStrings.defaultConnection
    $signingKey = [string]$authentication.api.tokens.signingKey
    $bootstrapPassword = [string]$bootstrap.bloodLink.bootstrapAdmin.password
    $secretValues.Add($connectionString)
    $secretValues.Add($signingKey)
    $secretValues.Add($bootstrapPassword)

    $passwordMatch = [regex]::Match($connectionString, '(?i)(?:Password|Pwd)=([^;]+)')
    if ($passwordMatch.Success) { $secretValues.Add($passwordMatch.Groups[1].Value) }

    $functions = aws cloudformation describe-stack-resources --region $Region --stack-name bloodlink-backend-prod --query "StackResources[?ResourceType=='AWS::Lambda::Function'].PhysicalResourceId" --output json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not discover deployed Lambda log groups." }

    $startTime = [DateTimeOffset]::UtcNow.AddDays(-1).ToUnixTimeMilliseconds()
    foreach ($functionName in $functions) {
        $logGroup = "/aws/lambda/$functionName"
        $nextToken = $null
        do {
            $arguments = @("logs", "filter-log-events", "--region", $Region, "--log-group-name", $logGroup, "--start-time", "$startTime", "--limit", "10000", "--output", "json")
            if ($nextToken) { $arguments += @("--next-token", $nextToken) }
            $pageJson = & aws @arguments
            if ($LASTEXITCODE -ne 0) { throw "Could not inspect deployed Lambda logs." }
            $page = $pageJson | ConvertFrom-Json
            foreach ($event in $page.events) { $messages.Add([string]$event.message) }
            $nextToken = $page.nextToken
        } while ($nextToken)
    }

    $secretMatches = 0
    foreach ($message in $messages) {
        foreach ($value in $secretValues) {
            if (-not [string]::IsNullOrEmpty($value) -and $message.Contains($value)) { $secretMatches++ }
        }
    }

    $authorizationMatches = @($messages | Where-Object { $_ -match '(?i)Authorization\s*[:=]\s*Bearer\s+\S+' }).Count
    $jwtMatches = @($messages | Where-Object { $_ -match 'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}' }).Count
    $tokenJsonMatches = @($messages | Where-Object { $_ -match '(?i)"(?:accessToken|refreshToken)"\s*:\s*"[^" ]{20,}' }).Count

    $results = @(
        [pscustomobject]@{ Check = "Inspected Lambda log events"; Result = $messages.Count },
        [pscustomobject]@{ Check = "Secret value matches"; Result = $secretMatches },
        [pscustomobject]@{ Check = "Authorization header matches"; Result = $authorizationMatches },
        [pscustomobject]@{ Check = "JWT-shaped values"; Result = $jwtMatches },
        [pscustomobject]@{ Check = "Access/refresh token fields"; Result = $tokenJsonMatches }
    )
    $results | Format-Table -AutoSize

    $failure = $secretMatches -gt 0 -or $authorizationMatches -gt 0 -or $jwtMatches -gt 0 -or $tokenJsonMatches -gt 0
} catch {
    Write-Error "CloudWatch disclosure scan could not complete. No secret values were printed."
    exit 1
} finally {
    $secretValues.Clear()
    $messages.Clear()
    $connectionString = $null
    $signingKey = $null
    $bootstrapPassword = $null
    $passwordMatch = $null
    $database = $null
    $authentication = $null
    $bootstrap = $null
}

if ($failure) { exit 1 }
