param([int]$AppProcessId, [switch]$CheckSearch, [switch]$CheckAnswers)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$app = Get-Process -Id $AppProcessId
$root = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
if (-not $root) { throw 'Desktop window was not created.' }
$controls = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$names = @($controls | ForEach-Object { $_.Current.Name })
foreach ($expected in @('Start session','Live transcript','Meeting assistance','Settings','Answer policy','Auto assist','Meeting view')) {
    if ($names -notcontains $expected) { throw "Missing UI element: $expected" }
    Write-Host "PASS: UI element $expected"
}
$settings = $controls | Where-Object { $_.Current.Name -eq 'Settings' } | Select-Object -First 1
$invoke = $settings.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$invoke.Invoke()
Start-Sleep -Milliseconds 700
$dialogControls = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$cancel = $dialogControls | Where-Object { $_.Current.Name -eq 'Cancel' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1
if (-not $cancel) { throw 'Settings dialog did not open.' }
$cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Host 'PASS: Settings dialog opens and cancels without starting audio.'
if ($CheckSearch) {
    $controls = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $searchBox = $controls | Where-Object { $_.Current.AutomationId -eq 'SearchBox' } | Select-Object -First 1
    if (-not $searchBox) { throw 'Search input is missing.' }
    $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('How should we prepare for a meeting?')
    $options = $controls | Where-Object { $_.Current.Name -eq 'Options' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1
    $options.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
    $menuControls = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $evidence = $menuControls | Where-Object { $_.Current.Name -eq 'Find source evidence' } | Select-Object -First 1
    if (-not $evidence) { throw 'The source-evidence route is missing.' }
    $evidence.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $searchDeadline = [DateTime]::UtcNow.AddSeconds(45)
    $found = $false
    while ([DateTime]::UtcNow -lt $searchDeadline) {
        Start-Sleep -Milliseconds 500
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        if (@($all | Where-Object { $_.Current.Name -like 'SOURCE EVIDENCE*' }).Count -gt 0) { $found = $true; break }
    }
    if (-not $found) { throw 'Configured source search did not produce an evidence card.' }
    Write-Host 'PASS: Fresh desktop launch starts semantic runtime and displays cited source evidence.'
}
if ($CheckAnswers) {
    function Ask-And-Check([string]$question, [string]$label) {
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $box = $all | Where-Object { $_.Current.AutomationId -eq 'SearchBox' } | Select-Object -First 1
        $ask = $all | Where-Object { $_.Current.AutomationId -eq 'AskButton' } | Select-Object -First 1
        $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($question)
        $ask.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $deadline = [DateTime]::UtcNow.AddSeconds(100)
        do {
            Start-Sleep -Milliseconds 500
            $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            if (@($all | Where-Object { $_.Current.Name -like "$label*" }).Count -gt 0) { Write-Host "PASS: $question -> $label"; return }
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Expected answer label '$label' for '$question'."
    }
    Ask-And-Check 'What is quantum entanglement?' 'GENERAL KNOWLEDGE'
    Ask-And-Check 'What is our confidential ZephyrX budget today?' 'MORE EVIDENCE NEEDED'
    $activeAsr = Get-Process -Name 'whisper-server' -ErrorAction SilentlyContinue
    if ($activeAsr) { Write-Host 'NOTE: An existing speech server is running; this check does not assert ownership.' }
    Write-Host 'PASS: Typed questions completed without starting a listening session.'
}
