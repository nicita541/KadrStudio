# Preview architecture

The editor preview is split into independent layers. Keep these boundaries when adding features:

1. `EditorProject` owns the timeline and separate `VideoRevision` / `AudioRevision` counters.
2. `PreviewCompositionService` is a pure FFmpeg render engine. It creates video-only composited segments from V tracks, audio-only mixed segments from A tracks, and exact composited still frames.
3. `TimelinePreviewSession` owns cache generations, jobs, cancellation, and prefetch. Video invalidation never clears audio state and audio invalidation never clears video state.
4. `PreviewPlaybackController` owns the four WPF decoders. Video and audio each have an active and standby decoder. A standby source is presented only after `MediaOpened`.
5. `MainWindow` supplies the project, playhead, quality, and Play/Pause state. It does not own FFmpeg graphs, cache keys, or decoder swapping.

Hard invariants:

- V tracks produce video only (`-an`).
- A tracks produce audio only (`-vn`).
- Track index defines the video layer order; higher V tracks are overlaid later.
- All active A tracks are mixed; there is no “single active audio clip” shortcut.
- Cache signatures include only properties that affect that pipeline.
- A stale render generation cannot replace a current source.
- Decoder failure invalidates and restarts only the failed pipeline.
- Text remains an independent WPF overlay during preview and an FFmpeg overlay during export.

Regression command:

```powershell
dotnet run --project tests\KadrStudio.AnalysisSmoke\KadrStudio.AnalysisSmoke.csproj -c Release -- --preview-composition-smoke
```
