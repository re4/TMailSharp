$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$previous = [Environment]::GetEnvironmentVariable('TMAIL_TEST_MONGO', 'Process')
if (!$previous) { $env:TMAIL_TEST_MONGO = 'mongodb://127.0.0.1:27018' }
Push-Location $projectRoot
try {
    & dotnet test TMailSharp.slnx --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Some tests failed. Make sure the local MongoDB server is running.' }
} finally {
    Pop-Location
    [Environment]::SetEnvironmentVariable('TMAIL_TEST_MONGO', $previous, 'Process')
}
