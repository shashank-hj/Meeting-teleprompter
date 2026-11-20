param(
    [string]$SettingsPath,
    [string]$SamplesPath,
    [string[]]$AdditionalSettingsPath = @(),
    [switch]$IncludeSmallSpeechModel
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SettingsPath) { $SettingsPath = Join-Path $projectRoot 'models/installed-settings.json' }
if (-not $SamplesPath) { $SamplesPath = Join-Path $projectRoot 'artifacts/meeting-validation/samples.json' }
if (-not (Test-Path -LiteralPath $SamplesPath)) { throw 'Supply a local meeting corpus manifest, or run scripts/fetch-meeting-samples.ps1 first.' }
$hostExe = Join-Path $projectRoot 'src/MeetingTeleprompter.Host/bin/Release/net10.0/MeetingTeleprompter.Host.exe'
if (-not (Test-Path -LiteralPath $hostExe)) { throw 'Run scripts/build.ps1 first.' }
$outputDirectory = Join-Path $projectRoot ('artifacts/profile-evaluation/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$base = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
$profiles = [Collections.Generic.List[object]]::new()
function Clone-Settings($settings) {
    $copy = @{}
    foreach ($property in $settings.psobject.Properties) { $copy[$property.Name] = $property.Value }
    return $copy
}
foreach ($context in @(512,0)) {
    $copy = Clone-Settings $base
    $copy.WhisperAudioContext = $context
    $copy.WhisperBeamSize = 1
    $profiles.Add(@{ Name = $(if ($context -eq 512) { 'base-low-latency' } else { 'base-full-context' }); Settings = $copy })
}
if ($IncludeSmallSpeechModel) {
    $small = Join-Path $projectRoot 'models/ggml-small.en-q5_1.bin'
    if (-not (Test-Path -LiteralPath $small)) { throw 'The optional small.en model is not installed.' }
    $copy = Clone-Settings $base
    $copy.WhisperModel = $small
    $copy.WhisperAudioContext = 512
    $profiles.Add(@{ Name = 'small-low-latency'; Settings = $copy })
}
$extra = 0
foreach ($path in $AdditionalSettingsPath) {
    $extra++
    $copy = Clone-Settings (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
    $profiles.Add(@{ Name = "candidate-$extra"; Settings = $copy })
}
$summary = [Collections.Generic.List[object]]::new()
foreach ($profile in $profiles) {
    # Separate indexes keep evaluation out of the user's saved source database.
    $profile.Settings.DataDirectory = Join-Path $outputDirectory $profile.Name
    $profile.Settings.Microphone = $false
    $config = Join-Path $outputDirectory ($profile.Name + '-settings.json')
    $report = Join-Path $outputDirectory ($profile.Name + '-results.json')
    $profile.Settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $config -Encoding UTF8
    Write-Host "Evaluating $($profile.Name)..."
    & $hostExe --validate-models $config $SamplesPath $report
    if ($LASTEXITCODE -ne 0) { throw "Profile $($profile.Name) failed. Earlier reports are retained in $outputDirectory." }
    $data = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $summary.Add([pscustomobject]@{
        Profile = $profile.Name
        WordErrorRate = $data.wordErrorRate
        MedianAsrMs = $data.medianAsrMs
        MaxAsrMs = $data.maxAsrMs
        RealTimeFactor = $data.aggregateRealTimeFactor
        NativePeakGiB = $data.resources.PeakNativeWorkingSetBytes / 1GB
        MinimumFreeGiB = $data.resources.MinimumAvailableMemoryBytes / 1GB
        AverageCpuPercent = $data.resources.AverageNativeCpuPercent
        FirstGenerationMs = $data.generations[0].completionMs
        CitationFixturesPassed = @($data.generations | Where-Object { -not $_.cited -or -not $_.correctDate -or -not $_.correctOwner }).Count -eq 0
        Report = $report
    })
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputDirectory 'comparison.json') -Encoding UTF8
$summary | Format-Table Profile,WordErrorRate,MedianAsrMs,RealTimeFactor,NativePeakGiB,AverageCpuPercent,CitationFixturesPassed -AutoSize
Write-Host "Comparison reports: $outputDirectory"
Write-Host 'This does not change app settings. Candidate weights and alternative runtimes must be installed separately; no microphone is opened.'
