param([switch]$AudioOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$settings = Join-Path $env:LOCALAPPDATA 'MeetingTeleprompter/settings.json'
$sampleManifest = Join-Path $projectRoot 'artifacts/meeting-validation/samples.json'
if (-not (Test-Path -LiteralPath $sampleManifest)) { & (Join-Path $PSScriptRoot 'fetch-meeting-samples.ps1') }
$samples = Get-Content -LiteralPath $sampleManifest -Raw | ConvertFrom-Json
if (-not $AudioOnly) {
    $hostExecutable = Join-Path $projectRoot 'src/MeetingTeleprompter.Host/bin/Release/net10.0/MeetingTeleprompter.Host.exe'
    & $hostExecutable --validate-models $settings $sampleManifest (Join-Path $projectRoot 'artifacts/meeting-validation/low-latency-results.json')
    if ($LASTEXITCODE -ne 0) { throw 'Model validation failed.' }
}
$appExecutable = Join-Path $projectRoot 'src/MeetingTeleprompter.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/MeetingTeleprompter.App.exe'
$audioReport = Join-Path $projectRoot 'artifacts/meeting-validation/audio-results.json'
$arguments = @('--validate-audio', ('"' + $settings + '"'), ('"' + $samples[0].Path + '"'), ('"' + $audioReport + '"'))
Write-Host 'Playing one public meeting excerpt through the default output for a loopback test. Microphone capture is disabled.'
$audioProcess = Start-Process -FilePath $appExecutable -ArgumentList $arguments -WindowStyle Hidden -PassThru
$audioProcess.WaitForExit()
if (-not (Test-Path -LiteralPath $audioReport)) { throw 'Audio validation did not produce a report.' }
$report = Get-Content -LiteralPath $audioReport -Raw | ConvertFrom-Json
$report | ConvertTo-Json -Depth 5
if (-not $report.passed) { throw 'Audio capture validation failed.' }
