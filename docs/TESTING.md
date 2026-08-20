# Проверка Kadr Studio

## Обязательный быстрый gate

```powershell
dotnet restore KadrStudio.sln --disable-parallel -m:1 -nr:false
dotnet build KadrStudio.sln -c Release --no-restore -m:1 -nr:false -warnaserror
dotnet test KadrStudio.sln -c Release --no-build --no-restore -m:1 -nr:false
```

Набор включает:

- domain/property tests точного времени, edit-команд, undo/redo, link groups, transitions и range invalidation;
- architecture tests направления зависимостей, отсутствия старого mutable project/JSON bridge и Process/File construction во ViewModel;
- SQLite schema 1/2/3/4, миграция старого таймлайна в исходную последовательность, checksum corruption, history, 20 recovery-версий и write lease;
- ИИ-монтаж: независимые последовательности и Undo/Redo, Required/Excluded/locked-инварианты, stale fingerprint/revision, source-range scope, связанный V/A rough cut, субтитры и статический 9:16 reframe;
- cache fingerprint/checksum/LRU/move/budget и stereo waveform pyramid;
- UI geometry/render snapshots общего viewport, thumbnail virtualization/zoom precision и DPI-dependent waveform density;
- реальные FFmpeg V-only/A-only/AV, multitrack, transitions, proxy corruption, точные on-demand thumbnail, fractional FPS, subtitles и analysis;
- MediaHost crash/restart, generation filtering, exact seek, bounded workers и orphan-process checks;
- CPU export и принудительный NVENC failure с автоматическим CPU fallback.

Нагрузочный unit-gate использует четырёхчасовой проект, 18 дорожек и 10 000 медиаклипов. Seek-stress выполняет повторные frame-accurate запросы в одном host и проверяет bounded workers.

## Release одним сценарием

```powershell
.\scripts\build-release.ps1 -SkipSdkInstall
```

После тестов сценарий делает self-contained win-x64 publish, проверяет FFmpeg/FFprobe/MediaHost, запускает опубликованный `KadrStudio.exe --launch-smoke` и только затем создаёт ZIP.

Долгий ручной soak перед публичным релизом: 30 минут playback, исчезновение/relink исходника, убийство MediaHost, 1000 seek и 500 edit/undo/redo с наблюдением process/handle/memory. Эти проверки не заменяют автоматические stress-тесты, а подтверждают поведение драйвера WASAPI и конкретного GPU.
