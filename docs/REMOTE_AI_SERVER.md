# Kadr AI Server

Kadr Studio использует один AI-путь:

```text
KadrStudio.exe -> HTTP -> KadrStudio.AiServer.exe -> private Ollama backend
```

Desktop не запускает Ollama, не скачивает модель и не хранит AI runtime в проекте. По умолчанию он подключается к `http://127.0.0.1:5080/`. Для другого GPU-ПК задаются `KADR_STUDIO_AI_ENDPOINT`, `KADR_STUDIO_AI_API_KEY` и при необходимости публичные aliases vision/planner. Desktop не принимает приватное имя Ollama-модели.

## Локальная установка

Рабочий корень фиксирован вне репозитория: `F:\KadrStudioData\AiServer`.

```powershell
.\scripts\install-ai-server-local.ps1 -DataRoot 'F:\KadrStudioData\AiServer'
.\scripts\run-ai-server.ps1 -DataRoot 'F:\KadrStudioData\AiServer'
.\scripts\test-ai-server-connection.ps1
```

Структура:

- `runtime` — self-contained Kadr AI Server;
- `ollama-runtime` — приватный Ollama runtime;
- `ollama-models` — server-managed model store.

Installer публикует сервер во временный каталог, атомарно заменяет runtime и умеет один раз перенести валидный старый Ollama store (`blobs` + `manifests`) из `.ollama`/`AI\models` без второй копии. Неизвестные каталоги он не удаляет.

## API v1

Внешняя граница содержит только model discovery и единый structured inference:

- `GET /v1/models`;
- `POST /v1/inference/structured`.

`GET /v1/models` возвращает две роли: `kadr-planner:latest` и `kadr-vision:latest`. Inference request содержит `schema`, `systemPrompt`, `userPrompt`, optional `model`, `think`, `reasoningTokens`, `images`, `temperature`, `contextTokens`, `maxTokens`. Ответ содержит `content`, `doneReason`, `evalCount`, `reasoningEvalCount`, `attemptCount`. Planner не принимает изображения. Ollama-compatible `/api/*`, старые `/v1/agent/turn` и `/v1/vision/analyze` не публикуются.

Служебные проверки: `GET /health/live`, `GET /health/ready`, `GET /health`.

## Переменные сервера

- `KADR_AI_URLS` — listen URL, default `http://127.0.0.1:5080`;
- `KADR_AI_API_KEY` — Bearer key для не-loopback клиентов;
- `KADR_AI_OLLAMA_ENDPOINT` — приватный backend, default `http://127.0.0.1:11436/`;
- `KADR_AI_OLLAMA_EXE` — путь к внешнему `ollama.exe`;
- `KADR_AI_MODELS_ROOT` — внешний model store;
- `KADR_AI_MODEL` — backend model, default `qwen3-vl:4b-instruct`;
- `KADR_AI_PUBLIC_MODEL` — публичный alias, default `kadr-vision:latest`;
- `KADR_AI_PLANNER_MODEL` — planner/critic backend, default `qwen3.5:9b`;
- `KADR_AI_PLANNER_PUBLIC_MODEL` — публичный planner alias, default `kadr-planner:latest`;
- `KADR_AI_MANAGE_OLLAMA`, `KADR_AI_AUTO_PULL` — server-side управление backend/model.

При публикации вне loopback обязателен API key. Bearer key поверх обычного HTTP не шифрует трафик: для LAN/Internet нужен HTTPS reverse proxy или VPN. Ollama наружу не публикуется.

Планировщик работает с thinking и динамическим контекстом до 32K, vision — без thinking и с контекстом до 8K. Сервер выбирает минимальное подходящее окно, заранее резервируя место под reasoning, финальный JSON и safety margin. `reasoningTokens` задаёт отдельный бюджет размышления, а `maxTokens` относится только к финальному JSON. При пустом, оборванном или нарушающем schema результате сервер делает один finalizer-вызов с `think=false` из исходных pinned system/user/schema; повреждённый ответ и hidden thinking в retry не копируются. JSON Schema включается в system prompt и проверяется сервером; ошибка возвращает типизированный `errorCode`, `doneReason`, `evalCount` и `attemptCount`. Скрытые рассуждения Ollama не возвращаются desktop и не записываются в agent log. При смене роли сервер сначала освобождает предыдущую модель через `keep_alive=0`, затем загружает нужную; качество имеет приоритет над задержкой переключения.

## Release

Desktop release содержит FFmpeg/FFprobe и MediaHost, но не содержит Ollama или модель. AI Server публикуется отдельно. Agent logs хранятся в `KADR_STUDIO_DATA_ROOT` либо в per-user local application data; `Logs/` в workspace не используется.
