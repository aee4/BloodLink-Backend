param(
    [string]$Region = "eu-north-1",
    [string]$RdsEndpoint,
    [string]$BootstrapEmail,
    [string]$BootstrapFirstName = "System",
    [string]$BootstrapLastName = "Admin"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RdsEndpoint)) {
    $db = aws rds describe-db-instances `
        --region $Region `
        --db-instance-identifier bloodlink-db `
        --query "DBInstances[0].Endpoint.Address" `
        --output text
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($db) -or $db -eq "None") {
        throw "Could not discover the bloodlink-db endpoint."
    }
    $RdsEndpoint = $db.Trim()
}

if ([string]::IsNullOrWhiteSpace($BootstrapEmail)) {
    $BootstrapEmail = Read-Host "Bootstrap SystemAdmin email"
}

$rdsPassword = Read-Host "RDS master password for bloodlinkadmin" -AsSecureString
$bootstrapPassword = Read-Host "Bootstrap SystemAdmin password" -AsSecureString

$bstrRds = [IntPtr]::Zero
$bstrBootstrap = [IntPtr]::Zero
$payloadPath = $null
try {
    $bstrRds = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($rdsPassword)
    $bstrBootstrap = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($bootstrapPassword)
    $payloadPath = [IO.Path]::GetTempFileName()
    $plainRdsPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstrRds)
    $plainBootstrapPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstrBootstrap)

    $payload = @{
        region = $Region
        rdsEndpoint = $RdsEndpoint
        rdsPassword = $plainRdsPassword
        bootstrapEmail = $BootstrapEmail
        bootstrapPassword = $plainBootstrapPassword
        bootstrapFirstName = $BootstrapFirstName
        bootstrapLastName = $BootstrapLastName
    } | ConvertTo-Json -Depth 4 -Compress

    [IO.File]::WriteAllText($payloadPath, $payload, [Text.UTF8Encoding]::new($false))

    $repoRoot = Split-Path -Parent $PSScriptRoot
    $helperProject = Join-Path $repoRoot "src\BloodLink.SecretConfigurator\BloodLink.SecretConfigurator.csproj"
    $processInfo = [Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = "dotnet"
    $processInfo.Arguments = "run --project `"$helperProject`" --configuration Release --no-restore"
    $processInfo.RedirectStandardInput = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true

    $process = [Diagnostics.Process]::Start($processInfo)
    try {
        $process.StandardInput.Write([IO.File]::ReadAllText($payloadPath))
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    } finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }

    if ($exitCode -ne 0) {
        throw "Secret configuration failed before storing all required secrets."
    }

    if (![string]::IsNullOrWhiteSpace($stdout)) {
        Write-Host $stdout.Trim()
    }
} finally {
    if ($bstrRds -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstrRds) }
    if ($bstrBootstrap -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstrBootstrap) }
    if ($null -ne $payloadPath -and (Test-Path -LiteralPath $payloadPath)) {
        Remove-Item -LiteralPath $payloadPath -Force
    }
    Remove-Variable plainRdsPassword, plainBootstrapPassword, payload, stdout, stderr -ErrorAction SilentlyContinue
}
