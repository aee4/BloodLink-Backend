[CmdletBinding()]
param(
    [ValidateSet("eu-north-1")]
    [string]$Region = "eu-north-1",
    [ValidatePattern('^[A-Za-z0-9+=,.@_-]+$')]
    [string]$ExpectedIamUser = "bloodlink-deployer",
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$ExpectedRdsSecurityGroupName = "bloodlink-production-rds",
    [switch]$NoRun
)

$ErrorActionPreference = "Stop"

function ConvertFrom-BloodLinkJson([string]$Json, [string]$Operation) {
    if ([string]::IsNullOrWhiteSpace($Json)) {
        throw "$Operation returned no JSON."
    }

    try {
        return ($Json | ConvertFrom-Json)
    } catch {
        throw "$Operation returned invalid JSON."
    }
}

function ConvertFrom-BloodLinkMigratorPayload([string]$Payload) {
    $result = ConvertFrom-BloodLinkJson $Payload "The migrator"
    if ($result -is [string]) {
        $result = ConvertFrom-BloodLinkJson $result "The migrator payload"
    }

    $required = @("success", "pendingBefore", "pendingAfter", "canonicalRoles", "systemAdminCount", "intendedSystemAdminCount")
    foreach ($name in $required) {
        if ($result.PSObject.Properties.Name -notcontains $name) {
            throw "The migrator payload is missing a required field."
        }
    }

    return $result
}

function Assert-BloodLinkLambdaInvocation($Metadata, $Payload) {
    if ($null -eq $Metadata -or [int]$Metadata.StatusCode -ne 200 -or
        -not [string]::IsNullOrWhiteSpace([string]$Metadata.FunctionError)) {
        throw "The private migrator Lambda invocation failed."
    }
    if ($Payload.success -ne $true) {
        throw "The private migrator did not report success."
    }
}

function Get-BloodLinkSqlIngressAssessment($Permissions, [string]$LambdaSecurityGroupId) {
    $sqlPermissions = @($Permissions | Where-Object {
        ($_.IpProtocol -eq "tcp" -or $_.IpProtocol -eq "-1") -and
        ($_.IpProtocol -eq "-1" -or
            ($null -ne $_.FromPort -and $null -ne $_.ToPort -and
                [int]$_.FromPort -le 1433 -and [int]$_.ToPort -ge 1433))
    })

    $ipv4 = @($sqlPermissions | ForEach-Object { @($_.IpRanges) } | Where-Object { $null -ne $_ })
    $ipv6 = @($sqlPermissions | ForEach-Object { @($_.Ipv6Ranges) } | Where-Object { $null -ne $_ })
    $prefixLists = @($sqlPermissions | ForEach-Object { @($_.PrefixListIds) } | Where-Object { $null -ne $_ })
    $groupPairs = @($sqlPermissions | ForEach-Object { @($_.UserIdGroupPairs) } | Where-Object { $null -ne $_ })
    $unexpectedGroups = @($groupPairs | Where-Object { $_.GroupId -ne $LambdaSecurityGroupId })

    [pscustomobject]@{
        SqlPermissionCount = $sqlPermissions.Count
        LambdaSourceCount = @($groupPairs | Where-Object { $_.GroupId -eq $LambdaSecurityGroupId }).Count
        HasWorldIpv4 = @($ipv4 | Where-Object { $_.CidrIp -eq "0.0.0.0/0" }).Count -gt 0
        HasWorldIpv6 = @($ipv6 | Where-Object { $_.CidrIpv6 -eq "::/0" }).Count -gt 0
        HasAnyCidr = ($ipv4.Count + $ipv6.Count) -gt 0
        HasPrefixList = $prefixLists.Count -gt 0
        UnexpectedGroupCount = $unexpectedGroups.Count
        IsRestrictedToLambda = $sqlPermissions.Count -gt 0 -and
            @($groupPairs | Where-Object { $_.GroupId -eq $LambdaSecurityGroupId }).Count -gt 0 -and
            ($ipv4.Count + $ipv6.Count + $prefixLists.Count + $unexpectedGroups.Count) -eq 0
    }
}

function Protect-BloodLinkText([string]$Text, [string[]]$SensitiveValues = @()) {
    $safe = [string]$Text
    foreach ($value in $SensitiveValues) {
        if (-not [string]::IsNullOrEmpty($value)) {
            $safe = $safe.Replace($value, "[REDACTED]")
        }
    }

    $patterns = @(
        '(?i)(authorization["'']?\s*[:=]\s*["'']?bearer\s+)[^\s"'',}]+' ,
        '(?i)((?:password|pwd|signingkey|accessToken|refreshToken)\s*["'']?\s*[:=]\s*["'']?)[^\s;"'',}]+' ,
        'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'
    )
    foreach ($pattern in $patterns) {
        $safe = [regex]::Replace($safe, $pattern, '${1}[REDACTED]')
    }
    return $safe
}

function Assert-BloodLinkEvidenceRedacted([string]$Json, [string[]]$SensitiveValues = @()) {
    foreach ($value in $SensitiveValues) {
        if (-not [string]::IsNullOrEmpty($value) -and $Json.Contains($value)) {
            throw "Sanitized evidence contains a sensitive runtime value."
        }
    }

    $forbidden = @(
        '(?i)authorization["'']?\s*[:=]\s*["'']?bearer\s+[^\s"'',}]+',
        '(?i)(?:password|pwd|signingkey|accessToken|refreshToken)\s*["'']?\s*[:=]\s*["'']?[^\s"'',}]{8,}',
        'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}',
        '(?i)(?:Server|Data Source)=[^;\r\n]+;.*(?:Password|Pwd)='
    )
    foreach ($pattern in $forbidden) {
        if ($Json -match $pattern) {
            throw "Sanitized evidence contains secret-shaped content."
        }
    }
}

function Invoke-BloodLinkAwsJson([string[]]$Arguments, [string]$Operation) {
    $output = @(& aws @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed. AWS CLI output was suppressed."
    }
    return ConvertFrom-BloodLinkJson ($output -join [Environment]::NewLine) $Operation
}

function Invoke-BloodLinkApiRequest(
    [string]$ApiUrl,
    [string]$Method,
    [string]$Path,
    [string]$Token = "",
    [object]$Body = $null,
    [string]$Origin = "",
    [string]$RequestedMethod = "",
    [string]$RequestedHeaders = ""
) {
    $headers = @{}
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    if ($Origin) { $headers.Origin = $Origin }
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
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Headers = $response.Headers; Content = [string]$response.Content }
    } catch {
        $response = $_.Exception.Response
        if ($null -eq $response) { throw "The live HTTPS request failed without an HTTP response." }
        $content = ""
        try {
            if ($null -ne $response.Content -and $null -ne $response.Content.ReadAsStringAsync) {
                $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            } else {
                $stream = $response.GetResponseStream()
                if ($null -ne $stream) {
                    $reader = [IO.StreamReader]::new($stream)
                    $content = $reader.ReadToEnd()
                    $reader.Dispose()
                }
            }
        } catch {
            $content = ""
        }
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Headers = $response.Headers; Content = $content }
    }
}

function Get-BloodLinkHeader($Headers, [string]$Name) {
    if ($Headers -is [Collections.IDictionary]) {
        foreach ($key in @($Headers.Keys)) {
            if ([string]$key -ieq $Name) { return [string]$Headers[$key] }
        }
    } elseif ($null -ne $Headers) {
        $values = $null
        if ($Headers.TryGetValues($Name, [ref]$values)) { return [string](@($values) -join ",") }
    }
    return ""
}

function Test-BloodLinkClientDisclosure([string]$Content, [string[]]$SensitiveValues = @()) {
    if ($Content -match '(?im)stack trace|System\.[A-Za-z]+Exception|SqlException|(?:Server|Data Source)=[^;]+;|Authorization["'']?\s*:\s*["'']?Bearer|^\s+at\s+\S+\(') {
        return $true
    }
    foreach ($value in $SensitiveValues) {
        if (-not [string]::IsNullOrEmpty($value) -and $Content.Contains($value)) { return $true }
    }
    return $false
}

function Invoke-BloodLinkMigrator([string]$FunctionName, [string]$Region, [string]$OutputPath) {
    $metadata = Invoke-BloodLinkAwsJson @(
        "lambda", "invoke", "--region", $Region, "--function-name", $FunctionName,
        "--invocation-type", "RequestResponse", "--cli-binary-format", "raw-in-base64-out",
        "--payload", "{}", $OutputPath, "--output", "json"
    ) "Private migrator invocation"
    $payloadText = [IO.File]::ReadAllText($OutputPath)
    $payload = ConvertFrom-BloodLinkMigratorPayload $payloadText
    Assert-BloodLinkLambdaInvocation $metadata $payload
    Remove-Item -LiteralPath $OutputPath -Force
    return $payload
}

function Get-BloodLinkRoleCounts($MigrationResult) {
    $counts = @{}
    foreach ($item in @($MigrationResult.canonicalRoles)) {
        $counts[[string]$item.name] = [int]$item.count
    }
    return $counts
}

function Assert-BloodLinkMigrationResult($Result, [int]$ExpectedMigrationCount) {
    if ([int]$Result.pendingAfter -ne 0) { throw "Database migration left pending migrations." }
    $roles = Get-BloodLinkRoleCounts $Result
    foreach ($role in @("SystemAdmin", "FacilityAdmin", "FacilityStaff")) {
        if (-not $roles.ContainsKey($role) -or [int]$roles[$role] -ne 1) {
            throw "Canonical role verification failed."
        }
    }
    if ([int]$Result.systemAdminCount -ne 1) { throw "Expected exactly one SystemAdmin." }
    if ($ExpectedMigrationCount -lt 1) { throw "No local EF migrations were found." }
}

function Get-BloodLinkLambdaLogMessages([string[]]$FunctionNames, [string]$Region, [long]$StartTime) {
    $messages = [System.Collections.Generic.List[string]]::new()
    foreach ($functionName in $FunctionNames) {
        $nextToken = $null
        $seenTokens = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        do {
            $arguments = @("logs", "filter-log-events", "--region", $Region, "--log-group-name", "/aws/lambda/$functionName",
                "--start-time", [string]$StartTime, "--limit", "10000", "--output", "json")
            if ($nextToken) { $arguments += @("--next-token", [string]$nextToken) }
            $page = Invoke-BloodLinkAwsJson $arguments "CloudWatch log inspection"
            foreach ($event in @($page.events)) { $messages.Add([string]$event.message) }
            $candidateToken = [string]$page.nextToken
            $nextToken = $(if (-not [string]::IsNullOrWhiteSpace($candidateToken) -and $seenTokens.Add($candidateToken)) {
                $candidateToken
            } else {
                $null
            })
        } while (-not [string]::IsNullOrWhiteSpace($nextToken))
    }
    return $messages
}

function Get-BloodLinkDisclosureCounts($Messages, [string[]]$RuntimeSecrets = @()) {
    $patterns = [ordered]@{
        connectionStrings = '(?i)(?:Server|Data Source)=[^;\r\n]+;.*(?:Database|Initial Catalog)='
        databasePasswords = '(?i)(?:Password|Pwd)\s*=\s*[^;\s]+'
        passwordFields = '(?i)["'']?(?:password|pwd)["'']?\s*[:=]\s*["'']?\S{8,}'
        signingKeys = '(?i)(?:signingKey|signing_key)\s*["'']?\s*[:=]\s*["'']?\S{16,}'
        bootstrapPasswords = '(?i)bootstrap.{0,40}password\s*["'']?\s*[:=]\s*["'']?\S{8,}'
        accessTokens = '(?i)accessToken\s*["'']?\s*[:=]\s*["'']?\S{20,}'
        refreshTokens = '(?i)refreshToken\s*["'']?\s*[:=]\s*["'']?\S{20,}'
        authorizationHeaders = '(?i)Authorization["'']?\s*[:=]\s*["'']?Bearer\s+\S+'
        jwtValues = 'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'
    }
    $result = [ordered]@{}
    foreach ($entry in $patterns.GetEnumerator()) {
        $result[$entry.Key] = @($Messages | Where-Object { $_ -match $entry.Value }).Count
    }
    $result.runtimeSecretMatches = 0
    foreach ($message in $Messages) {
        foreach ($secret in $RuntimeSecrets) {
            if (-not [string]::IsNullOrEmpty($secret) -and $message.Contains($secret)) { $result.runtimeSecretMatches++ }
        }
    }
    return [pscustomobject]$result
}

function Write-BloodLinkEvidence([string]$Path, $Evidence, [string[]]$SensitiveValues = @()) {
    $json = ConvertTo-Json -InputObject $Evidence -Depth 12
    Assert-BloodLinkEvidenceRedacted $json $SensitiveValues
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Invoke-BloodLinkProductionVerification {
    param([string]$Region, [string]$ExpectedIamUser, [string]$ExpectedRdsSecurityGroupName)

    if ($Region -ne "eu-north-1") { throw "This verification is restricted to eu-north-1." }
    if ($null -eq (Get-Command aws -ErrorAction SilentlyContinue)) { throw "AWS CLI is required on PATH." }
    if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ".NET SDK is required on PATH." }

    $repoRoot = Split-Path -Parent $PSScriptRoot
    if ((Get-Location).Path -ne $repoRoot) { throw "Run this script from the repository root." }
    $evidencePath = Join-Path $repoRoot "artifacts\aws-production-verification.json"
    $temporaryFiles = [System.Collections.Generic.List[string]]::new()
    $checks = [System.Collections.Generic.List[object]]::new()
    $clientBodies = [System.Collections.Generic.List[string]]::new()
    $sensitiveValues = [System.Collections.Generic.List[string]]::new()
    $startedAt = [DateTimeOffset]::UtcNow
    $adminAccessToken = $null
    $refreshToken = $null
    $rotatedAccessToken = $null
    $rotatedRefreshToken = $null
    $bootstrapPasswordPlain = $null
    $bootstrapPasswordSecure = $null
    $bootstrapPasswordBstr = [IntPtr]::Zero
    $smokePassword = $null
    $smokePasswordBytes = $null
    $rng = $null
    $smokeFacilityId = $null
    $apiUrl = $null
    $expectedApiUrl = "https://wvsrmqrfc0.execute-api.eu-north-1.amazonaws.com"
    $expectedFrontendOrigin = "https://d2z1pcfp95dfwd.cloudfront.net"
    $evidence = [ordered]@{
        schemaVersion = 1
        startedAtUtc = $startedAt.ToString("o")
        completedAtUtc = $null
        result = "IN_PROGRESS"
        region = $Region
        stack = [ordered]@{}
        identity = [ordered]@{}
        database = [ordered]@{}
        securityGroups = [ordered]@{}
        migrations = [ordered]@{}
        bootstrap = [ordered]@{}
        api = [ordered]@{}
        cloudWatch = [ordered]@{}
        checks = $checks
    }

    function Add-LiveCheck([string]$Name, [bool]$Passed, [string]$Detail = "") {
        $checks.Add([pscustomobject]@{ name = $Name; status = $(if ($Passed) { "PASS" } else { "FAIL" }); detail = $Detail })
        if (-not $Passed) { throw "Verification check failed: $Name" }
    }

    try {
        $env:AWS_PAGER = ""
        $identity = Invoke-BloodLinkAwsJson @("sts", "get-caller-identity", "--region", $Region, "--output", "json") "AWS identity check"
        $expectedArnSuffix = ":user/$ExpectedIamUser"
        Add-LiveCheck "Active identity is the expected IAM deployment user" (
            [string]$identity.Arn -like "arn:aws:iam::*$expectedArnSuffix" -and
            [string]$identity.Arn -notmatch ':root$' -and [string]$identity.Arn -notmatch ':assumed-role/'
        )
        $evidence.identity.accountId = [string]$identity.Account
        $evidence.identity.principalArn = [string]$identity.Arn
        $evidence.identity.root = $false

        $stackResponse = Invoke-BloodLinkAwsJson @("cloudformation", "describe-stacks", "--region", $Region,
            "--stack-name", "bloodlink-backend-prod", "--output", "json") "Stack inspection"
        $stack = @($stackResponse.Stacks)[0]
        Add-LiveCheck "Stack is UPDATE_COMPLETE" ([string]$stack.StackStatus -eq "UPDATE_COMPLETE")
        $outputs = @{}
        foreach ($item in @($stack.Outputs)) { $outputs[[string]$item.OutputKey] = [string]$item.OutputValue }
        foreach ($requiredOutput in @("ApiUrl", "LambdaSecurityGroupId", "MigratorFunctionName")) {
            Add-LiveCheck "Stack output $requiredOutput exists" $outputs.ContainsKey($requiredOutput)
        }
        $apiUri = $null
        $apiUrl = $outputs.ApiUrl.TrimEnd('/')
        Add-LiveCheck "API output is HTTPS" ([Uri]::TryCreate($apiUrl, [UriKind]::Absolute, [ref]$apiUri) -and $apiUri.Scheme -eq "https")
        Add-LiveCheck "Production API URL is unchanged" ($apiUrl -eq $expectedApiUrl)
        $parameters = @{}
        foreach ($item in @($stack.Parameters)) { $parameters[[string]$item.ParameterKey] = [string]$item.ParameterValue }
        $expectedVpcId = $parameters.VpcId
        Add-LiveCheck "Stack VPC parameter exists" (-not [string]::IsNullOrWhiteSpace($expectedVpcId))
        Add-LiveCheck "Stack CORS parameter is the exact CloudFront origin" ($parameters.CorsOrigin -eq $expectedFrontendOrigin)
        $evidence.stack.name = "bloodlink-backend-prod"
        $evidence.stack.status = [string]$stack.StackStatus
        $evidence.stack.id = [string]$stack.StackId
        $evidence.stack.apiUrl = $apiUrl
        $evidence.stack.corsOrigin = [string]$parameters.CorsOrigin
        $evidence.stack.migratorFunctionName = $outputs.MigratorFunctionName
        $evidence.stack.lambdaSecurityGroupId = $outputs.LambdaSecurityGroupId
        $evidence.stack.vpcId = $expectedVpcId

        $apiId = $apiUri.Host.Split('.')[0]
        $gateway = Invoke-BloodLinkAwsJson @("apigatewayv2", "get-api", "--region", $Region,
            "--api-id", $apiId, "--output", "json") "API Gateway CORS inspection"
        $gatewayOrigins = @($gateway.CorsConfiguration.AllowOrigins | ForEach-Object { [string]$_ })
        Add-LiveCheck "API Gateway allows only the exact CloudFront origin" (
            $gatewayOrigins.Count -eq 1 -and $gatewayOrigins[0] -eq $expectedFrontendOrigin -and
            @($gatewayOrigins | Where-Object { $_ -eq "*" }).Count -eq 0
        )
        $gatewayMethods = @($gateway.CorsConfiguration.AllowMethods | ForEach-Object { ([string]$_).ToUpperInvariant() })
        Add-LiveCheck "API Gateway CORS methods are GET, POST, and PUT only" (
            $gatewayMethods.Count -eq 3 -and @("GET", "POST", "PUT" | Where-Object { $gatewayMethods -notcontains $_ }).Count -eq 0
        )
        $gatewayHeaders = @($gateway.CorsConfiguration.AllowHeaders | ForEach-Object { ([string]$_).ToLowerInvariant() })
        Add-LiveCheck "API Gateway CORS headers are Authorization and Content-Type only" (
            $gatewayHeaders.Count -eq 2 -and @("authorization", "content-type" | Where-Object { $gatewayHeaders -notcontains $_ }).Count -eq 0
        )
        $apiFunctionResource = Invoke-BloodLinkAwsJson @("cloudformation", "describe-stack-resources", "--region", $Region,
            "--stack-name", "bloodlink-backend-prod", "--output", "json") "API Lambda resource discovery"
        $apiFunction = @($apiFunctionResource.StackResources | Where-Object { $_.LogicalResourceId -eq "ApiFunction" })
        Add-LiveCheck "API Lambda function was discovered" ($apiFunction.Count -eq 1)
        $lambdaCorsOrigin = Invoke-BloodLinkAwsJson @("lambda", "get-function-configuration", "--region", $Region,
            "--function-name", [string]$apiFunction[0].PhysicalResourceId,
            "--query", "Environment.Variables.Api__AllowedOrigins__0", "--output", "json") "API Lambda CORS inspection"
        Add-LiveCheck "API Lambda allows the exact CloudFront origin" ([string]$lambdaCorsOrigin -eq $expectedFrontendOrigin)

        $rdsResponse = Invoke-BloodLinkAwsJson @("rds", "describe-db-instances", "--region", $Region,
            "--db-instance-identifier", "bloodlink-db", "--output", "json") "RDS inspection"
        $database = @($rdsResponse.DBInstances)[0]
        Add-LiveCheck "RDS is available" ([string]$database.DBInstanceStatus -eq "available")
        Add-LiveCheck "RDS is private" ($database.PubliclyAccessible -eq $false)
        Add-LiveCheck "RDS is in the stack VPC" ([string]$database.DBSubnetGroup.VpcId -eq $expectedVpcId)
        $rdsSecurityGroupIds = @($database.VpcSecurityGroups | ForEach-Object { [string]$_.VpcSecurityGroupId })
        Add-LiveCheck "RDS has exactly one security group attached" ($rdsSecurityGroupIds.Count -eq 1)
        $evidence.database.identifier = [string]$database.DBInstanceIdentifier
        $evidence.database.status = [string]$database.DBInstanceStatus
        $evidence.database.publiclyAccessible = [bool]$database.PubliclyAccessible
        $evidence.database.vpcId = [string]$database.DBSubnetGroup.VpcId
        $evidence.database.securityGroupIds = $rdsSecurityGroupIds

        $sgArguments = @("ec2", "describe-security-groups", "--region", $Region, "--group-ids") +
            $rdsSecurityGroupIds + @("--output", "json")
        $sgResponse = Invoke-BloodLinkAwsJson $sgArguments "RDS security-group inspection"
        $attachedRdsGroups = @($sgResponse.SecurityGroups)
        $dedicatedRdsGroup = @($attachedRdsGroups | Where-Object { $_.GroupName -eq $ExpectedRdsSecurityGroupName })
        Add-LiveCheck "Dedicated production RDS security group is attached" ($dedicatedRdsGroup.Count -eq 1)
        Add-LiveCheck "VPC default security group is detached from RDS" (
            @($attachedRdsGroups | Where-Object { $_.GroupName -eq "default" }).Count -eq 0
        )
        $expectedRdsSecurityGroupId = [string]$dedicatedRdsGroup[0].GroupId
        $allRdsPermissions = @($sgResponse.SecurityGroups | ForEach-Object { @($_.IpPermissions) })
        $assessment = Get-BloodLinkSqlIngressAssessment $allRdsPermissions $outputs.LambdaSecurityGroupId
        Add-LiveCheck "SQL ingress excludes IPv4 world access" (-not $assessment.HasWorldIpv4)
        Add-LiveCheck "SQL ingress excludes IPv6 world access" (-not $assessment.HasWorldIpv6)
        Add-LiveCheck "SQL ingress is restricted to the Lambda security group" $assessment.IsRestrictedToLambda
        $lambdaSgResponse = Invoke-BloodLinkAwsJson @("ec2", "describe-security-groups", "--region", $Region,
            "--group-ids", $outputs.LambdaSecurityGroupId, "--output", "json") "Lambda security-group inspection"
        $lambdaSqlEgress = @($lambdaSgResponse.SecurityGroups[0].IpPermissionsEgress | Where-Object {
            $_.IpProtocol -eq "tcp" -and [int]$_.FromPort -le 1433 -and [int]$_.ToPort -ge 1433 -and
            @($_.UserIdGroupPairs | Where-Object { $_.GroupId -eq $expectedRdsSecurityGroupId }).Count -gt 0
        })
        Add-LiveCheck "Lambda SQL egress targets the dedicated RDS security group" ($lambdaSqlEgress.Count -gt 0)
        $evidence.securityGroups.rdsSecurityGroupId = $expectedRdsSecurityGroupId
        $evidence.securityGroups.rdsSecurityGroupName = $ExpectedRdsSecurityGroupName
        $evidence.securityGroups.lambdaSecurityGroupId = $outputs.LambdaSecurityGroupId
        $evidence.securityGroups.sqlPermissionCount = $assessment.SqlPermissionCount
        $evidence.securityGroups.restrictedToLambda = $assessment.IsRestrictedToLambda
        $evidence.securityGroups.worldIpv4 = $assessment.HasWorldIpv4
        $evidence.securityGroups.worldIpv6 = $assessment.HasWorldIpv6
        $evidence.securityGroups.stackParameterRdsSecurityGroupId = [string]$parameters.RdsSecurityGroupId
        $evidence.securityGroups.stackParameterMatchesAttachment = [string]$parameters.RdsSecurityGroupId -eq $expectedRdsSecurityGroupId

        $migrationFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src\BloodLink.Infrastructure\Migrations") -Filter "*.cs" |
            Where-Object { $_.Name -match '^\d+_.+\.cs$' -and $_.Name -notmatch '\.Designer\.cs$' })
        $expectedMigrationCount = $migrationFiles.Count
        $firstOutput = [IO.Path]::GetTempFileName()
        $temporaryFiles.Add($firstOutput)
        $firstMigration = Invoke-BloodLinkMigrator $outputs.MigratorFunctionName $Region $firstOutput
        $temporaryFiles.Remove($firstOutput) | Out-Null
        Assert-BloodLinkMigrationResult $firstMigration $expectedMigrationCount
        Add-LiveCheck "Migration history matches all deployed migrations" ([int]$firstMigration.pendingAfter -eq 0) "$expectedMigrationCount expected; zero pending"
        Add-LiveCheck "Exactly one SystemAdmin exists" ([int]$firstMigration.systemAdminCount -eq 1)

        $secondOutput = [IO.Path]::GetTempFileName()
        $temporaryFiles.Add($secondOutput)
        $secondMigration = Invoke-BloodLinkMigrator $outputs.MigratorFunctionName $Region $secondOutput
        $temporaryFiles.Remove($secondOutput) | Out-Null
        Assert-BloodLinkMigrationResult $secondMigration $expectedMigrationCount
        $firstRoles = Get-BloodLinkRoleCounts $firstMigration
        $secondRoles = Get-BloodLinkRoleCounts $secondMigration
        $stable = [int]$secondMigration.pendingBefore -eq 0 -and [int]$secondMigration.pendingAfter -eq 0 -and
            [int]$firstMigration.systemAdminCount -eq [int]$secondMigration.systemAdminCount -and
            (@("SystemAdmin", "FacilityAdmin", "FacilityStaff") | Where-Object { $firstRoles[$_] -ne $secondRoles[$_] }).Count -eq 0
        Add-LiveCheck "Repeated migration/bootstrap invocation is idempotent" $stable
        $evidence.migrations.expectedCount = $expectedMigrationCount
        $evidence.migrations.firstPendingBefore = [int]$firstMigration.pendingBefore
        $evidence.migrations.firstPendingAfter = [int]$firstMigration.pendingAfter
        $evidence.migrations.secondPendingBefore = [int]$secondMigration.pendingBefore
        $evidence.migrations.secondPendingAfter = [int]$secondMigration.pendingAfter
        $evidence.bootstrap.systemAdminCount = [int]$secondMigration.systemAdminCount
        $evidence.bootstrap.roleCounts = [ordered]@{
            SystemAdmin = [int]$secondRoles.SystemAdmin
            FacilityAdmin = [int]$secondRoles.FacilityAdmin
            FacilityStaff = [int]$secondRoles.FacilityStaff
        }
        $evidence.bootstrap.idempotent = $stable

        $lambda = Invoke-BloodLinkAwsJson @("lambda", "get-function-configuration", "--region", $Region,
            "--function-name", $outputs.MigratorFunctionName, "--output", "json") "Migrator configuration inspection"
        $secretNames = [string]$lambda.Environment.Variables.BloodLink__AwsSecrets__Names
        $bootstrapNotAttached = $secretNames -notmatch 'bloodlink/prod/bootstrap'
        Add-LiveCheck "Bootstrap secret is detached from the migrator" $bootstrapNotAttached
        $roleName = ([string]$lambda.Role -split '/')[-1]
        $policyNames = Invoke-BloodLinkAwsJson @("iam", "list-role-policies", "--role-name", $roleName, "--output", "json") "Migrator IAM inspection"
        $bootstrapPolicyReferences = 0
        foreach ($policyName in @($policyNames.PolicyNames)) {
            $policy = Invoke-BloodLinkAwsJson @("iam", "get-role-policy", "--role-name", $roleName,
                "--policy-name", [string]$policyName, "--output", "json") "Migrator IAM policy inspection"
            if ((ConvertTo-Json $policy.PolicyDocument -Depth 20 -Compress) -match 'bloodlink/prod/bootstrap') { $bootstrapPolicyReferences++ }
        }
        Add-LiveCheck "Bootstrap secret is detached from migrator IAM" ($bootstrapPolicyReferences -eq 0)

        $helperProject = Join-Path $repoRoot "src\BloodLink.SecretConfigurator\BloodLink.SecretConfigurator.csproj"
        $helperOutput = @(& dotnet run --project $helperProject --configuration Release --no-restore -- --disable-bootstrap 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "The bootstrap disable operation failed; helper output was suppressed." }
        $helperOutput = $null
        $evidence.bootstrap.secretDisabled = $true
        $evidence.bootstrap.migratorSecretDetached = $bootstrapNotAttached
        $evidence.bootstrap.migratorIamDetached = $bootstrapPolicyReferences -eq 0

        $corsPreflight = Invoke-BloodLinkApiRequest $apiUrl OPTIONS "/health" "" $null `
            $expectedFrontendOrigin POST "authorization,content-type"
        $corsOriginHeader = Get-BloodLinkHeader $corsPreflight.Headers "Access-Control-Allow-Origin"
        Add-LiveCheck "CloudFront CORS preflight succeeds" ($corsPreflight.StatusCode -in @(200, 204))
        Add-LiveCheck "CloudFront CORS preflight returns the exact origin" ($corsOriginHeader -eq $expectedFrontendOrigin)
        Add-LiveCheck "CORS does not emit a wildcard origin" ($corsOriginHeader -ne "*")
        Add-LiveCheck "CloudFront preflight permits POST" (
            ((Get-BloodLinkHeader $corsPreflight.Headers "Access-Control-Allow-Methods").Split(',') |
                ForEach-Object { $_.Trim().ToUpperInvariant() }) -contains "POST"
        )
        $corsAllowedHeaders = @((Get-BloodLinkHeader $corsPreflight.Headers "Access-Control-Allow-Headers").Split(',') |
            ForEach-Object { $_.Trim().ToLowerInvariant() })
        Add-LiveCheck "CloudFront preflight permits Authorization and Content-Type" (
            $corsAllowedHeaders -contains "authorization" -and $corsAllowedHeaders -contains "content-type"
        )
        Add-LiveCheck "CORS does not enable credentials" (
            [string]::IsNullOrWhiteSpace((Get-BloodLinkHeader $corsPreflight.Headers "Access-Control-Allow-Credentials"))
        )
        $normalCorsResponse = Invoke-BloodLinkApiRequest $apiUrl GET "/health" "" $null $expectedFrontendOrigin
        Add-LiveCheck "Normal API response includes the exact CloudFront origin" (
            $normalCorsResponse.StatusCode -eq 200 -and
            (Get-BloodLinkHeader $normalCorsResponse.Headers "Access-Control-Allow-Origin") -eq $expectedFrontendOrigin
        )
        foreach ($untrustedOrigin in @(
            "https://example.com",
            "https://evil.example",
            "http://d2z1pcfp95dfwd.cloudfront.net",
            "https://d2z1pcfp95dfwd.cloudfront.net.evil.example"
        )) {
            $deniedCors = Invoke-BloodLinkApiRequest $apiUrl OPTIONS "/health" "" $null $untrustedOrigin POST "authorization,content-type"
            $deniedOriginHeader = Get-BloodLinkHeader $deniedCors.Headers "Access-Control-Allow-Origin"
            Add-LiveCheck "Untrusted CORS origin is denied: $untrustedOrigin" ([string]::IsNullOrWhiteSpace($deniedOriginHeader))
        }

        $bootstrapEmail = Read-Host "Bootstrap SystemAdmin email"
        if ([string]::IsNullOrWhiteSpace($bootstrapEmail)) { throw "Bootstrap SystemAdmin email is required for live login." }
        $bootstrapPasswordSecure = Read-Host "Bootstrap SystemAdmin password" -AsSecureString
        $bootstrapPasswordBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($bootstrapPasswordSecure)
        $bootstrapPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bootstrapPasswordBstr)
        $sensitiveValues.Add($bootstrapPasswordPlain)

        $health = Invoke-BloodLinkApiRequest $apiUrl GET "/health"
        $clientBodies.Add($health.Content)
        Add-LiveCheck "HTTPS health endpoint" ($health.StatusCode -eq 200)
        $readiness = Invoke-BloodLinkApiRequest $apiUrl GET "/health/ready"
        $clientBodies.Add($readiness.Content)
        Add-LiveCheck "Database readiness endpoint" ($readiness.StatusCode -eq 200)
        Add-LiveCheck "Health request without Origin remains available" (
            $health.StatusCode -eq 200 -and
            [string]::IsNullOrWhiteSpace((Get-BloodLinkHeader $health.Headers "Access-Control-Allow-Origin"))
        )

        $login = Invoke-BloodLinkApiRequest $apiUrl POST "/api/v1/auth/login" "" @{ email = $bootstrapEmail; password = $bootstrapPasswordPlain }
        Add-LiveCheck "SystemAdmin login" ($login.StatusCode -eq 200)
        $loginBody = ConvertFrom-BloodLinkJson $login.Content "Login"
        $adminAccessToken = [string]$loginBody.accessToken
        $refreshToken = [string]$loginBody.refreshToken
        $sensitiveValues.Add($adminAccessToken)
        $sensitiveValues.Add($refreshToken)
        Add-LiveCheck "Access token is issued" (-not [string]::IsNullOrWhiteSpace($adminAccessToken))
        Add-LiveCheck "Refresh token is issued" (-not [string]::IsNullOrWhiteSpace($refreshToken))
        Add-LiveCheck "Intended bootstrap account is the sole facility-less SystemAdmin" (
            @($loginBody.user.roles).Count -eq 1 -and @($loginBody.user.roles) -contains "SystemAdmin" -and
            $null -eq $loginBody.user.facilityId -and [int]$secondMigration.systemAdminCount -eq 1
        )

        $me = Invoke-BloodLinkApiRequest $apiUrl GET "/api/v1/auth/me" $adminAccessToken
        $clientBodies.Add($me.Content)
        Add-LiveCheck "Access token reaches a protected endpoint" ($me.StatusCode -eq 200)

        $facilityPageBeforeResponse = Invoke-BloodLinkApiRequest $apiUrl GET "/api/v1/system/facilities?page=1&pageSize=100" $adminAccessToken
        Add-LiveCheck "SystemAdmin facility snapshot is available" ($facilityPageBeforeResponse.StatusCode -eq 200)
        $facilityPageBefore = ConvertFrom-BloodLinkJson $facilityPageBeforeResponse.Content "Facility snapshot"
        $facilityCountBefore = [int]$facilityPageBefore.totalCount

        $unauthorized = Invoke-BloodLinkApiRequest $apiUrl GET "/api/v1/auth/me"
        $clientBodies.Add($unauthorized.Content)
        Add-LiveCheck "Protected endpoint returns 401 without a token" ($unauthorized.StatusCode -eq 401)
        $wrongRole = Invoke-BloodLinkApiRequest $apiUrl GET "/api/v1/facilities/me" $adminAccessToken
        $clientBodies.Add($wrongRole.Content)
        Add-LiveCheck "Wrong-role endpoint returns 403" ($wrongRole.StatusCode -eq 403)
        $missingId = [guid]::NewGuid().ToString()
        $missing = Invoke-BloodLinkApiRequest $apiUrl GET "/api/v1/system/facilities/$missingId" $adminAccessToken
        $clientBodies.Add($missing.Content)
        Add-LiveCheck "Missing private record returns safe 404" ($missing.StatusCode -eq 404 -and -not (Test-BloodLinkClientDisclosure $missing.Content @($sensitiveValues)))
        $invalid = Invoke-BloodLinkApiRequest $apiUrl POST "/api/v1/facilities/register" "" @{}
        $clientBodies.Add($invalid.Content)
        Add-LiveCheck "Invalid input returns safe 400" ($invalid.StatusCode -eq 400 -and -not (Test-BloodLinkClientDisclosure $invalid.Content @($sensitiveValues)))

        $afterDeniedResponse = Invoke-BloodLinkApiRequest $apiUrl GET "/api/v1/system/facilities?page=1&pageSize=100" $adminAccessToken
        $afterDenied = ConvertFrom-BloodLinkJson $afterDeniedResponse.Content "Post-denial facility snapshot"
        Add-LiveCheck "Denied and invalid operations caused no facility writes" ([int]$afterDenied.totalCount -eq $facilityCountBefore)

        $suffix = [guid]::NewGuid().ToString("N")
        $smokePasswordBytes = [byte[]]::new(32)
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($smokePasswordBytes)
        $smokePassword = [Convert]::ToBase64String($smokePasswordBytes) + "Aa1!"
        $sensitiveValues.Add($smokePassword)
        $registrationBody = @{
            name = "BloodLink Verification $suffix"; facilityType = 0; registrationNumber = "VERIFY-$suffix"
            region = "Greater Accra"; city = "Accra"; address = "Production verification"
            contactEmail = "verify-$suffix@example.invalid"; contactPhone = "0200000000"
            adminFirstName = "Production"; adminLastName = "Verification"
            adminEmail = "verify-admin-$suffix@example.invalid"; adminPhoneNumber = "0200000001"; adminPassword = $smokePassword
        }
        $registration = Invoke-BloodLinkApiRequest $apiUrl POST "/api/v1/facilities/register" "" $registrationBody
        $clientBodies.Add($registration.Content)
        Add-LiveCheck "Verification facility registration succeeds" ($registration.StatusCode -eq 201)
        $registered = ConvertFrom-BloodLinkJson $registration.Content "Verification registration"
        $smokeFacilityId = [string]$registered.id
        $beforeConflictResponse = Invoke-BloodLinkApiRequest $apiUrl GET "/api/v1/system/facilities?page=1&pageSize=100" $adminAccessToken
        $beforeConflict = ConvertFrom-BloodLinkJson $beforeConflictResponse.Content "Pre-conflict facility snapshot"
        $conflict = Invoke-BloodLinkApiRequest $apiUrl POST "/api/v1/system/facilities/$smokeFacilityId/restore" $adminAccessToken @{}
        $clientBodies.Add($conflict.Content)
        Add-LiveCheck "Invalid lifecycle transition returns safe 409" ($conflict.StatusCode -eq 409 -and -not (Test-BloodLinkClientDisclosure $conflict.Content @($sensitiveValues)))
        $afterConflictResponse = Invoke-BloodLinkApiRequest $apiUrl GET "/api/v1/system/facilities?page=1&pageSize=100" $adminAccessToken
        $afterConflict = ConvertFrom-BloodLinkJson $afterConflictResponse.Content "Post-conflict facility snapshot"
        Add-LiveCheck "Denied conflict caused no facility write" ([int]$beforeConflict.totalCount -eq [int]$afterConflict.totalCount)
        $reject = Invoke-BloodLinkApiRequest $apiUrl POST "/api/v1/system/facilities/$smokeFacilityId/reject" $adminAccessToken @{ reason = "Production verification cleanup" }
        Add-LiveCheck "Verification facility is placed in rejected state" ($reject.StatusCode -eq 204)

        $refresh = Invoke-BloodLinkApiRequest $apiUrl POST "/api/v1/auth/refresh" "" @{ refreshToken = $refreshToken }
        Add-LiveCheck "Refresh rotation succeeds" ($refresh.StatusCode -eq 200)
        $refreshBody = ConvertFrom-BloodLinkJson $refresh.Content "Refresh rotation"
        $rotatedAccessToken = [string]$refreshBody.accessToken
        $rotatedRefreshToken = [string]$refreshBody.refreshToken
        $sensitiveValues.Add($rotatedAccessToken)
        $sensitiveValues.Add($rotatedRefreshToken)
        Add-LiveCheck "Refresh rotation returns new credentials" (
            -not [string]::IsNullOrWhiteSpace($rotatedAccessToken) -and -not [string]::IsNullOrWhiteSpace($rotatedRefreshToken) -and
            $rotatedRefreshToken -ne $refreshToken
        )
        $replay = Invoke-BloodLinkApiRequest $apiUrl POST "/api/v1/auth/refresh" "" @{ refreshToken = $refreshToken }
        $clientBodies.Add($replay.Content)
        Add-LiveCheck "Refresh replay is denied" ($replay.StatusCode -eq 401)
        $logout = Invoke-BloodLinkApiRequest $apiUrl POST "/api/v1/auth/logout" $rotatedAccessToken
        Add-LiveCheck "Logout revokes the session" ($logout.StatusCode -eq 204)
        $adminAccessToken = $null
        $rotatedAccessToken = $null
        $afterLogout = Invoke-BloodLinkApiRequest $apiUrl POST "/api/v1/auth/refresh" "" @{ refreshToken = $rotatedRefreshToken }
        $clientBodies.Add($afterLogout.Content)
        Add-LiveCheck "Revoked refresh token is denied" ($afterLogout.StatusCode -eq 401)
        $refreshToken = $null
        $rotatedRefreshToken = $null

        $clientDisclosureCount = @($clientBodies | Where-Object { Test-BloodLinkClientDisclosure $_ @($sensitiveValues) }).Count
        Add-LiveCheck "Client responses disclose no secrets or stack traces" ($clientDisclosureCount -eq 0)

        Start-Sleep -Seconds 10
        $resources = Invoke-BloodLinkAwsJson @("cloudformation", "describe-stack-resources", "--region", $Region,
            "--stack-name", "bloodlink-backend-prod", "--output", "json") "Lambda resource discovery"
        $functionNames = @($resources.StackResources | Where-Object { $_.ResourceType -eq "AWS::Lambda::Function" } |
            ForEach-Object { [string]$_.PhysicalResourceId })
        Add-LiveCheck "Relevant Lambda log groups were discovered" ($functionNames.Count -ge 2)
        $logStart = $startedAt.AddHours(-24).ToUnixTimeMilliseconds()
        $messages = Get-BloodLinkLambdaLogMessages $functionNames $Region $logStart
        $disclosures = Get-BloodLinkDisclosureCounts $messages @($sensitiveValues)
        $disclosureTotal = 0
        foreach ($property in $disclosures.PSObject.Properties) { $disclosureTotal += [int]$property.Value }
        Add-LiveCheck "CloudWatch logs contain no credential disclosures" ($disclosureTotal -eq 0)
        $evidence.cloudWatch.logGroupCount = $functionNames.Count
        $evidence.cloudWatch.eventCount = $messages.Count
        $evidence.cloudWatch.windowStartUtc = $startedAt.AddHours(-24).ToString("o")
        $evidence.cloudWatch.disclosureCounts = $disclosures
        $evidence.api.url = $apiUrl
        $evidence.api.allowedFrontendOrigin = $expectedFrontendOrigin
        $evidence.api.clientDisclosureCount = $clientDisclosureCount
        $evidence.api.liveChecksPassed = @($checks | Where-Object { $_.status -eq "PASS" }).Count

        $evidence.result = "PASS"
        $evidence.completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        Write-BloodLinkEvidence $evidencePath $evidence @($sensitiveValues)
        Write-Host "Production verification passed. Sanitized evidence: artifacts/aws-production-verification.json"
    } catch {
        $evidence.result = "FAIL"
        $evidence.completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        $evidence.failure = Protect-BloodLinkText ([string]$_.Exception.Message) @($sensitiveValues)
        try { Write-BloodLinkEvidence $evidencePath $evidence @($sensitiveValues) } catch { }
        throw (Protect-BloodLinkText ([string]$_.Exception.Message) @($sensitiveValues))
    } finally {
        if (($adminAccessToken -or $rotatedAccessToken) -and $apiUrl) {
            $cleanupToken = $(if ($rotatedAccessToken) { $rotatedAccessToken } else { $adminAccessToken })
            try { $null = Invoke-BloodLinkApiRequest $apiUrl POST "/api/v1/auth/logout" $cleanupToken } catch { }
            $cleanupToken = $null
        }
        foreach ($path in $temporaryFiles) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
        }
        if ($bootstrapPasswordBstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bootstrapPasswordBstr) }
        if ($null -ne $bootstrapPasswordSecure) { $bootstrapPasswordSecure.Dispose() }
        if ($null -ne $rng) { $rng.Dispose() }
        if ($null -ne $smokePasswordBytes) { [Array]::Clear($smokePasswordBytes, 0, $smokePasswordBytes.Length) }
        $sensitiveValues.Clear()
        $clientBodies.Clear()
        $bootstrapPasswordPlain = $null
        $bootstrapEmail = $null
        $smokePassword = $null
        $adminAccessToken = $null
        $refreshToken = $null
        $rotatedAccessToken = $null
        $rotatedRefreshToken = $null
        Remove-Variable login, loginBody, refresh, refreshBody, registrationBody, registration, registered, identity, stackResponse, rdsResponse, sgResponse, messages -ErrorAction SilentlyContinue
    }
}

if (-not $NoRun) {
    try {
        Invoke-BloodLinkProductionVerification -Region $Region -ExpectedIamUser $ExpectedIamUser `
            -ExpectedRdsSecurityGroupName $ExpectedRdsSecurityGroupName
    } catch {
        Write-Error (Protect-BloodLinkText ([string]$_.Exception.Message))
        exit 1
    }
}
