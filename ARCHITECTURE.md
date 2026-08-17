# Архитектура Kadr Studio

Kadr Studio разделён на слои с зависимостями только внутрь:

```text
Kadr (WPF views, view-models, composition root)
  -> Kadr.Infrastructure (SQLite, FFmpeg, artifact store, jobs)
  -> Kadr.Application (commands, render graph, preview/storage contracts)
  -> Kadr.Core (immutable ProjectState v3, exact time, validation)

Kadr.MediaHost -> Application + Core + Infrastructure
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

`.kadr` — нормализованный SQLite schema v3. Загрузчик читает schema 1/2 и мигрирует только при следующем успешном сохранении. Сохранение и экспорт пишут временный файл, проверяют его и только затем атомарно публикуют. Одновременная запись одного проекта защищена межпроцессной lease-блокировкой.

Recovery хранит до 20 checksum-защищённых состояний каждого проекта. История проекта встроена в `.kadr`. Экспорт всегда читает оригиналы, проверяет video/audio streams и duration, а при сбое NVENC автоматически повторяется через CPU-кодек.

## Границы ответственности

- Composition root создаёт FFmpeg/process/cache/storage adapters; view-model их не конструирует.
- View не изменяет `ProjectState`; timeline interaction-controller выдаёт intents.
- Render/cache/playback не читают WPF-модели.
- Автоматизация работает со snapshot и возвращает proposal; stale proposal не применяется.
- Ollama даёт смысловые названия, но frame-exact границы определяет визуальный многошаговый анализ.
- Recording удалён. Реализованные переходы являются типизированными сущностями, а не UI-заглушками.

Эти правила закреплены `ArchitectureBoundaryTests` и `SourceArchitectureTests`. Полный gate описан в [docs/TESTING.md](docs/TESTING.md).
