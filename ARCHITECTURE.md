# Архитектура Kadr Studio

Kadr Studio разделён на слои с зависимостями только внутрь:

```text
Kadr (WPF views, view-models, composition root)
  -> Kadr.Infrastructure (SQLite, FFmpeg, artifact store, jobs)
  -> Kadr.Application (commands, render graph, preview/storage contracts)
  -> Kadr.Core (immutable ProjectState v4, exact time, validation)

Kadr.MediaHost -> Application + Core + Infrastructure
Kadr.AiServer -> independent HTTP inference boundary -> Ollama/backend
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

`.kadr` — нормализованный SQLite schema v4. Загрузчик читает schema 1–3, оборачивает старый таймлайн в «Исходный монтаж» и мигрирует только при следующем успешном сохранении. Schema v4 хранит последовательности, source-аннотации, ссылки на анализ и декларативные монтажные планы. Сохранение и экспорт пишут временный файл, проверяют его и только затем атомарно публикуют. Одновременная запись одного проекта защищена межпроцессной lease-блокировкой.

Recovery хранит до 20 checksum-защищённых состояний каждого проекта. История проекта встроена в `.kadr`. Экспорт всегда читает оригиналы, проверяет video/audio streams и duration, а при сбое NVENC автоматически повторяется через CPU-кодек.

## ИИ-монтаж

Пользователь работает с одним ИИ-режиссёром, но контур намеренно разделён. `AiMontageAnalysisService` создаёт версионированный source-level индекс; `IMontagePlanningProvider` возвращает только `MontagePlan`; `MontagePlanValidator` проверяет доказательства, Required/Excluded, fingerprint и revision; `MontagePlanCompiler` компилирует белый список операций в отдельный `SequenceState`. Модель не получает `IEditCommand`, файловую систему или возможность изменить `ProjectState`.

Технический FFmpeg/Whisper-слой кэшируется независимо от игры и модели и может запускаться после импорта с фоновым приоритетом. Профильный vision-проход использует контактные листы и уточняет перспективные границы плотным FFmpeg-проходом. Один индекс можно использовать для YouTube и Shorts; 9:16 выполняется статическим crop/reframe через существующие `VideoParameters`.

`KadrStudio.AiServer` — отдельный процесс/сервис и граница доверия для inference. Desktop отправляет ему только prompt/schema и подготовленные локально кадры/contact sheets; сервер не получает путь к исходному видео, `ProjectState`, файловую систему или editor tools. Модель возвращает структурированное решение, а разрешённые tool-вызовы и изменения timeline исполняются только локальным Kadr. Настоящее имя backend-модели и её model store принадлежат серверу и не выбираются desktop-клиентом.

Активная последовательность зеркалируется в верхнеуровневых timeline-полях для совместимости рендера. При переключении текущий вариант сначала синхронизируется, затем live timeline атомарно заменяется снимком выбранного варианта. Исходная последовательность, принятые версии и черновики независимы и участвуют в обычных Undo/Redo, recovery и checkpoints.

## Границы ответственности

- Composition root создаёт FFmpeg/process/cache/storage adapters; view-model их не конструирует.
- View не изменяет `ProjectState`; timeline interaction-controller выдаёт intents.
- Render/cache/playback не читают WPF-модели.
- Автоматизация работает со snapshot и возвращает proposal; stale proposal не применяется.
- Kadr AI Server выполняет model inference, но не редактирует проект: frame-exact анализ и разрешённые editor tools остаются на стороне desktop.
- Desktop использует только `GET /v1/models` и `POST /v1/inference/structured`; Ollama является приватной реализацией backend и не входит во внешний контракт.
- Recording удалён. Реализованные переходы являются типизированными сущностями, а не UI-заглушками.

Эти правила закреплены `ArchitectureBoundaryTests` и `SourceArchitectureTests`. Полный gate описан в [docs/TESTING.md](docs/TESTING.md).
