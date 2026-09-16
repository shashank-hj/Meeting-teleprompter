param([int]$AppProcessId)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$app = Get-Process -Id $AppProcessId
$root = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
if (-not $root) { throw 'Desktop window was not created.' }
$controls = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$names = @($controls | ForEach-Object { $_.Current.Name })
foreach ($expected in @('Start session','YOUR SOURCES','LIVE TRANSCRIPT','SUGGESTED RESPONSES','Settings')) {
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
