param([string]$CertificatePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $projectRoot ('dist/package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
dotnet publish (Join-Path $projectRoot 'src/MeetingTeleprompter.App/MeetingTeleprompter.App.csproj') -c Release -r win-x64 --self-contained true -o $staging
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$workerDirectory = Join-Path $staging 'workers'
dotnet publish (Join-Path $projectRoot 'src/MeetingTeleprompter.Host/MeetingTeleprompter.Host.csproj') -c Release -r win-x64 --self-contained true -o $workerDirectory
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging/AppxManifest.xml') -Destination (Join-Path $staging 'AppxManifest.xml')
$assets = Join-Path $staging 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Add-Type -AssemblyName System.Drawing
foreach ($asset in @(@('StoreLogo.png',50),@('Square44x44Logo.png',44),@('Square150x150Logo.png',150))) {
    $size = [int]$asset[1]
    $bitmap = New-Object System.Drawing.Bitmap $size,$size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::FromArgb(13,17,23))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(147,197,181)),([single]($size / 15))
    foreach ($fraction in @(0.3,0.5,0.7)) { $graphics.DrawLine($pen, [single]($size * 0.2), [single]($size * $fraction), [single]($size * 0.8), [single]($size * $fraction)) }
    $bitmap.Save((Join-Path $assets $asset[0]), [System.Drawing.Imaging.ImageFormat]::Png)
    $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$makeAppx = Get-ChildItem -LiteralPath $sdkRoot -Filter makeappx.exe -Recurse | Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeAppx) { throw 'Install the Windows SDK to create MSIX packages.' }
$package = Join-Path $projectRoot 'dist/MeetingTeleprompter.msix'
$packOutput = & $makeAppx.FullName pack /d $staging /p $package /o
if ($LASTEXITCODE -ne 0) { $packOutput | Write-Output; throw 'MSIX packaging failed.' }
if ($CertificatePath) {
    $signTool = Join-Path $makeAppx.DirectoryName 'signtool.exe'
    & $signTool sign /fd SHA256 /f $CertificatePath $package
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed. Certificate subject must match the manifest publisher.' }
}
Write-Host "Package: $package"
Write-Host 'Unsigned packages must be signed with a trusted matching certificate before installation.'
$resolvedStage = (Resolve-Path -LiteralPath $staging).Path
$resolvedDist = (Resolve-Path -LiteralPath (Join-Path $projectRoot 'dist')).Path
if (-not $resolvedStage.StartsWith($resolvedDist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected staging path; cleanup skipped.' }
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
