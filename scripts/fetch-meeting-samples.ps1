$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sampleDirectory = Join-Path $projectRoot 'artifacts/meeting-validation'
New-Item -ItemType Directory -Force -Path $sampleDirectory | Out-Null
$samples = @()
foreach ($offset in @(0,1500,3000)) {
    $rows = Invoke-RestMethod "https://datasets-server.huggingface.co/rows?dataset=edinburghcstr%2Fami&config=ihm&split=test&offset=$offset&length=100"
    $selected = @($rows.rows | Where-Object { ($_.row.end_time - $_.row.begin_time) -ge 2 -and ($_.row.end_time - $_.row.begin_time) -le 10 -and ($_.row.text -split ' ').Count -ge 6 } | Select-Object -First 4)
    foreach ($sample in $selected) {
        $path = Join-Path $sampleDirectory ($sample.row.audio_id + '.wav')
        & curl.exe --fail --location --silent --show-error --retry 2 --output $path $sample.row.audio[0].src
        if ($LASTEXITCODE -ne 0) { throw 'Sample download failed.' }
        $samples += [pscustomobject]@{ Id=$sample.row.audio_id; Meeting=$sample.row.meeting_id; Path=$path; Reference=$sample.row.text; Row=$sample.row_idx }
    }
}
if ($samples.Count -ne 12) { throw 'Expected twelve deterministic AMI samples.' }
$samples | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $sampleDirectory 'samples.json') -Encoding UTF8
Write-Host "Downloaded $($samples.Count) public meeting excerpts from edinburghcstr/ami, ihm/test."
