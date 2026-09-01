# Kadr AI Server v2

Единственный AI-путь Kadr Studio:

```text
KadrStudio.exe → HTTP API v2 → C# trust gateway
  → versioned loopback gRPC workers → local model/artifact stores
```

Ollama и API v1 больше не участвуют в архитектуре. Desktop не передаёт workers исходные пути: он создаёт analysis proxy и аудиочанки, загружает их по SHA-256, а оригинал остаётся локальным.

## Локальная установка

Эта документация и все команды принадлежат самостоятельной папке `KadrStudioAiServer`. Runtime, веса и все AI-кэши находятся в `KadrStudioAiServer\.kadr-ai`. Каталог целиком исключён из Git; `TEMP`, pip/Hugging Face/PyTorch/CUDA/Triton caches для worker-процессов также перенаправлены туда:

```powershell
.\scripts\install-ai-server-local.ps1
.\scripts\setup-ai-workers.ps1 -InstallDependencies
.\scripts\install-production-models.ps1
# После montage-eval отдельно активируйте vision и planner capability manifests.
.\scripts\run-ai-server.ps1
.\scripts\test-ai-server-connection.ps1
```

Структура данных: `runtime`, `workers`, `models`, `data`, `eval`, `cache`. Setup добавляет runtime-корень в локальное исключение Git и проверяет, что веса не видны в `git status`.

## API

- `POST /v2/assets`, `GET /v2/assets/{sha256}` — возобновляемая content-addressed загрузка;
- `POST /v2/jobs`, `GET /v2/jobs/{id}`, `GET /v2/jobs/{id}/events`, `DELETE /v2/jobs/{id}`;
- `GET /v2/artifacts/{id}` — JSON или binary artifact с Range support;
- analyzer `anime-upscale` — ограниченный video-asset → проверенный MP4 без передачи desktop path;
- `POST /v2/reason/structured` — role-isolated constrained JSON;
- `GET /health/live`, `GET /health/ready`.

Маршруты `/v1/*`, `/api/*` и прямой model discovery удалены. `health/ready` требует manifests основных workers и успешно прошедшие montage-eval capability manifests для planner и vision.

Для локального smoke до набора 30-кейсного montage-eval можно явно запустить desktop с `KADR_STUDIO_AI_ALLOW_DEV_MODELS=1`. Это отключает только требование production-manifest на стороне клиента; source isolation, typed commands, Agent Draft и QC остаются обязательными. Переменная не устанавливается setup-скриптами и не должна использоваться для release gate.

## Конфигурация

- `KADR_AI_DATA_ROOT` — общий корень (по умолчанию `<project>\.kadr-ai`);
- `KADR_AI_WORKERS_ROOT`, `KADR_AI_PRODUCTION_MODELS_ROOT`, `KADR_AI_RUNTIME_DATA_ROOT`;
- `KADR_AI_VISION_MODEL`, `KADR_AI_PLANNER_MODEL` — идентификаторы, совпадающие с capability manifests;
- `KADR_VISION_MODEL` — локальный каталог Qwen3-VL; `KADR_DIRECTOR_MODEL` — единственный GGUF-файл;
- `KADR_DIRECTOR_TOKENIZER`, `KADR_LLAMA_SERVER`, `KADR_REASONING_BACKEND=llama.cpp`;
- `KADR_PLANNER_CONTEXT_TOKENS=16384`, `KADR_LLAMA_GPU_LAYERS=12` — лимиты локального workstation-профиля;
- `KADR_AI_URLS`, `KADR_AI_API_KEY`;
- `KADR_STUDIO_AI_ENDPOINT`, `KADR_STUDIO_AI_API_KEY` — настройки desktop.

Вне loopback обязателен Bearer key. Для другой машины нужен HTTPS reverse proxy или VPN: API key поверх обычного HTTP не шифрует proxy и артефакты.

Planner и Critic запускаются отдельными worker-процессами и не делят историю. Каждый процесс управляет собственным дочерним `llama-server`, который supervisor завершает вместе с worker перед сменой GPU-роли. Ввод измеряется тем же Qwen tokenizer без загрузки весов: 65% окна на stage input, 20% на reasoning/JSON, 15% резерв. JSON ограничивается schema grammar в llama.cpp и повторно проверяется C# gateway. Падение worker, повреждённый JSON и context overflow остаются восстанавливаемыми ошибками стадии.
