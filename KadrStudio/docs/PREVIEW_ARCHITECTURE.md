# Архитектура предпросмотра

Preview и export компилируются из одного immutable `RenderGraph`, но не делят изменяемое состояние проигрывателя.

## Поток данных

1. `EditorSession` публикует `ProjectState` и `ProjectChangeSet`.
2. `RenderGraphCompiler` создаёт план и четыре независимые подписи: source decode, video, audio, overlay.
3. `PreviewPresenter` повышает только изменившиеся поколения.
4. `MediaHostClient` передаёт план в постоянный процесс `Kadr.MediaHost` по named pipe.
5. Host создаёт независимые последовательные decoder-workers активных V/A-клипов.
6. Видео композируется в BGRA и попадает в bounded queue; WPF копирует кадр в `WriteableBitmap`.
7. Аудио микшируется в stereo float32 48 kHz, выводится WASAPI и задаёт главный playback clock. Без A-дорожек используется монотонный clock.
8. Все активные text layers рисуются WPF поверх кадра; export запекает тот же overlay через FFmpeg.

## Гарантии

- Изменение gain/pan/EQ не меняет video generation; transform/color не меняют audio; текст не перезапускает media decoder.
- Старое/отменённое поколение никогда не публикуется в UI.
- Опоздавший кадр пропускается без сдвига аудио.
- Seek/paused preview возвращает кадр с допуском не более одного frame.
- Ошибка одного layer-worker превращает только этот слой в black/silence и не завершает второй pipeline.
- Последний правильный кадр сохраняется при buffering/failure. Чёрный экран допустим для пустого участка или чёрного исходного кадра.
- V-only не запускает аудио, A-only не запускает видео, активные A смешиваются, верхняя V перекрывает нижние.

## Proxy

Фоновый proxy — H.264 video-only 960×540 CFR, GOP 12. До его готовности используется оригинал; режим «Оригинал» обходит proxy; export всегда использует оригинал. Proxy находится в едином `IArtifactStore`, проверяется checksum и FFprobe и перестраивается после повреждения. Папка и LRU-лимит задаются пользователем.

Реальные проверки находятся в `MediaHostIntegrationTests`, `AudioWorkerSupervisorIntegrationTests` и `MediaPipelineIntegrationTests`.
