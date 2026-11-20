param([ValidateSet('Baseline','LowLatency')][string]$Profile = 'LowLatency')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $env:LOCALAPPDATA 'MeetingTeleprompter/settings.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$context = if ($Profile -eq 'LowLatency') { 512 } else { 0 }
$settings | Add-Member -NotePropertyName WhisperAudioContext -NotePropertyValue $context -Force
$settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
$settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $projectRoot 'models/installed-settings.json') -Encoding UTF8
Write-Host "Configured $Profile profile (Whisper audio context: $context)."
