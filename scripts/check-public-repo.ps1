param([switch]$IncludeUntracked)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

# Inspect only files that are tracked or eligible for publication. Never print matching secrets.
$arguments = @('-C', $projectRoot, 'ls-files', '--cached', '-z')
if ($IncludeUntracked) { $arguments += @('--others', '--exclude-standard') }
$output = & git @arguments
if ($LASTEXITCODE -ne 0) { throw 'Could not list repository files.' }
$files = (($output -join "`n").Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -Unique)
$findings = [Collections.Generic.List[string]]::new()

function Test-PrivatePath([string]$Path) {
    $path = $Path.Replace('\', '/').ToLowerInvariant()
    $name = ($path -split '/')[-1]
    $example = $name -match '\.example$|\.example\.json$'
    if ($path -match '(^|/)(\.keys|\.local|\.secrets|\.codex|\.ssh|\.aws|\.azure|\.kube|\.terraform|\.vs|\.artifacts|bin|obj|node_modules)/') { return $true }
    if ($path -match '^(keys|identity|data|mongo-data|mongodb-data|dump|dumps|backup|backups)/') { return $true }
    if (!$example -and ($name -match '^\.env($|\.)|\.env$|^appsettings\..*\.json$')) { return $true }
    if ($name -match '^(secrets|credentials)\.(json|ya?ml)$|^local\.settings\.json$|^client_secret.*\.json$|^\.(npmrc|pypirc|netrc)$|-(password|token)\.txt$|\.tfstate($|\.)') { return $true }
    return $name -match '\.(key|pem|pfx|p12|p8|jks|keystore|snk|db|sqlite|sqlite3)(-|$)|\.(bson|dump|backup|eml|msg|mbox|pst|ost|har|pcap|pcapng)$'
}

function Test-Placeholder([string]$Value) {
    return [string]::IsNullOrWhiteSpace($Value) -or $Value -match '^\$|^<|^\.\.\.$|YOUR_|APP_USER|URI_ENCODED|REPLACE_|PLACEHOLDER|EXAMPLE|(^|[-_])(test|mock|fake|sample)([-_]|$)'
}

$patterns = @{
    'private key' = '-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----|-----BEGIN PGP PRIVATE [K]EY BLOCK-----'
    'data-protection key' = '<master[K]ey(?:\s|>)'
    'NuGet credentials' = '<packageSource[C]redentials(?:\s|>)'
    'GitHub token' = '\bgh[pousr]_[A-Za-z0-9]{30,}\b|\bgithub_pat_[A-Za-z0-9_]{40,}\b'
    'AWS access key' = '\b(?:AKIA|ASIA)[0-9A-Z]{16}\b'
    'Slack token' = '\bxox[baprs]-[A-Za-z0-9-]{20,}\b'
    'OpenAI key' = '\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{40,}\b'
}
foreach ($relative in $files) {
    if (Test-PrivatePath $relative) { $findings.Add("$relative (private file path)"); continue }
    $path = Join-Path $projectRoot $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { $findings.Add("$relative (symbolic link requires review)"); continue }
    $text = [IO.File]::ReadAllText($path)
    if ($text.IndexOf([char]0) -ge 0) { continue }
    foreach ($kind in $patterns.Keys) {
        if ([regex]::IsMatch($text, $patterns[$kind])) { $findings.Add("$relative ($kind)") }
    }
    foreach ($match in [regex]::Matches($text, '(?i)(?:mongodb(?:\+srv)?|postgres(?:ql)?|mysql)://(?<value>[^\s/@]+:[^\s/@]+)@')) {
        if (!(Test-Placeholder $match.Groups['value'].Value)) { $findings.Add("$relative (embedded database credentials)") }
    }
    if ($relative -match '(?i)\.json$|(?:^|/)\.?env(?:\.|$)|\.env(?:\.|$)') {
        foreach ($match in [regex]::Matches($text, '(?im)(?:"(?:password|secret|clientSecret|apiKey|accessToken)"\s*:\s*"(?<value>[^"\r\n]+)"|^[ \t]*[A-Z0-9_]*(?:PASSWORD|SECRET|TOKEN|API_KEY)[ \t]*=[ \t]*(?<value>[^\r\n]+))')) {
            if (!(Test-Placeholder $match.Groups['value'].Value)) { $findings.Add("$relative (non-placeholder credential setting)") }
        }
    }
}
if ($findings.Count -gt 0) {
    $findings | Sort-Object -Unique | ForEach-Object { Write-Output "REVIEW: $_" }
    throw 'Public repository check failed. Remove private files or replace credentials with safe placeholders.'
}
Write-Output "Public repository check passed ($($files.Count) files)."
