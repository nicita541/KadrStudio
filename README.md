# Kadr Studio

Kadr Studio — нативный локальный видеоредактор для Windows на C# и WPF. В проекте нет Electron, HTML и облачной обработки; исходные медиа остаются на компьютере пользователя.

## Ядро

- неизменяемый `ProjectState`, точное целочисленное время и команды `EditorSession` с undo/redo;
- единый `RenderPlan` для интерактивного просмотра и экспорта;
- независимые подписи и поколения Video, Audio и Overlay;
- постоянный FFmpeg frame server: BGRA-кадры в WPF `WriteableBitmap`, stereo PCM 48 kHz через WASAPI;
- video-only прокси 960×540 CFR с коротким GOP, проверкой повреждений и общим настраиваемым LRU-кэшем;
- многодорожечная композиция: верхние V перекрывают нижние, активные A микшируются;
- общий `TimelineViewport` для линейки, клипов, кадров, waveform, маркеров, playhead и скролла;
- многоуровневая stereo min/max/RMS-пирамида waveform с детализацией по масштабу;
- локальный FFmpeg/FFprobe, локальный Ollama-анализ, SQLite-проекты и контрольные точки истории;
- MP4-экспорт H.264/AAC в 480p, 720p и 1080p с NVENC/fallback на CPU.

## Сборка Windows x64

Требуется .NET 10 SDK. Запустите `build-release.bat`. Сценарий одной командой выполняет restore, Release build с warnings-as-errors, unit/integration/UI tests, self-contained publish, launch smoke готового EXE и создаёт:

- `release\KadrStudio-win-x64\KadrStudio.exe`
- `release\KadrStudio-win-x64.zip`

Для разработки:

```powershell
dotnet restore KadrStudio.sln --disable-parallel
dotnet build KadrStudio.sln -c Debug --no-restore -m:1 -warnaserror
dotnet test KadrStudio.sln -c Debug --no-build --no-restore -m:1
```

Архитектура описана в [ARCHITECTURE.md](ARCHITECTURE.md), предпросмотр — в [docs/PREVIEW_ARCHITECTURE.md](docs/PREVIEW_ARCHITECTURE.md), тестовый gate — в [docs/TESTING.md](docs/TESTING.md), работа в редакторе — в [docs/USER_GUIDE.md](docs/USER_GUIDE.md).

## Структура

- `src/Kadr.Core` — точное время, неизменяемое состояние и инварианты;
- `src/Kadr.Application` — команды, транзакции, render plan, preview-контракты;
- `src/Kadr.Infrastructure` — SQLite, FFmpeg-композиция, кэш и планировщик;
- `src/Kadr` — WPF-представления и внешние адаптеры;
- `tests` — unit, архитектурные и реальные FFmpeg integration-тесты;
- `tools/win-x64` — локальные FFmpeg и FFprobe;
- `scripts` — проверка, publish и упаковка.
