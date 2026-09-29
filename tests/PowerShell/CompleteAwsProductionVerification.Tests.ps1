$scriptPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "scripts\complete-aws-production-verification.ps1"
. $scriptPath -NoRun

Describe "complete-aws-production-verification parsing" {
    It "parses the JSON string returned by the Lambda runtime" {
        $inner = '{"success":true,"pendingBefore":0,"pendingAfter":0,"canonicalRoles":[{"name":"SystemAdmin","count":1},{"name":"FacilityAdmin","count":1},{"name":"FacilityStaff","count":1}],"systemAdminCount":1,"intendedSystemAdminCount":0}'
        $outer = ConvertTo-Json $inner -Compress

        $result = ConvertFrom-BloodLinkMigratorPayload $outer

        $result.success | Should Be $true
        $result.pendingAfter | Should Be 0
        @($result.canonicalRoles).Count | Should Be 3
    }

    It "rejects a migrator payload with missing fields" {
        { ConvertFrom-BloodLinkMigratorPayload '{"success":true}' } | Should Throw
    }
}

Describe "complete-aws-production-verification failure handling" {
    It "rejects a Lambda FunctionError even when HTTP status is 200" {
        $metadata = [pscustomobject]@{ StatusCode = 200; FunctionError = "Unhandled" }
        $payload = [pscustomobject]@{ success = $true }

        { Assert-BloodLinkLambdaInvocation $metadata $payload } | Should Throw
    }

    It "rejects SQL ingress from a world CIDR" {
        $permissions = @([pscustomobject]@{
            IpProtocol = "tcp"; FromPort = 1433; ToPort = 1433
            IpRanges = @([pscustomobject]@{ CidrIp = "0.0.0.0/0" })
            Ipv6Ranges = @(); PrefixListIds = @()
            UserIdGroupPairs = @([pscustomobject]@{ GroupId = "sg-lambda" })
        })

        $result = Get-BloodLinkSqlIngressAssessment $permissions "sg-lambda"

        $result.HasWorldIpv4 | Should Be $true
        $result.IsRestrictedToLambda | Should Be $false
    }

    It "accepts SQL ingress only from the dedicated Lambda security group" {
        $permissions = @([pscustomobject]@{
            IpProtocol = "tcp"; FromPort = 1433; ToPort = 1433
            IpRanges = @(); Ipv6Ranges = @(); PrefixListIds = @()
            UserIdGroupPairs = @([pscustomobject]@{ GroupId = "sg-lambda" })
        })

        $result = Get-BloodLinkSqlIngressAssessment $permissions "sg-lambda"

        $result.IsRestrictedToLambda | Should Be $true
    }
}

Describe "complete-aws-production-verification redaction" {
    It "redacts runtime secrets, bearer credentials, and token-shaped fields" {
        $secret = "HiddenValue-123!"
        $text = "password=$secret Authorization: Bearer abc.def.ghi accessToken=abcdefghijklmnopqrstuv"

        $safe = Protect-BloodLinkText $text @($secret)

        $safe | Should Not Match ([regex]::Escape($secret))
        $safe | Should Not Match "Bearer abc"
        $safe | Should Not Match "abcdefghijklmnopqrstuv"
    }

    It "rejects secret-shaped evidence" {
        { Assert-BloodLinkEvidenceRedacted '{"authorization":"Bearer hidden-token-value"}' } | Should Throw
        { Assert-BloodLinkEvidenceRedacted '{"status":"PASS"}' } | Should Not Throw
    }
}

Describe "complete-aws-production-verification CORS target" {
    It "uses the exact production frontend origin and tests denied variants" {
        $contents = Get-Content -LiteralPath $scriptPath -Raw

        $contents | Should Match 'https://d2z1pcfp95dfwd\.cloudfront\.net'
        $contents | Should Match 'authorization,content-type'
        $contents | Should Match 'https://d2z1pcfp95dfwd\.cloudfront\.net\.evil\.example'
        $contents | Should Not Match 'placeholder\.invalid'
        $contents | Should Not Match 'AllowAnyOrigin'
    }
}
