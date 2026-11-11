# Starts a published Tasky against throw-away sample data, lets it open every page, and fails
# when it crashes, logs an error, or never finishes. Used by CI; you can run it locally too:
#
#   ./scripts/smoke-test.ps1 -Exe Tasky/bin/Release/net10.0/win-x64/publish/Tasky.exe
#
# Your real data is never touched: TASKY_DATA_ROOT points Tasky at a temporary folder.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [int] $TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'

$root = Join-Path ([IO.Path]::GetTempPath()) "tasky-smoke-$([guid]::NewGuid().ToString('N'))"
$env:TASKY_DATA_ROOT  = $root
$env:TASKY_SMOKE_FULL = '1'
$env:TASKY_SYNC_TEST_FOLDER = Join-Path $root 'cloud'   # sync is turned on, run and off again against this folder
$marker = Join-Path $root 'Local\Tasky\smoke-test-passed'
$logs   = Join-Path $root 'Local\Tasky\logs'

# Lines that mean something went wrong (recovered errors count too: they are bugs).
$errorPattern = 'Fatal error|could not start|Unhandled|Unobserved'

$process = Start-Process -FilePath $Exe -PassThru
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Path $marker) -and -not $process.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }

    $logText = if (Test-Path $logs) { Get-Content (Join-Path $logs '*.log') -Raw } else { '' }
    if ($logText) { Write-Host "── Tasky log ──`n$logText" }

    if ($process.HasExited) { throw "Tasky exited during the smoke test (exit code $($process.ExitCode))." }
    if (-not (Test-Path $marker)) { throw "Tasky did not open every page within $TimeoutSeconds seconds." }
    if ($logText -match $errorPattern) { throw 'Tasky logged an error during the smoke test.' }
    Write-Host 'Smoke test passed: Tasky opened every page with sample data.'
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
