[CmdletBinding()]
param(
    [string]$Endpoint = 'http://127.0.0.1:5080',
    [string]$ApiKey,
    [switch]$SkipInference
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$base = $Endpoint.TrimEnd('/')
$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($ApiKey)) { $headers.Authorization = "Bearer $ApiKey" }

$live = Invoke-RestMethod -Method Get -Uri "$base/health/live" -TimeoutSec 5
if ($live.status -ne 'live') { throw "Unexpected health/live status: $($live.status)" }
Write-Host 'health/live: live' -ForegroundColor Green

$ready = $null
try {
    $ready = Invoke-RestMethod -Method Get -Uri "$base/health/ready" -Headers $headers -TimeoutSec 15
} catch {
    $body = $_.ErrorDetails.Message
    if ($body) { Write-Warning "health/ready: $body" } else { Write-Warning $_.Exception.Message }
}
if ($SkipInference) { return }
if (-not $ready -or $ready.status -ne 'ready') {
    throw 'Production workers/models are not ready. Run setup, model installation and montage-eval activation first.'
}

$schema = @{
    type = 'object'
    properties = @{ status = @{ type = 'string'; enum = @('ok') } }
    required = @('status')
    additionalProperties = $false
}
$payload = @{
    role = 'Director'
    schema = $schema
    context = @{ purpose = 'readiness-smoke' }
    instruction = 'Return status ok.'
    contextWindowTokens = 4096
    maxTokens = 64
    profile = 'profile-selection'
    requireProduction = $true
} | ConvertTo-Json -Depth 10 -Compress
$turn = Invoke-RestMethod -Method Post -Uri "$base/v2/reason/structured" -Headers $headers `
    -ContentType 'application/json; charset=utf-8' -Body $payload -TimeoutSec 7200
if ([string]::IsNullOrWhiteSpace([string]$turn.content)) {
    throw 'v2/reason/structured returned empty content.'
}
$content = $turn.content | ConvertFrom-Json
if ($content.status -ne 'ok') { throw "Unexpected structured result: $($turn.content)" }
Write-Host 'v2/reason/structured: production worker OK' -ForegroundColor Green
