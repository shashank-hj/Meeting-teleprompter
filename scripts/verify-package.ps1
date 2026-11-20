param([string]$PackagePath, [switch]$RunChecks, [switch]$WithBrowser)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $PackagePath) { $PackagePath = Join-Path $projectRoot 'dist/MeetingTeleprompter.msix' }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
$reportDirectory = Join-Path $projectRoot 'artifacts/msix-validation'
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$workerDirectory = $null
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    foreach ($expected in @('AppxManifest.xml','MeetingTeleprompter.App.exe','workers/MeetingTeleprompter.Host.exe',
        'workers/MeetingTeleprompter.Core.dll','.playwright/node/win32_x64/node.exe','workers/.playwright/node/win32_x64/node.exe')) {
        if ($names -notcontains $expected) { throw "Missing package entry: $expected" }
    }
    if (@($names | Where-Object { $_ -match '\.playwright/node/(linux|darwin)' }).Count -gt 0) { throw 'Other-platform Node binaries were included.' }
    if (@($names | Where-Object { $_ -match '\.(gguf|onnx)$' }).Count -gt 0) { throw 'Model weights must be installed separately.' }
    if ($RunChecks) {
        $workerDirectory = [IO.Path]::GetFullPath((Join-Path $reportDirectory ([Guid]::NewGuid().ToString('N'))))
        New-Item -ItemType Directory -Path $workerDirectory | Out-Null
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\','/')
            if (-not $name.StartsWith('workers/') -or $entry.Name.Length -eq 0) { continue }
            $target = [IO.Path]::GetFullPath((Join-Path $workerDirectory $name.Substring(8)))
            if (-not $target.StartsWith($workerDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package extraction path.' }
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$false)
        }
        $checkArguments = @('--self-test')
        if ($WithBrowser) { $checkArguments += '--with-browser' }
        & (Join-Path $workerDirectory 'MeetingTeleprompter.Host.exe') @checkArguments
        if ($LASTEXITCODE -ne 0) { throw 'The packaged worker acceptance checks failed.' }
    }
    $report = [ordered]@{
        passed = $true
        timestamp = [DateTimeOffset]::UtcNow.ToString('O')
        package = $package
        packageBytes = (Get-Item -LiteralPath $package).Length
        packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        entryCount = $names.Count
        modelsExcluded = $true
        onlyWindowsNode = $true
        packagedChecksRun = [bool]$RunChecks
        browserCheckRun = [bool]($RunChecks -and $WithBrowser)
        extractedWorkers = $workerDirectory
        signing = 'Not checked; use a trusted matching publisher certificate for installation.'
    }
    $report | ConvertTo-Json -Depth 4 | Tee-Object -FilePath (Join-Path $reportDirectory 'latest-package-check.json')
} finally { $archive.Dispose() }
