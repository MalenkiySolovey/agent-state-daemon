$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-HasReportedVulnerability([object] $node) {
    if ($node -is [System.Collections.IDictionary]) {
        foreach ($key in $node.Keys) {
            $value = $node[$key]
            if ($key -eq 'vulnerabilities' -and $null -ne $value -and @($value).Count -gt 0) {
                return $true
            }

            if (Test-HasReportedVulnerability $value) { return $true }
        }

        return $false
    }

    if ($node -is [System.Collections.IEnumerable] -and $node -isnot [string]) {
        foreach ($item in $node) {
            if (Test-HasReportedVulnerability $item) { return $true }
        }
    }

    return $false
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $repositoryRoot
try {
    dotnet restore .\AgentStateDaemon.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }

    dotnet format .\AgentStateDaemon.sln --verify-no-changes --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Formatting verification failed.' }

    dotnet build .\AgentStateDaemon.sln --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    dotnet test .\AgentStateDaemon.sln --configuration Release --no-build --no-restore --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

    $auditOutput = dotnet list .\AgentStateDaemon.sln package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability audit failed to run.' }

    $audit = ($auditOutput -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable
    if (Test-HasReportedVulnerability $audit) {
        throw 'NuGet audit reported a vulnerable dependency.'
    }

    & (Join-Path $PSScriptRoot 'privacy-scan.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Repository privacy scan failed.' }

    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Working tree whitespace check failed.' }

    git diff --cached --check
    if ($LASTEXITCODE -ne 0) { throw 'Staged whitespace check failed.' }

    if (Test-Path .\docs) { throw 'Internal development documentation must stay outside this repository.' }

    Write-Host 'QUALITY_RESULT=PASS'
}
finally {
    Pop-Location
}
