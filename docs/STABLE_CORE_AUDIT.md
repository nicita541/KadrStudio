# Итоговый аудит стабильного ядра

Ledger закрывается только вместе с зелёным release-gate.

## Закрытые системные дефекты

- [x] `ProjectState v3` — единственное live-состояние; старая `EditorProject` и JSON undo bridge удалены.
- [x] Fractional FPS, VFR metadata, track ID/order/name/mute/lock/visibility сохраняются без потерь.
- [x] Все clip/text/marker/In-Out/track/transition изменения выполняются командами и поддерживают undo/redo.
- [x] Preview/export используют один `RenderGraph`; V/A/Overlay подписи и поколения независимы.
- [x] `Kadr.MediaHost` отделён от WPF и держит независимые video/audio worker-пайплайны, bounded queues и generation filtering.
- [x] BGRA показывается через `WriteableBitmap`; LibVLC/HwndHost и 15-секундные segment-preview удалены.
- [x] Audio meter вычисляется из реального итогового stereo PCM; V-only/A-only/AV покрыты integration-тестами.
- [x] Proxy, waveform, thumbnails и analysis artifacts используют единый checksum/LRU store; повреждение вызывает rebuild.
- [x] Waveform хранит stereo min/max/RMS-пирамиду без искусственного уровня тишины и масштабируется по viewport.
- [x] Timeline использует общий viewport для ruler/clips/thumbnails/waveform/markers/playhead и виртуализирует невидимые строки.
- [x] Thumbnail-плитки запрашиваются on demand для видимого диапазона; фиксированная редкая сетка и лимит кадров на длинный файл удалены.
- [x] Несколько text overlay одновременно видны; multiline edit, drag и resize выполняются через draft + command.
- [x] Offline/relink проверяет stream compatibility и content fingerprint.
- [x] Пять видеопереходов и Constant Power Audio являются полноценными сущностями preview/export.
- [x] Анализ выполняет coarse scan → candidates → fine boundary → frame verification; Ollama не заменяет визуальную границу.
- [x] Автосубтитры используют локальный whisper.cpp и до запуска проверяют binary/model.
- [x] Recovery хранит 20 версий каждого проекта, история встроена в SQLite, параллельный writer блокируется.
- [x] Export проверяет streams/duration и автоматически повторяет CPU-кодеком после NVENC failure.
- [x] Recording и скрытый inspector удалены; composition root отделяет ViewModel от Process/File adapters.
- [x] Release script выполняет restore, warnings-as-errors build, unit/integration/UI tests, publish, launch smoke и ZIP.

## Проверяемые ограничения первой стабильной версии

Поддерживаются Windows x64, SDR, mono/stereo, 23.976–60 fps, VFR ingest, базовый монтаж/текст/переходы и локальная обработка. HDR/10-bit pipeline, 5.1, multicam, nested sequences, plugin SDK и облачная совместная работа намеренно не входят в этот релиз.

Последний фактический прогон и команды фиксируются в [TESTING.md](TESTING.md); документация не должна объявлять готовой функцию без соответствующего теста.
