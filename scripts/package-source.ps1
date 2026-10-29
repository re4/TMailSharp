$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$outputRoot = Join-Path $projectRoot '.artifacts'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$zipPath = Join-Path $outputRoot 'TMailSharp-source.zip'
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    $rootFiles = @('.gitignore', '.gitattributes', '.dockerignore', '.env.example', 'global.json', 'Directory.Build.props', 'Directory.Packages.props', 'README.md', 'LICENSE', 'Dockerfile', 'compose.yaml')
    $files = foreach ($name in $rootFiles) { Get-Item -LiteralPath (Join-Path $projectRoot $name) -Force }
    $files += foreach ($name in @('TMailSharp.slnx', 'THIRD-PARTY-NOTICES.md')) {
        if (Test-Path -LiteralPath (Join-Path $projectRoot $name)) { Get-Item -LiteralPath (Join-Path $projectRoot $name) -Force }
    }
    $files += foreach ($folder in @('src', 'tests', 'scripts', 'docs', '.github', 'deploy')) {
        $folderPath = Join-Path $projectRoot $folder
        if (Test-Path -LiteralPath $folderPath) { Get-ChildItem -LiteralPath $folderPath -File -Recurse -Force }
    }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj|\.git|\.local|\.keys|\.artifacts|node_modules)/' -or
            $relative -match '(^|/)(\.env|appsettings\.Local\.json)$' -or
            $relative -match '^deploy/.*\.env$' -or
            $relative -match '\.(pfx|user|suo)$') { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Write-Host "Source package: $zipPath"
