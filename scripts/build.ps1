param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $projectRoot 'MeetingTeleprompter.sln') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& (Join-Path $projectRoot "src/MeetingTeleprompter.Host/bin/$Configuration/net10.0/MeetingTeleprompter.Host.exe") --self-test
if ($LASTEXITCODE -ne 0) { throw 'Acceptance checks failed.' }
