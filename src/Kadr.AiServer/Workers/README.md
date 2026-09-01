# Kadr worker protocol v2

Каждый analyzer находится в отдельном каталоге под `KADR_AI_WORKERS_ROOT`:

```text
workers/
  video-understanding/
    worker-manifest.json
    kadr-video-understanding.exe
  asr-align/
  diarization/
  audio-events/
  embedding/
  director/
  critic/
```

Пример manifest:

```json
{
  "analyzer": "video-understanding",
  "protocolVersion": "2",
    "executable": "runtime/Scripts/python.exe",
    "arguments": ["-m", "kadr_worker.server", "--analyzer", "video-understanding"],
  "port": 52101
}
```

Gateway принимает только analyzers из allowlist, требует порт `1024..65535`, не разрешает executable выйти за каталог worker и запускает Windows-процесс без окна. Ему передаются `--grpc-port`, `--protocol-version 2`, `--data-root`. Worker обязан слушать только loopback.

Контракт находится в [`kadr_worker_v2.proto`](kadr_worker_v2.proto). `RunJobRequest.asset_ids` — SHA-256 IDs в `${data-root}/assets`; это не desktop paths. `parameters_json` зависит от analyzer. Успешный `result_json` должен быть валидным JSON. Для индексаторов рекомендуемый корневой payload — сериализованный `MediaUnderstandingIndex`; gateway сохраняет его в content-addressed artifact store. Director worker также реализует `CountTokens` реальным tokenizer той модели, которая указана в request.

Версия analyzer входит в job и cache key. Несовместимый protocol, падение worker, повреждённый JSON и отмена дают типизированную job error и не повреждают ProjectState. Повторный job после рестарта создаётся/возобновляется через API, а не через передачу desktop path.

Production manifests не включены в репозиторий: они должны поставляться вместе с версионированными CUDA worker binaries и model manifests. Один и тот же gRPC contract предназначен для Windows binaries и Linux containers.
