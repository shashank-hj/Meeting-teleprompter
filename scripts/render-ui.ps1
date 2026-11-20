param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $projectRoot "src/MeetingTeleprompter.App/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64/MeetingTeleprompter.App.exe"
if (-not (Test-Path -LiteralPath $appPath)) { throw 'Build the app first.' }
$outputDirectory = Join-Path $projectRoot ('artifacts/ui-live-review/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$settingsPath = Join-Path $outputDirectory 'settings.json'
@{
    Microphone = $false
    SemanticSearch = $false
    Folders = @()
    DataDirectory = Join-Path $outputDirectory 'data'
    ConserveMemory = $true
    AllowGeneralKnowledge = $false
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
$arguments = @('--render-ui', ('"' + $settingsPath + '"'), ('"' + $outputDirectory + '"'))
$preview = Start-Process -FilePath $appPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (-not $preview.WaitForExit(60000)) {
    $preview.Kill()
    throw "The render-only preview timed out. Review $outputDirectory."
}
$errorPath = Join-Path $outputDirectory 'render-error.txt'
if (Test-Path -LiteralPath $errorPath) { throw (Get-Content -LiteralPath $errorPath -Raw) }
$report = Join-Path $outputDirectory 'layout-check.json'
if (-not (Test-Path -LiteralPath $report)) { throw 'The app did not produce its layout report.' }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.passed -or $result.microphoneCaptured) { throw 'The render-only UI check failed.' }
$result | ConvertTo-Json -Depth 5
Write-Host "UI previews: $outputDirectory"
