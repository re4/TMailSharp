$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localRoot = Join-Path $projectRoot '.local'
foreach ($name in @('web', 'mongo')) {
    $pidFile = Join-Path $localRoot "$name.pid"
    if (!(Test-Path -LiteralPath $pidFile)) { continue }
    $savedId = [int](Get-Content -LiteralPath $pidFile -Raw).Trim()
    $process = Get-Process -Id $savedId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        $expected = if ($name -eq 'mongo') { 'mongod' } else { 'dotnet' }
        $stampFile = Join-Path $localRoot "$name.process.json"
        $matchesStamp = $false
        if (Test-Path -LiteralPath $stampFile) {
            $stamp = Get-Content -LiteralPath $stampFile -Raw | ConvertFrom-Json
            $matchesStamp = [long]$stamp.StartTicks -eq $process.StartTime.ToUniversalTime().Ticks
        }
        if ($process.ProcessName -eq $expected -and $matchesStamp) {
            $process.Kill()
            $process.WaitForExit(10000) | Out-Null
            Write-Host "Stopped TMailSharp $name."
        } else { Write-Warning "Process $savedId does not belong to this project; it was left running." }
    }
    Remove-Item -LiteralPath $pidFile
    $stampFile = Join-Path $localRoot "$name.process.json"
    if (Test-Path -LiteralPath $stampFile) { Remove-Item -LiteralPath $stampFile }
}
Write-Host 'Your inbox data is preserved in .local/data.'
