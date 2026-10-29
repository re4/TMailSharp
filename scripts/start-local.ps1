param([int]$WebPort = 5080, [int]$MongoPort = 27018, [int]$SmtpPort = 2525, [switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localRoot = Join-Path $projectRoot '.local'
$webRoot = Join-Path $projectRoot 'src\TMailSharp.Web'
New-Item -ItemType Directory -Path $localRoot -Force | Out-Null

function Get-OwnedProcess([string]$Name) {
    $pidFile = Join-Path $localRoot "$Name.pid"
    if (!(Test-Path -LiteralPath $pidFile)) { return $null }
    $savedId = Get-Content -LiteralPath $pidFile -Raw
    $process = Get-Process -Id ([int]$savedId.Trim()) -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    $expected = if ($Name -eq 'mongo') { 'mongod' } else { 'dotnet' }
    if ($process.ProcessName -ne $expected) { return $null }
    $stampFile = Join-Path $localRoot "$Name.process.json"
    if (!(Test-Path -LiteralPath $stampFile)) { return $null }
    $stamp = Get-Content -LiteralPath $stampFile -Raw | ConvertFrom-Json
    if ([long]$stamp.StartTicks -ne $process.StartTime.ToUniversalTime().Ticks) { return $null }
    return $process
}

function Wait-Port([int]$Port) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $tcp = [Net.Sockets.TcpClient]::new()
        try { $tcp.Connect('127.0.0.1', $Port); return }
        catch { Start-Sleep -Milliseconds 500 }
        finally { $tcp.Dispose() }
    }
    throw "Port $Port did not start. See logs in $localRoot."
}

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK first: https://dotnet.microsoft.com/download/dotnet/10.0' }
$existingWeb = Get-OwnedProcess 'web'
if ($null -ne $existingWeb) {
    Write-Host "TMailSharp is already running. Use scripts/stop-local.ps1 before changing ports."
    exit 0
}

$mongo = Get-OwnedProcess 'mongo'
if ($null -eq $mongo) {
    $mongoExe = Join-Path $localRoot 'mongodb-win32-x86_64-windows-8.0.34\bin\mongod.exe'
    if (!(Test-Path -LiteralPath $mongoExe)) {
        Write-Host 'Downloading MongoDB Community for local development...'
        $archive = Join-Path $localRoot 'mongodb.zip'
        Invoke-WebRequest -UseBasicParsing -Uri 'https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.34.zip' -OutFile $archive
        $expectedHash = '6B021A0864494E4DECF278B36477A29EB36072432773E4E62C2F42C302630DB0'
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'MongoDB checksum did not match. Download stopped.' }
        Expand-Archive -LiteralPath $archive -DestinationPath $localRoot -Force
    }
    $dataPath = Join-Path $localRoot 'data'
    New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
    $arguments = @('--dbpath', ('"' + $dataPath + '"'), '--bind_ip', '127.0.0.1', '--port', "$MongoPort", '--logpath', ('"' + (Join-Path $localRoot 'mongo.log') + '"'))
    $mongo = Start-Process -FilePath $mongoExe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $mongo.Id | Set-Content -LiteralPath (Join-Path $localRoot 'mongo.pid')
    @{ StartTicks = "$($mongo.StartTime.ToUniversalTime().Ticks)" } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $localRoot 'mongo.process.json')
}
Wait-Port $MongoPort

$passwordFile = Join-Path $localRoot 'admin-password.txt'
if (!(Test-Path -LiteralPath $passwordFile)) {
    $bytes = [byte[]]::new(24)
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $random.GetBytes($bytes)
    $random.Dispose()
    ([BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()) | Set-Content -LiteralPath $passwordFile
}

Push-Location $projectRoot
try {
    & dotnet restore TMailSharp.slnx --packages (Join-Path $localRoot 'packages') --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    & dotnet build src/TMailSharp.Web/TMailSharp.Web.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'The application could not be built.' }
} finally { Pop-Location }

$settings = @{
    ASPNETCORE_ENVIRONMENT = 'Development'
    ASPNETCORE_URLS = "http://localhost:$WebPort"
    Mongo__ConnectionString = "mongodb://127.0.0.1:$MongoPort"
    Mongo__Database = 'tmailsharp'
    Admin__Password = (Get-Content -LiteralPath $passwordFile -Raw).Trim()
    Smtp__Host = '127.0.0.1'
    Smtp__Port = "$SmtpPort"
    Demo__Enabled = 'true'
    DataProtection__KeyDirectory = (Join-Path $localRoot 'keys')
}
$previous = @{}
foreach ($entry in $settings.GetEnumerator()) {
    $previous[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
}
try {
    $dllPath = Join-Path $webRoot 'bin\Release\net10.0\TMailSharp.Web.dll'
    $web = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList ('"' + $dllPath + '"') -WorkingDirectory $webRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $localRoot 'web.log') -RedirectStandardError (Join-Path $localRoot 'web-error.log')
    $web.Id | Set-Content -LiteralPath (Join-Path $localRoot 'web.pid')
    @{ StartTicks = "$($web.StartTime.ToUniversalTime().Ticks)" } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $localRoot 'web.process.json')
} finally {
    foreach ($entry in $previous.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
}
Wait-Port $WebPort
$health = Invoke-RestMethod -Uri "http://localhost:$WebPort/health"
if ($health.status -ne 'healthy') { throw 'The application started but is not healthy.' }
Write-Host "TMailSharp is ready: http://localhost:$WebPort"
Write-Host "Administration: http://localhost:$WebPort/admin"
Write-Host "Your local administrator password is in $passwordFile"
Write-Host "SMTP: 127.0.0.1:$SmtpPort | MongoDB: 127.0.0.1:$MongoPort (local access only)"
Write-Host 'Stop with: powershell -ExecutionPolicy Bypass -File scripts/stop-local.ps1'
if (!$NoBrowser) { Start-Process "http://localhost:$WebPort" }
