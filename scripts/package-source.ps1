$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
& (Join-Path $PSScriptRoot 'check-public-repo.ps1') -IncludeUntracked
$outputRoot = Join-Path $projectRoot '.artifacts'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$zipPath = Join-Path $outputRoot 'TMailSharp-source.zip'
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    $rootFiles = @('.gitignore', '.gitattributes', '.dockerignore', '.env.example', 'global.json', 'Directory.Build.props', 'Directory.Packages.props', 'README.md', 'LICENSE', 'Dockerfile', 'compose.yaml')
    foreach ($name in $rootFiles) { $null = Get-Item -LiteralPath (Join-Path $projectRoot $name) -Force }
    $listed = & git -C $projectRoot ls-files --cached --others --exclude-standard -z
    if ($LASTEXITCODE -ne 0) { throw 'Could not list public source files.' }
    $paths = ($listed -join "`n").Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -Unique
    foreach ($relative in $paths) {
        if ($relative -notin $rootFiles -and $relative -notin @('TMailSharp.slnx', 'THIRD-PARTY-NOTICES.md') -and
            $relative -notmatch '^(src|tests|scripts|docs|\.github|deploy)/') { continue }
        $file = Get-Item -LiteralPath (Join-Path $projectRoot $relative) -Force
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Write-Host "Source package: $zipPath"
