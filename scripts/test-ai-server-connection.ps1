param(
    [string]$Endpoint = 'http://127.0.0.1:5080',
    [string]$ApiKey,
    [switch]$SkipInference
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$base = $Endpoint.TrimEnd('/')
$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
    $headers.Authorization = "Bearer $ApiKey"
}

Write-Host "Testing Kadr AI Server: $base" -ForegroundColor Cyan

$live = Invoke-RestMethod -Method Get -Uri "$base/health/live" -TimeoutSec 5
if ($live.status -ne 'live') {
    throw "Unexpected health/live status: $($live.status)"
}
Write-Host "health/live: $($live.status)" -ForegroundColor Green

try {
    $ready = Invoke-RestMethod -Method Get -Uri "$base/health/ready" -Headers $headers -TimeoutSec 10
    Write-Host "health/ready: $($ready.status)" -ForegroundColor Green
}
catch {
    Write-Warning "health/ready is not ready yet. The first model preparation can still be running: $($_.Exception.Message)"
}

# This call goes through EnsureReadyAsync. On first launch it can also wait for the
# server-managed model download, so the timeout is intentionally long.
$models = Invoke-RestMethod -Method Get -Uri "$base/v1/models" -Headers $headers -TimeoutSec 7200
if (-not $models.models -or $models.models.Count -lt 1) {
    throw 'AI server returned no public model aliases.'
}

$modelAlias = [string]$models.models[0].id
if ([string]::IsNullOrWhiteSpace($modelAlias)) {
    throw 'AI server returned an empty public model alias.'
}
Write-Host "v1/models: $modelAlias" -ForegroundColor Green

if (-not $SkipInference) {
    $schema = @{
        type = 'object'
        properties = @{
            status = @{
                type = 'string'
                enum = @('ok')
            }
        }
        required = @('status')
        additionalProperties = $false
    }
    $payload = @{
        schema = $schema
        systemPrompt = 'Return only JSON that follows the supplied schema.'
        userPrompt = 'Kadr AI Server readiness check. Return status ok.'
    } | ConvertTo-Json -Depth 12 -Compress

    $turn = Invoke-RestMethod `
        -Method Post `
        -Uri "$base/v1/inference/structured" `
        -Headers $headers `
        -ContentType 'application/json; charset=utf-8' `
        -Body $payload `
        -TimeoutSec 7200

    if ([string]::IsNullOrWhiteSpace([string]$turn.content)) {
        throw 'v1/inference/structured returned empty content.'
    }

    try {
        $turnJson = $turn.content | ConvertFrom-Json
    }
    catch {
        throw "v1/inference/structured returned non-JSON content: $($turn.content)"
    }

    if ($turnJson.status -ne 'ok') {
        throw "v1/inference/structured readiness response is unexpected: $($turn.content)"
    }

    Write-Host 'v1/inference/structured: inference OK' -ForegroundColor Green
}

$readyAfter = Invoke-RestMethod -Method Get -Uri "$base/health/ready" -Headers $headers -TimeoutSec 10
if ($readyAfter.status -ne 'ready') {
    throw "AI server did not become ready: $($readyAfter.status)"
}
Write-Host "health/ready: $($readyAfter.status)" -ForegroundColor Green
Write-Host 'Connection test completed.' -ForegroundColor Green
