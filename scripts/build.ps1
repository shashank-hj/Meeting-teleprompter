param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $projectRoot "src/MeetingTeleprompter.App/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64/MeetingTeleprompter.App.exe"
$runningApp = @(Get-Process -Name 'MeetingTeleprompter.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appPath })
if ($runningApp.Count -gt 0) {
    $processIds = ($runningApp | ForEach-Object { $_.Id }) -join ', '
    throw "The $Configuration app is running (PID: $processIds) and locks the build output. Export any transcript, close the app, then rerun scripts/build.ps1."
}

dotnet build (Join-Path $projectRoot 'MeetingTeleprompter.sln') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& (Join-Path $projectRoot "src/MeetingTeleprompter.Host/bin/$Configuration/net10.0/MeetingTeleprompter.Host.exe") --self-test
if ($LASTEXITCODE -ne 0) { throw 'Acceptance checks failed.' }
