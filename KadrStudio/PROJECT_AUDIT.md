# Kadr Studio V2 — контекст проекта для разработчика и ИИ

## 1. Что это за проект

Kadr Studio — локальный видеоредактор для Windows x64 на .NET 10 и WPF. Он хранит
исходники, проект, монтаж, preview и экспорт на машине пользователя. Модель не получает
доступ к файловой системе редактора и не редактирует проект напрямую: независимый
Kadr AI Server возвращает структурированные результаты, а desktop проверяет их и
применяет типизированными командами только к отдельному Agent Draft.

Основные возможности текущего V2:

- многодорожечный видео- и аудиомонтаж, текст, субтитры, переходы, Undo/Redo;
- точное время `TimelineTime` с базой 240 000 ticks/s и fractional frame rates;
- общий render graph для preview и экспорта;
- отдельный процесс `Kadr.MediaHost` для видео/аудио preview;
- SQLite-проекты `.kadr`, recovery, история и атомарное сохранение;
- AI Editor V2: мультимодальный индекс, `MontageGraph`, безопасный Agent Draft и QC;
- локальный/удалённый Kadr AI Server API v2 с versioned Python workers;
- AnimeSR-X upscale открытого таймлайна с отменой и независимыми rendition-дорожками;
- локальные автосубтитры через whisper.cpp;
- self-contained Release publish для `win-x64`.

## 2. Архитектурные границы

```text
Kadr (WPF, ViewModel, adapters, composition root)
  -> Kadr.Infrastructure (SQLite, FFmpeg, cache, jobs)
  -> Kadr.Application (commands, workflows, render/storage contracts)
  -> Kadr.Core (immutable domain, exact time, validation)

Kadr.MediaHost -> Core + Application + Infrastructure
Kadr.AiServer  -> HTTP API v2 -> supervised local workers
```

Зависимости направлены внутрь. `Kadr.Core` не знает о WPF, файлах, FFmpeg или сети.
`Kadr.Application` не зависит от UI. View не изменяет domain state напрямую.
Архитектурные запреты проверяются `ArchitectureBoundaryTests` и
`SourceArchitectureTests`.

### Единственное состояние

`EditorSession.State` (`ProjectState`) — единственное сохраняемое рабочее состояние.
Изменения проходят через `IEditCommand` внутри атомарной `EditTransaction`. Перед
commit выполняется `ProjectValidator`; затем создаются новый immutable snapshot,
`ProjectChangeSet` и запись Undo/Redo. `ProjectViewState` — только WPF-проекция и не
является источником истины.

Проект содержит несколько `SequenceState`. Активная последовательность зеркалируется
в live timeline-полях `ProjectState`; перед переключением снимок синхронизируется.
Оригинал, Agent Draft, принятый монтаж и upscale rendition остаются независимыми.

## 3. Карта репозитория и точки входа

| Путь | Назначение | Ключевые точки |
|---|---|---|
| `src/Kadr.Core` | Domain и инварианты | `ProjectState`, `TimelineTime`, `ProjectValidator` |
| `src/Kadr.Application` | Команды и use cases | `EditorSession`, `ProjectCommands`, `EditorialPipeline` |
| `src/Kadr.Infrastructure` | Файлы и процессы | `SqliteProjectStore`, `FfmpegRenderEngine`, `DiskMediaArtifactCache` |
| `src/Kadr` | WPF desktop | `App`, `MainWindow`, `MainViewModel`, `EditorWorkspaceCompositionRoot` |
| `src/Kadr.MediaHost` | Out-of-process preview | `MediaHostProgram`, `MediaHostServer`, `HostPlaybackSession` |
| `src/Kadr.AiServer` | Trust gateway API v2 | `Program`, `KadrV2Endpoints`, job/artifact/worker services |
| `workers/python/kadr_worker` | Versioned AI workers | `server.py`, `analyzers.py`, `animesr_upscale.py` |
| `config` | Production model manifest | `ai-model-pack.production.json` |
| `scripts` | Setup, eval, smoke, release | `build-release.ps1`, AI Server/model scripts |
| `tests` | Unit, UI, integration, server tests | четыре test-проекта в solution |
| `tools/win-x64` | Поставляемые FFmpeg binaries | `ffmpeg.exe`, `ffprobe.exe` |

Composition root находится в `src/Kadr/Services/EditorWorkspaceCompositionRoot.cs`.
Новые process/file/network adapters нужно регистрировать там, а не создавать во
ViewModel.

## 4. Основные потоки данных

### Ручное редактирование

```text
WPF intent -> MainViewModel -> IEditCommand -> EditorSession
  -> ProjectValidator -> новый ProjectState -> ProjectViewMapper -> WPF
```

Все геометрические изменения должны сохранять track IDs, linked V/A, subtitle streams,
transitions и точное время. Нельзя изменять `ProjectState` из code-behind.

### Preview и экспорт

`RenderGraphCompiler` создаёт общий типизированный `RenderPlan`. Preview отправляет его
в постоянный `Kadr.MediaHost` по версионированному named-pipe protocol. Видео передаётся
как BGRA, аудио — stereo float32 PCM. Video, Audio и Overlay имеют независимые signatures
и generations. Экспорт использует тот же граф, H.264/AAC и NVENC с CPU fallback.

Proxy, thumbnails и waveform лежат в checksum-защищённом `IArtifactStore`; viewport
запрашивает только видимый диапазон. Кэш можно удалить и построить заново, исходники и
`.kadr` при этом не меняются.

### AI Editor V2

```text
User request + profile
  -> MediaUnderstandingIndex
  -> EditorialBrief
  -> hierarchical retrieval
  -> MontageGraph
  -> DraftPatch passes
  -> DraftCommandCompiler
  -> deterministic DraftQualityReport
  -> ReviewingDraft -> Accepted / Revised / Discarded
```

Модель формирует только структурированные hypotheses/graph. Она не получает editor
tools и не применяет команды. Desktop компилирует graph в новый `SequenceState`, хранит
receipts/diff и проверяет source revision, coverage, boundaries и protected complement.
Старые API v1, Ollama runtime, generic tool-loop и technical-plan approval удалены.

### AI Server и workers

Desktop использует `/v2/assets`, `/v2/jobs`, `/v2/artifacts` и
`/v2/reason/structured`. Исходный локальный путь серверу не передаётся: desktop создаёт
content-addressed proxy/chunks. C# gateway валидирует auth, manifests, JSON schemas,
job state и artifacts; supervisor запускает workers на loopback. Planner и Critic имеют
раздельные процессы и контексты.

Вне loopback требуется Bearer key и защищённый транспорт (HTTPS reverse proxy или VPN).
Production role разрешается только model capability manifest после montage-eval.

### Upscale

AnimeSR-X обрабатывает выбранные visual tracks последовательности, которая была открыта
при запуске. Job, отмена и готовая rendition публикуются в эту sequence даже при
последующем переключении UI на другой timeline. Производные video-only источники не
заменяют оригинал; пользователь переключает rendition отдельно.

## 5. Хранение и локальные каталоги

- Текущая SQLite schema: **v9**, читаются schema v1–v9; миграция выполняется при
  следующем успешном сохранении.
- Текущий document persistence format: **v5**.
- Текущий agent-task persistence format: **v4**.
- Сохранение, recovery и export используют временный файл, проверку и атомарную замену.
- История и recovery ограничены и checksum-защищены; concurrent writer блокируется lease.

Каталоги, которые не являются исходным кодом:

| Каталог | Содержимое | Политика |
|---|---|---|
| `.kadr-ai` | runtime, модели, manifests, AI data/eval/cache | не удалять при code cleanup |
| `LocalData` | настройки, recovery, history, logs, cache, temp | пользовательские данные; не удалять |
| `artifacts` | ручные результаты диагностики/eval | не удалять автоматически |
| `release` | self-contained publish и ZIP | воспроизводим полной сборкой |
| `bin`, `obj`, `.vs`, `TestResults`, `__pycache__` | сборочные/IDE-кэши | можно очищать |
| `tools/win-x64` | поставляемые FFmpeg/FFprobe | обязательная часть release |

Исходные медиа никогда не должны открываться на запись. Секреты и веса не добавляются
в Git.

## 6. Сборка и проверка

Требуется Windows x64 и .NET 10 SDK. Полный gate:

```powershell
.\scripts\build-release.ps1 -SkipSdkInstall
```

Сценарий выполняет restore, Release build с warnings-as-errors, все тестовые проекты,
self-contained publish, проверяет FFmpeg/FFprobe/MediaHost, запускает
`KadrStudio.exe --launch-smoke` и создаёт:

```text
release/KadrStudio-win-x64/KadrStudio.exe
release/KadrStudio-win-x64.zip
```

MediaHost integration tests требуют доступ к Windows named pipes. Live AI tests
используют внешнее медиа только при явно заданном `KADR_ANIME_EPISODE_PATH`; без него
fixture пропускает этот внешний сценарий.

Дополнительно перед release проверяются:

- `git diff --check`;
- Python syntax для `workers/python` и Python-скриптов;
- JSON manifests/schemas;
- PowerShell parser для `scripts/*.ps1`;
- отсутствие запрещённых legacy identifiers/files.

Политика анализаторов: обязательный gate использует настроенный проектами
`AnalysisLevel=latest` и превращает все его предупреждения в ошибки. Режим
`latest-all` включает экспериментальные opt-in правила (включая соглашения об именах
xUnit-тестов, локализацию внутренних CLI-строк и рекомендации менять wire/persistence
DTO). Он применяется только как отдельный triage-аудит, а не как release gate. На
2026-09-01 полный `latest-all` scan давал 1 585 таких рекомендаций при нуле ошибок
компиляции; их нельзя исправлять массово или подавлять глобально без проверки API,
JSON и SQLite-совместимости.

## 7. Ограничения и риски

- Поддерживаемый первый релиз: Windows x64, SDR, mono/stereo, 23.976–60 fps, VFR
  ingest, базовый монтаж, text/subtitles/transitions и локальная обработка.
- Не входят: HDR/10-bit pipeline, 5.1, multicam, nested sequences, plugin SDK и cloud
  collaboration.
- AI production readiness зависит не только от сборки, но и от capability manifests,
  model hashes и пройденной revision montage-eval.
- Sparse vision samples не доказывают frame-exact boundary; точная граница требует
  dense probe и независимых modalities.
- `MainWindow` и `MainViewModel` остаются крупными файлами. Их можно дробить только с
  сохранением однонаправленного state flow и существующих architecture tests.
- Persistence/migration-код может выглядеть неиспользуемым в runtime, но нужен для
  совместимости старых `.kadr`; удалять его по простому reference count нельзя.

## 8. Правила для следующего разработчика или ИИ

1. Считать `ProjectState`/`EditorSession` источником истины; не добавлять второй mutable
   project model.
2. Любое изменение timeline оформлять `IEditCommand` и покрывать validation/Undo/Redo.
3. Не создавать FFmpeg, filesystem или network adapters во ViewModel.
4. Preview и export должны продолжать использовать общий render graph.
5. AI не получает editor tools, исходные paths или право менять `ProjectState`.
6. Не возвращать API v1, Ollama tool-loop, generic planner/executor или временный
   edit-review; V2 работает через готовый Agent Draft.
7. Не удалять `.kadr-ai`, `LocalData`, `artifacts`, `tools` и migration code при уборке.
8. После изменений запускать полный Release gate; документация не объявляет функцию
   готовой без теста.

Подробные документы: `ARCHITECTURE.md`, `docs/KADR_AI_EDITOR_V2.md`,
`docs/REMOTE_AI_SERVER.md`, `docs/PREVIEW_ARCHITECTURE.md`, `docs/TESTING.md` и
`docs/USER_GUIDE.md`.
