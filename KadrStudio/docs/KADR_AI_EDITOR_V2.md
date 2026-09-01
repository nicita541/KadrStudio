# Kadr AI Editor v2.1

## Монтажный конвейер

```text
Запрос + MontageProfile
  → MediaUnderstandingIndex
  → EditorialBrief
  → semantic coarse-to-fine retrieval
  → MontageGraph
  → специализированные DraftPatch-проходы
  → native DraftCommandCompiler
  → deterministic DraftQualityReport
  → ReviewingDraft (A/B + diff)
```

Старого цикла `model → один tool`, technical-plan approval и отдельного AI Montage больше нет. Компилятор клонирует исходный `SequenceState` в Agent Draft и изменяет его типизированными командами; он не пересобирает сохранённый материал из media source. Исходная последовательность и файлы не меняются.

## Интерфейс

Основной WPF-редактор сохранён: медиатека, preview, timeline и ручные инструменты работают как прежде. В левой навигации раздел «ИИ» содержит выбор профиля, текст задачи, стадии выполнения, A/B «Draft / Source», diff и кнопки принять, переделать или удалить Draft. Пользователь проверяет готовый безопасный Draft, а не технический план.

## Anime Episode / Exact Complement

Профиль `AnimeEpisode` для запроса «удали OP и ED, остальное не меняй» разрешает только два `Remove`: `Opening` и `Ending`. Episode body, post-credits, preview, recap и весь complement защищены. Reorder, retime, reframe, captions, transitions и audio mix блокируются.

Граница требует frame/shot evidence, покрытие нужного интервала, явный `segment_role` и минимум две независимые модальности. Confidence 0.65–0.85 создаёт `NeedsReview` с альтернативными границами; ниже 0.65 контроллер запускает дополнительные probes и возвращает точный `EvidenceGap`.

Native exact-complement QC проверяет ровно два удаления, frame snapping, длительность, source mapping, occurrence IDs, link groups, эффекты, маркеры, титры, переходы и синхронный ripple для video, всех audio и ASS subtitle streams. Post-credit и preview обязаны сохраниться.

## Индекс и retrieval

Индекс хранит `Source → Chapter → Scene → Shot → Moment`, измеренные facts, semantic hypotheses, role candidates, audio events, speaker turns и word alignment. `CoverageMap` не заполняет промежутки между редкими samples. Embeddings находятся во внешнем artifact/vector store; working set резервирует начало, конец, обе границы кандидатов и counterevidence. Механического отсечения хвоста массива нет.

OCR является opportunistic capability: отсутствие локального OCR runtime не заставляет повторно индексировать весь source и не блокирует визуальный монтаж. Frames/motion сохраняют честную измеренную плотность; непрерывное покрытие обязательно только для audio/transcript и для адресных dense probes, которые явно запросил валидатор.

## Workers и production gate

Windows supervisor запускает `video-understanding`, `audio-events`, `asr-align`, `diarization`, `embedding`, `director`, `critic` по protocol v2. Тот же контракт предназначен для Linux containers.

Старые `qwen3-vl:4b-instruct` и `qwen3.5:9b` остаются заблокированными как dev-only. Локальный профиль для RTX 5060 Ti 8 ГБ использует `Qwen3-VL-8B-Instruct` в NF4 и `Qwen3-30B-A3B-Instruct-2507` в `Q4_K_M GGUF` через локальный `llama.cpp`. Planner содержит 30.5B параметров, но активирует 3.3B на токен; non-thinking Instruct-вариант выбран для детерминированного structured output. Предквантизованный GGUF занимает около 18.6 ГБ и memory-map'ится без 60+ ГиБ transient commit, обнаруженного у runtime-квантизации исходного BF16 checkpoint. Planner использует 12 GPU layers, Q8 KV-cache и effective context 16K; Director и Critic запускаются раздельно. Vision получает отдельный NF4 runtime, не более 24 позиционно-сбалансированных кадров двумя пакетами и лимит 5120 MiB для весов. Production job всё равно требует manifest с model hash, tokenizer, context window, ролями, профилями и успешно пройденной ревизией montage-eval. Gate применяется отдельно к vision и planner/critic.

`run-montage-eval.ps1` агрегирует внешний локальный corpus: минимум 30 кейсов, по пять на каждый из шести профилей. Activation требует 100% сохранности source, не менее 95% технически завершённых задач, не менее 80% human-usable Draft, отсутствие повторного graph без evidence/context overflow и точное совпадение model hash. Медиа corpus в Git не добавляется.

## Хранение и миграция

SQLite schema v9 и persistence format v5 хранят stream descriptors, coverage, hierarchy, occurrence-aware targets, graph, patches, checkpoints, receipts, QC, upscale jobs и track renditions. Schema v1–v9 читаются; завершённые старые Draft остаются обычными последовательностями, незавершённая legacy-задача перезапускается по сохранённому запросу.

Workflow: `Indexing → Directing → Retrieving → RoughCut → BoundaryRefining → Compiling → Verifying → ReviewingDraft → Accepted / Discarded`.

Интернет не используется для доказательства локальных таймкодов. Опциональный research gateway принимает только явный текстовый запрос пользователя и никогда не отправляет кадры или звук.
