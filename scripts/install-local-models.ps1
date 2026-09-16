$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$modelDirectory = Join-Path $projectRoot 'models'
$runtimeDirectory = Join-Path $projectRoot 'runtimes'
$downloadDirectory = Join-Path $runtimeDirectory 'downloads'
New-Item -ItemType Directory -Force -Path $modelDirectory,$runtimeDirectory,$downloadDirectory | Out-Null
function Download-Checked([string]$Url, [string]$Destination, [string]$Sha256) {
    if (Test-Path -LiteralPath $Destination) {
        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256) { return }
        throw "Existing asset checksum differs: $Destination"
    }
    & curl.exe --fail --location --retry 3 --silent --show-error --output "$Destination.partial" $Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url" }
    if ((Get-FileHash -LiteralPath "$Destination.partial" -Algorithm SHA256).Hash -ne $Sha256) { throw "Checksum mismatch: $Url" }
    Move-Item -LiteralPath "$Destination.partial" -Destination $Destination
}
Download-Checked 'https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-bin-x64.zip' (Join-Path $downloadDirectory 'whisper-b5130.zip') 'f9ec6c52a2e949b62ab51fa21d0d497958f9e41c3010c157c4e42932d5316f3c'
Download-Checked 'https://github.com/ggml-org/llama.cpp/releases/download/b10909/llama-b10909-bin-win-cpu-x64.zip' (Join-Path $downloadDirectory 'llama-b10909.zip') '4d4e3341d94f729d343a8e67e4ab692032645122c99368b88e02868212e639a8'
Download-Checked 'https://github.com/asg017/sqlite-vec/releases/download/v0.1.9/sqlite-vec-0.1.9-loadable-windows-x86_64.tar.gz' (Join-Path $downloadDirectory 'sqlite-vec.tar.gz') '51581189d52066b4dfc6631f6d7a3eab7dedc2260656ab09ca97ab3fb8165983'
foreach ($name in @('whisper','llama','sqlite-vec')) { New-Item -ItemType Directory -Force -Path (Join-Path $runtimeDirectory $name) | Out-Null }
Expand-Archive -LiteralPath (Join-Path $downloadDirectory 'whisper-b5130.zip') -DestinationPath (Join-Path $runtimeDirectory 'whisper') -Force
Expand-Archive -LiteralPath (Join-Path $downloadDirectory 'llama-b10909.zip') -DestinationPath (Join-Path $runtimeDirectory 'llama') -Force
& tar.exe -xf (Join-Path $downloadDirectory 'sqlite-vec.tar.gz') -C (Join-Path $runtimeDirectory 'sqlite-vec')
if ($LASTEXITCODE -ne 0) { throw 'sqlite-vec extraction failed.' }
$whisperMetadata = Invoke-RestMethod 'https://huggingface.co/api/models/ggerganov/whisper.cpp/tree/main'
$whisperHash = ($whisperMetadata | Where-Object path -eq 'ggml-base.en.bin').lfs.oid
if (-not $whisperHash) { throw 'Whisper checksum unavailable.' }
Download-Checked 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin' (Join-Path $modelDirectory 'ggml-base.en.bin') $whisperHash
Download-Checked 'https://huggingface.co/ggml-org/embeddinggemma-300M-GGUF/resolve/main/embeddinggemma-300M-Q8_0.gguf' (Join-Path $modelDirectory 'embeddinggemma-300M-Q8_0.gguf') 'b5ce9d77a3fc4b3b39ccb5643c36777911cc4eb46a66962eadfa3f5f60490d63'
$sileroUri = 'https://raw.githubusercontent.com/snakers4/silero-vad/7e30209a3e901f9842f81b225f3e93d8199902b1/src/silero_vad/data/silero_vad.onnx'
$sileroPath = Join-Path $modelDirectory 'silero_vad.onnx'
if (-not (Test-Path -LiteralPath $sileroPath)) {
    & curl.exe --fail --location --retry 3 --silent --show-error --output $sileroPath $sileroUri
    if ($LASTEXITCODE -ne 0) { throw 'Silero download failed.' }
}
$ollamaManifest = Join-Path $env:USERPROFILE '.ollama/models/manifests/registry.ollama.ai/library/gemma2/2b'
$manifest = Get-Content -LiteralPath $ollamaManifest -Raw | ConvertFrom-Json
$modelLayer = $manifest.layers | Where-Object mediaType -eq 'application/vnd.ollama.image.model'
$generationPath = Join-Path $env:USERPROFILE ('.ollama/models/blobs/' + $modelLayer.digest.Replace(':','-'))
if (-not (Test-Path -LiteralPath $generationPath)) { throw 'Existing Gemma 2 model blob is missing.' }
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'MeetingTeleprompter'
New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$settings = @{}
if (Test-Path -LiteralPath $settingsPath) {
    $previous = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    foreach ($property in $previous.psobject.Properties) { $settings[$property.Name] = $property.Value }
    Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $settingsDirectory ('settings.before-model-setup-' + (Get-Date -Format yyyyMMddHHmmss) + '.json'))
}
$settings.WhisperEndpoint = 'http://127.0.0.1:8178/'
$settings.GeneratorEndpoint = 'http://127.0.0.1:8179/'
$settings.EmbeddingEndpoint = 'http://127.0.0.1:8180/'
$settings.WhisperExecutable = (Get-ChildItem (Join-Path $runtimeDirectory 'whisper') -Recurse -Filter whisper-server.exe | Select-Object -First 1).FullName
$settings.LlamaExecutable = (Get-ChildItem (Join-Path $runtimeDirectory 'llama') -Recurse -Filter llama-server.exe | Select-Object -First 1).FullName
$settings.WhisperModel = Join-Path $modelDirectory 'ggml-base.en.bin'
$settings.GeneratorModel = $generationPath
$settings.EmbeddingModelPath = Join-Path $modelDirectory 'embeddinggemma-300M-Q8_0.gguf'
$settings.EmbeddingModel = 'embeddinggemma-300M-Q8_0-b5ce9d77'
$settings.SileroModelPath = $sileroPath
$settings.SqliteVecPath = (Get-ChildItem (Join-Path $runtimeDirectory 'sqlite-vec') -Recurse -Filter vec0.dll | Select-Object -First 1).FullName
$settings.SemanticSearch = $true
$settings.Threads = 2
if (-not $settings.ContainsKey('Microphone')) { $settings.Microphone = $true }
if (-not $settings.ContainsKey('DataDirectory')) { $settings.DataDirectory = $settingsDirectory }
if (-not $settings.ContainsKey('Folders')) { $settings.Folders = @((Join-Path $projectRoot 'knowledge_base')) }
if (-not $settings.WhisperExecutable -or -not $settings.LlamaExecutable) { throw 'Runtime executables not present in downloaded archives.' }
$settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
$settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $modelDirectory 'installed-settings.json') -Encoding UTF8
Get-ChildItem -LiteralPath $modelDirectory -File | Select-Object Name,Length
Write-Host "Configured $settingsPath"
