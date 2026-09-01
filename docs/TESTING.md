# Проверка Kadr Studio

## Обязательный быстрый gate

```powershell
dotnet restore KadrStudio.sln --disable-parallel -m:1 -nr:false
dotnet build KadrStudio.sln -c Release --no-restore -m:1 -nr:false -warnaserror
dotnet test KadrStudio.sln -c Release --no-build --no-restore -m:1 -nr:false
```

`-warnaserror` проверяет уровень анализаторов, заданный проектами
(`AnalysisLevel=latest`). `latest-all` является отдельным opt-in triage-режимом и не
заменяет обязательный gate: часть его правил предлагает менять JSON/persistence DTO,
публичность API-контрактов и соглашения об именах тестов.

Набор включает:

- domain/property tests точного времени, edit-команд, undo/redo, link groups, transitions и range invalidation;
- architecture tests направления зависимостей, отсутствия старого mutable project/JSON bridge и Process/File construction во ViewModel;
- SQLite schema v1–v9, миграция старого таймлайна в исходную последовательность, upscale/rendition persistence, checksum corruption, history, 20 recovery-версий и write lease;
- ИИ-монтаж: независимые последовательности и Undo/Redo, Required/Excluded/locked-инварианты, stale fingerprint/revision, source-range scope, связанный V/A rough cut, субтитры и статический 9:16 reframe;
- cache fingerprint/checksum/LRU/move/budget и stereo waveform pyramid;
- UI geometry/render snapshots общего viewport, thumbnail virtualization/zoom precision и DPI-dependent waveform density;
- Razor без предварительного выделения, linked/Alt-unlink, Ripple Delete, pixel snapping, отсутствие отскока, toolbar icons и 10 000 точных frame steps на 23.976/29.97;
- AI Editor V2: hierarchical index, typed evidence gaps, MontageGraph validation, native Draft compiler, exact-complement QC, запрет повтора rejected graph без новых evidence и timeline integrity;
- реальные FFmpeg V-only/A-only/AV, multitrack, transitions, proxy corruption, точные on-demand thumbnail, fractional FPS, subtitles и analysis;
- MediaHost crash/restart, generation filtering, exact seek, bounded workers и orphan-process checks;
- CPU export и принудительный NVENC failure с автоматическим CPU fallback.

Нагрузочный unit-gate использует четырёхчасовой проект, 18 дорожек и 10 000 медиаклипов. Seek-stress выполняет повторные frame-accurate запросы в одном host и проверяет bounded workers.

## Реальный smoke AI-агента

После запуска локального AI Server можно проверить не только `/health`, но и реальные
контракты понимания задачи и выбора инструмента планировщиком. Тест выполняет три
последовательных inference-вызова: понимание запроса, первый выбор исследования и
повторный выбор после фактического `inspect_timeline`. Тем самым он воспроизводит
прежний сбой второго хода с переполнением контекста и оборванным JSON. На каждом
исследовательском ходе модели передаются только read-only schemas; editing schemas
добавляются отдельным компактным вызовом лишь при публикации плана:

```powershell
$env:KADR_ANIME_EPISODE_PATH = 'F:\KadrStudio\Tsue_to_Tsurugi_no_Wistoria_[02]_[AniLibria]_[WEBRip_1080p_HEVC].mkv'
dotnet test tests\KadrStudio.Integration.Tests\KadrStudio.Integration.Tests.csproj `
  -c Release --no-build -m:1 -nr:false `
  --filter 'FullyQualifiedName~AnimeEpisodeLiveIntegrationTests'
```

Без `KADR_ANIME_EPISODE_PATH` live fixture безопасно пропускает внешний файл. Тест читает MKV,
проверяет verified SHA-256 и собирает Agent Draft in-memory; source не открывается на запись.

Обычный synthetic fixture покрывает путь `Cold open → OP → Episode body → ED → Post-credit → Preview`:
индекс → graph с двумя Remove → native Draft compiler → exact-complement QC → ReviewingDraft.
Он подтверждает, что source не меняется, post-credit/preview сохраняются, а video, обе audio
и обе ASS mappings сдвигаются синхронно.

## Release одним сценарием

```powershell
.\scripts\build-release.ps1 -SkipSdkInstall
```

После тестов сценарий делает self-contained win-x64 publish, проверяет FFmpeg/FFprobe/MediaHost, запускает опубликованный `KadrStudio.exe --launch-smoke` и только затем создаёт ZIP.

Долгий ручной soak перед публичным релизом: 30 минут playback, исчезновение/relink исходника, убийство MediaHost, 1000 seek и 500 edit/undo/redo с наблюдением process/handle/memory. Эти проверки не заменяют автоматические stress-тесты, а подтверждают поведение драйвера WASAPI и конкретного GPU.
