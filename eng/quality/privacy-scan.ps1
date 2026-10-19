$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$patterns = @{
    local_user_path = '(?i)(?:[A-Z]:\\Users\\[^\\\s"''<>]+|/(?:Users|home)/[^/\s"''<>]+)'
    email_address = '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
    credential_or_key = '(?i)\b(?:sk-[A-Za-z0-9_-]{24,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[0-9A-Z]{16})\b|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
}

Push-Location $repositoryRoot
try {
    $files = git -c core.quotepath=false ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate repository files.' }

    $findings = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $files) {
        if ([string]::IsNullOrWhiteSpace($relativePath)) { continue }
        $fullPath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }

        try {
            $content = [System.IO.File]::ReadAllText($fullPath, [System.Text.Encoding]::UTF8)
        }
        catch [System.Text.DecoderFallbackException] {
            continue
        }

        foreach ($category in $patterns.Keys) {
            if ([regex]::IsMatch($content, $patterns[$category])) {
                $findings.Add("$relativePath`: $category")
            }
        }
    }

    if ($findings.Count -gt 0) {
        foreach ($finding in $findings) { Write-Error $finding -ErrorAction Continue }
        throw 'Sensitive-looking content must not be committed. Scan output intentionally excludes matching text.'
    }

    Write-Host 'PRIVACY_SCAN=PASS'
}
finally {
    Pop-Location
}
