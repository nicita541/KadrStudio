# Архитектура Kadr Studio

Kadr Studio разделён на слои с зависимостями только внутрь:

```text
Kadr (WPF views, view-models, composition root)
  -> Kadr.Infrastructure (SQLite, FFmpeg, artifact store, jobs)
  -> Kadr.Application (commands, render graph, preview/storage contracts)
  -> Kadr.Core (immutable ProjectState, exact time, validation)

Kadr.MediaHost -> Application + Core + Infrastructure
../KadrStudioAiServer -> independent HTTP trust gateway -> versioned local workers
```

## Единственное состояние проекта

`EditorSession.State` (`ProjectState`) — единственное рабочее состояние. Любое действие проходит как `EditorIntent`/`IEditCommand` внутри `EditTransaction`. Перед commit выполняется общая валидация; результат содержит новый snapshot и `ProjectChangeSet`. Undo/redo также возвращают immutable snapshot и диапазоны инвалидации.

WPF получает однонаправленную `ProjectViewState` через `ProjectViewMapper`. Это только проекция для binding и геометрии: она не сохраняется, не экспортируется, не используется для анализа и не преобразуется обратно в проект. Инспекторы редактируют отдельные draft-объекты и отправляют итоговую команду.

Время ядра хранится в `TimelineTime` (240 000 ticks/s). Точные 24000/1001, 30000/1001 и 60000/1001 не округляются. `double` допускается только на краях WPF и FFmpeg.

## Рендер и предпросмотр

`RenderGraphCompiler` компилирует один типизированный граф для preview и export. V-дорожки дают только изображение, A-дорожки — только звук, текст остаётся отдельным overlay в интерактивном режиме. Подписи разделены на source decode, video graph, audio graph и overlay; `ProjectChangeSet` инвалидирует только затронутые диапазоны.

`Kadr.MediaHost` — отдельный постоянный процесс с версионированным named-pipe протоколом. Внутри playback-сессии видео и аудио имеют независимые поколения, worker-наборы, отмену и восстановление. BGRA передаётся в `WriteableBitmap`, stereo float32 PCM — в WASAPI; реальные peak/RMS считаются из того же микса. Очереди ограничены, старые поколения фильтруются, при buffering сохраняется последний корректный кадр.

## Медиа и производные данные

`IMediaRegistry` хранит stream descriptors, fractional/VFR metadata, fast и verified content fingerprints, online/offline/relink состояние. Импорт не копирует и не меняет исходники.

`IArtifactStore` объединяет proxy, thumbnail tiles, waveform, conform/analysis artifacts. Записи атомарны и проверяются checksum; LRU имеет дисковый/памятный бюджет. Папку и лимит можно менять из UI. Waveform — версионированная stereo min/max/RMS-пирамида; визуализируется только видимый диапазон с плотностью около одной колонки на два физических пикселя. Видеоплитки также извлекаются только для видимой части клипа: время плитки вычисляется из общего viewport, старое поколение отменяется при scroll/zoom, а число параллельных FFmpeg-задач ограничено.

## Хранение и экспорт

`.kadr` — нормализованный SQLite schema v9 и persistence format v5. Загрузчик читает schema v1–v9, оборачивает старый таймлайн в «Исходный монтаж» и мигрирует только при следующем успешном сохранении. Schema v9 хранит stream descriptors, иерархический индекс, coverage, occurrence-aware edit targets, montage graph, task checkpoints, native receipts, exact-complement QC, upscale jobs и track renditions. Сохранение и экспорт пишут временный файл, проверяют его и только затем атомарно публикуют. Одновременная запись одного проекта защищена межпроцессной lease-блокировкой.

Recovery хранит до 20 checksum-защищённых состояний каждого проекта. История проекта встроена в `.kadr`. Экспорт всегда читает оригиналы, проверяет video/audio streams и duration, а при сбое NVENC автоматически повторяется через CPU-кодек.

## ИИ-монтаж

Пользователь работает с одним ИИ-режиссёром через конвейер `Indexing → Directing → Retrieving → RoughCut → BoundaryRefining → Compiling → Verifying → ReviewingDraft`. Модель формирует `EditorialBrief`, role hypotheses и семантический `MontageGraph`; она не получает editor tools, файловую систему или возможность изменить `ProjectState`. Пользователь принимает, пересматривает или удаляет уже собранный безопасный Agent Draft — plan approval и универсальный tool-loop удалены.

`MediaUnderstandingIndex` хранит иерархию `Source → Chapter → Scene → Shot → Moment`. Измеренные факты, семантические гипотезы и редакторские решения разделены. `CoverageMap` ведётся отдельно для frames, motion, audio, transcript и OCR и содержит только реально измеренные интервалы. Тяжёлые кадры, embeddings и аудиофичи остаются в artifact/vector store; bounded working set резервирует начало, конец, границы кандидатов и counterevidence, не отрезая хвост материала.

Для `AnimeEpisode + RemoveNamedSections + ExactComplement` разрешены только два удаления с ролями `Opening` и `Ending`. Весь complement защищён; post-credit и preview обязаны сохраниться. Каждая граница требует frame/shot evidence, а решение — минимум две независимые модальности. При недостатке данных контроллер запускает адресный analyzer по типизированному `EvidenceGap`; при средней уверенности создаёт `NeedsReview` Draft с альтернативами и обязательным A/B.

`DraftCommandCompiler` клонирует исходный `SequenceState` и применяет нормализованные диапазоны справа налево через общий `TimelineRangeTransformer`. Он не реконструирует сохранённый материал из source. Video, все audio streams, stream-backed ASS subtitles, attachments, link groups, эффекты, маркеры, титры и переходы сохраняются. Native receipts и deterministic diff позволяют exact-complement QC доказать, что изменились только заявленные диапазоны.

Соседнее приложение `KadrStudioAiServer` — C# trust gateway и supervisor. Desktop передаёт content-addressed analysis proxy/chunks, а не путь к оригиналу. Версионированные loopback workers `video-understanding`, `audio-events`, `asr-align`, `diarization`, `embedding`, `anime-upscale`, `director` и `critic` возвращают структурированные и бинарные artifacts. Публичный контракт — `/v2/assets`, `/v2/jobs`, `/v2/artifacts` и `/v2/reason/structured`; API v1 и Ollama tool-loop удалены. Редактор не читает server model store и не запускает Python/CUDA-процессы.

Локальный профиль RTX 5060 Ti 8 ГБ использует `Qwen3-VL-8B-Instruct` в NF4 и `Qwen3-30B-A3B-Instruct-2507` в предквантизованном `Q4_K_M GGUF` через project-local `llama.cpp`. 30B MoE активирует около 3B параметров на токен. Предквантизованный planner memory-map'ится сразу и не создаёт 60+ ГиБ transient commit, как runtime-квантизация BF16 checkpoint. GPU-heavy workers выполняются последовательно; planner выгружает 12 слоёв на GPU и использует Q8 KV-cache при effective context 16K. Vision получает два позиционно-сбалансированных пакета по 12 кадров и отдельный лимит 5120 MiB для весов. Director и Critic имеют разные процессы/контексты даже при общем GGUF.

Model hash, tokenizer, context, роли, профили и revision montage-eval фиксируются в `ModelCapabilityManifest`. Ни одна модель не получает production role до прохождения локального eval; старые 4B/9B остаются явно заблокированными. Повреждённый JSON, worker crash и context overflow являются recoverable failure с checkpoint, а fingerprint отклонённого graph запрещает повтор без нового evidence или изменившегося запроса.

## Таймлайн

`TimelineSnapEngine` — единая чистая математика привязки move/trim/Razor. Порог задаётся в экранных пикселях; точный существующий край имеет приоритет над округлением к кадру. Linked clips перемещаются одним delta. Разрешение коллизий выбирает ближайшую допустимую границу, а не перебрасывает клип через середину соседа. `TimelineFrameNavigator` использует `TimelineTime` и рациональный `FrameRate`, поэтому повторные шаги на 24000/1001 и 30000/1001 не накапливают ошибку.

Активная последовательность зеркалируется в верхнеуровневых timeline-полях для совместимости рендера. При переключении текущий вариант сначала синхронизируется, затем live timeline атомарно заменяется снимком выбранного варианта. Исходная последовательность, принятые версии и черновики независимы и участвуют в обычных Undo/Redo, recovery и checkpoints.

## Границы ответственности

- Composition root создаёт FFmpeg/process/cache/storage adapters; view-model их не конструирует.
- View не изменяет `ProjectState`; timeline interaction-controller выдаёт intents.
- Render/cache/playback не читают WPF-модели.
- Автоматизация работает со snapshot и возвращает proposal; stale proposal не применяется.
- Kadr AI Server выполняет model inference, но не редактирует проект: frame-exact анализ и разрешённые editor tools остаются на стороне desktop.
- Desktop использует только API v2 AI Server; worker protocol и model store являются приватной реализацией сервера.
- Recording удалён. Реализованные переходы являются типизированными сущностями, а не UI-заглушками.

Эти правила закреплены `ArchitectureBoundaryTests` и `SourceArchitectureTests`. Полный gate описан в [docs/TESTING.md](docs/TESTING.md).
