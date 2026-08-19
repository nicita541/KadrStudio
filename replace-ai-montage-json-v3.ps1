param(
    [string]$Path = ".\src\Kadr\Services\OllamaVideoAnalysisService.cs"
)

$ErrorActionPreference = "Stop"

$fullPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    throw "Файл не найден: $fullPath"
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$text = [System.IO.File]::ReadAllText($fullPath)
$newLine = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$hadFinalNewLine = $text.EndsWith("`r`n") -or $text.EndsWith("`n")

# Нормально разбиваем файл на строки (без SimpleMatch).
$lines = [System.Text.RegularExpressions.Regex]::Split($text, "\r\n|\n")

# Повторный запуск безопасен.
if ($text.Contains("using var montageSchema = JsonDocument.Parse(") -and
    $text.Contains("format = montageSchema.RootElement") -and
    $text.Contains("num_predict = 8192") -and
    $text.Contains('TryGetProperty("done_reason"')) {
    Write-Host "Исправление уже применено. Ничего не изменено." -ForegroundColor Yellow
    exit 0
}

# Ищем именно запрос планировщика монтажа по уникальным options:
# temperature=0.12, num_ctx=16384, num_predict=4096.
$candidates = @()

for ($i = 0; $i -lt $lines.Length; $i++) {
    if ($lines[$i].Trim() -ne 'using var response = await _httpClient.PostAsJsonAsync(') {
        continue
    }

    $windowEnd = [Math]::Min($i + 45, $lines.Length - 1)
    $window = ($lines[$i..$windowEnd] -join "`n")

    if ($window.Contains('format = "json"') -and
        $window.Contains('temperature = 0.12') -and
        $window.Contains('num_ctx = 16384') -and
        $window.Contains('num_predict = 4096')) {
        $candidates += $i
    }
}

if ($candidates.Count -ne 1) {
    throw "Ожидался ровно один блок AI-монтажа, найдено: $($candidates.Count). Файл не изменён."
}

$start = [int]$candidates[0]
$end = -1

for ($i = $start; $i -lt [Math]::Min($start + 80, $lines.Length); $i++) {
    if ($lines[$i].Trim() -eq 'using var result = JsonDocument.Parse(ExtractJson(rawContent));') {
        $end = $i
        break
    }
}

if ($end -lt $start) {
    throw "Конец блока AI-монтажа не найден. Файл не изменён."
}

$oldBlock = ($lines[$start..$end] -join $newLine)

# Последняя защита перед записью.
if (-not $oldBlock.Contains('format = "json"') -or
    -not $oldBlock.Contains('temperature = 0.12') -or
    -not $oldBlock.Contains('num_predict = 4096')) {
    throw "Найденный блок не прошёл проверку. Файл не изменён."
}

$newBlock = @'
        using var montageSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "summary": {
                  "type": "string"
                },
                "items": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "segment_id": {
                        "type": "string"
                      },
                      "role": {
                        "type": "string",
                        "enum": [
                          "hook",
                          "setup",
                          "development",
                          "payoff",
                          "ending"
                        ]
                      },
                      "transition_after": {
                        "type": "string",
                        "enum": [
                          "none",
                          "cross_dissolve",
                          "dip_to_black"
                        ]
                      },
                      "volume": {
                        "type": "number"
                      },
                      "subtitles": {
                        "type": "boolean"
                      }
                    },
                    "required": [
                      "segment_id",
                      "role",
                      "transition_after",
                      "volume",
                      "subtitles"
                    ],
                    "additionalProperties": false
                  }
                }
              },
              "required": [
                "summary",
                "items"
              ],
              "additionalProperties": false
            }
            """);

        using var response = await _httpClient.PostAsJsonAsync(
            "api/chat",
            new
            {
                model,
                stream = false,
                think = false,
                format = montageSchema.RootElement,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content =
                            "Ты универсальный режиссёр монтажа. " +
                            "Верни только JSON строго по переданной JSON Schema, без Markdown и без текста вокруг JSON. " +
                            "Используй только переданные segment_id и каждый не более одного раза. " +
                            "Порядок items — итоговый порядок монтажа. " +
                            "Обязательные элементы включай всегда. " +
                            "Не используй все кандидаты без необходимости: выбери материал под целевую длительность. " +
                            "Роли: hook, setup, development, payoff, ending."
                    },
                    new
                    {
                        role = "user",
                        content = contextText.ToString()
                    }
                },
                options = new
                {
                    temperature = 0,
                    num_ctx = 32768,
                    num_predict = 8192
                }
            },
            cancellationToken);

        await EnsureSuccessAsync(
            response,
            $"ИИ-модель {model} не составила план монтажа",
            cancellationToken);

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var envelope = JsonDocument.Parse(responseJson);

        var rawContent =
            envelope.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
            ?? string.Empty;

        var doneReason =
            envelope.RootElement.TryGetProperty("done_reason", out var doneReasonElement)
                ? doneReasonElement.GetString()
                : null;

        var evalCount =
            envelope.RootElement.TryGetProperty("eval_count", out var evalCountElement) &&
            evalCountElement.TryGetInt32(out var parsedEvalCount)
                ? parsedEvalCount
                : 0;

        if (string.IsNullOrWhiteSpace(rawContent))
        {
            throw new InvalidOperationException(
                $"ИИ вернул пустой монтажный план. done_reason={doneReason ?? "unknown"}, eval_count={evalCount}.");
        }

        if (string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase) ||
            !rawContent.TrimEnd().EndsWith("}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ИИ оборвал JSON до завершения. done_reason={doneReason ?? "unknown"}, eval_count={evalCount}.");
        }

        using var result = JsonDocument.Parse(ExtractJson(rawContent));
'@

$newBlock = $newBlock.Replace("`r`n", "`n").Replace("`n", $newLine)

$backupPath = Join-Path $env:TEMP (
    "OllamaVideoAnalysisService.cs." +
    (Get-Date -Format "yyyyMMdd-HHmmss") +
    ".bak"
)
[System.IO.File]::WriteAllText($backupPath, $text, $utf8NoBom)

$before = if ($start -gt 0) { $lines[0..($start - 1)] } else { @() }
$after = if ($end + 1 -lt $lines.Length) { $lines[($end + 1)..($lines.Length - 1)] } else { @() }

$updatedLines = @()
$updatedLines += $before
$updatedLines += [System.Text.RegularExpressions.Regex]::Split($newBlock, "\r\n|\n")
$updatedLines += $after

$updated = $updatedLines -join $newLine
if ($hadFinalNewLine -and -not ($updated.EndsWith("`r`n") -or $updated.EndsWith("`n"))) {
    $updated += $newLine
}

[System.IO.File]::WriteAllText($fullPath, $updated, $utf8NoBom)

# Самопроверка. При любой проблеме автоматически откатываем файл из backup.
$check = [System.IO.File]::ReadAllText($fullPath)
$ok =
    $check.Contains("using var montageSchema = JsonDocument.Parse(") -and
    $check.Contains("format = montageSchema.RootElement") -and
    $check.Contains("num_ctx = 32768") -and
    $check.Contains("num_predict = 8192") -and
    $check.Contains('TryGetProperty("done_reason"') -and
    $check.Contains("using var result = JsonDocument.Parse(ExtractJson(rawContent));")

if (-not $ok) {
    [System.IO.File]::WriteAllText($fullPath, $text, $utf8NoBom)
    throw "Самопроверка не прошла. Исходный файл автоматически восстановлен."
}

Write-Host ""
Write-Host "Готово. Изменён только блок AI-монтажа." -ForegroundColor Green
Write-Host "Старый диапазон: строки $($start + 1)-$($end + 1)" -ForegroundColor DarkGray
Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Дальше выполни:" -ForegroundColor Yellow
Write-Host "  git diff --check"
Write-Host "  git diff --stat"
Write-Host "  dotnet build .\src\Kadr\KadrStudio.csproj -c Release -warnaserror"
