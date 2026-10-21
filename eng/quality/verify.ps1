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
$baselineCommit = '2353c4b44aec5e1fba854739a4f4139359e05cf0'
$baselineTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("agent-state-daemon-baseline-" + [guid]::NewGuid().ToString('N'))
$baselineSource = Join-Path $baselineTempRoot 'source'
$baselinePublish = Join-Path $baselineTempRoot 'publish'
$previousBaselineHost = $env:AGENT_STATE_DAEMON_BASELINE_HOST
$baselineWorktreeAdded = $false
Push-Location $repositoryRoot
try {
    dotnet restore .\AgentStateDaemon.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }

    dotnet format .\AgentStateDaemon.sln --verify-no-changes --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Formatting verification failed.' }

    dotnet build .\AgentStateDaemon.sln --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    New-Item -ItemType Directory -Path $baselineTempRoot | Out-Null
    git worktree add --detach $baselineSource $baselineCommit
    if ($LASTEXITCODE -ne 0) { throw 'Unable to create the exact previous-daemon compatibility reference.' }
    $baselineWorktreeAdded = $true

    dotnet restore (Join-Path $baselineSource 'AgentStateDaemon.sln') --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Previous-daemon locked restore failed.' }

    dotnet publish (Join-Path $baselineSource 'src\AgentStateDaemon.Host\AgentStateDaemon.Host.csproj') --configuration Release --no-restore --output $baselinePublish
    if ($LASTEXITCODE -ne 0) { throw 'Previous-daemon compatibility reference failed to publish.' }

    $baselineHost = Join-Path $baselinePublish 'AgentStateDaemon.Host.exe'
    if (-not (Test-Path -LiteralPath $baselineHost -PathType Leaf)) { throw 'Previous-daemon compatibility host is missing.' }
    $env:AGENT_STATE_DAEMON_BASELINE_HOST = $baselineHost

    dotnet test .\AgentStateDaemon.sln --configuration Release --no-build --no-restore --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

    if ($null -eq $previousBaselineHost) {
        Remove-Item Env:\AGENT_STATE_DAEMON_BASELINE_HOST -ErrorAction SilentlyContinue
    }
    else {
        $env:AGENT_STATE_DAEMON_BASELINE_HOST = $previousBaselineHost
    }

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
    if ($null -eq $previousBaselineHost) {
        Remove-Item Env:\AGENT_STATE_DAEMON_BASELINE_HOST -ErrorAction SilentlyContinue
    }
    else {
        $env:AGENT_STATE_DAEMON_BASELINE_HOST = $previousBaselineHost
    }

    if ($baselineWorktreeAdded) {
        git worktree remove --force $baselineSource
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Failed to remove the exact baseline worktree automatically.' }
        git worktree prune
    }

    $resolvedTempRoot = [System.IO.Path]::GetFullPath($baselineTempRoot)
    $resolvedSystemTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ((Test-Path -LiteralPath $resolvedTempRoot) -and
        $resolvedTempRoot.StartsWith($resolvedSystemTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        $resolvedTempRoot.Length -gt $resolvedSystemTempRoot.Length) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }

    Pop-Location
}
