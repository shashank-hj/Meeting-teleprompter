param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $projectRoot "src/MeetingTeleprompter.App/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64/MeetingTeleprompter.App.exe"
if (-not (Test-Path -LiteralPath $appPath)) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
# This launcher is explicitly invoked by the user to open the desktop UI.
Start-Process -FilePath $appPath
