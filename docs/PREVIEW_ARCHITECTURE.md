# Preview architecture

The editor preview is split into independent layers. Keep these boundaries when adding features:

1. `ProjectState` is the validated source of truth; `EditorProjectMapper` is the compatibility boundary for current WPF controls.
2. `RenderPlanBuilder` creates one immutable composition plan used by both preview and export.
3. `TimelineRenderCoordinator` owns the FFmpeg render engine and typed scheduler lanes. `PreviewCompositionService` is only a WPF adapter over that coordinator.
4. `TimelinePreviewSession` owns cache generations, jobs, cancellation, and prefetch. Video invalidation never clears audio state and audio invalidation never clears video state.
5. `PreviewPlaybackController` owns two independent LibVLC players: one video-only presentation player and one audio-only player. Replacing either source cannot reset the other.
6. `MainWindow` supplies the project, playhead, quality, and Play/Pause state. It does not construct FFmpeg graphs or own cache keys.

Hard invariants:

- V tracks produce video only (`-an`).
- A tracks produce audio only (`-vn`).
- Track index defines the video layer order; higher V tracks are overlaid later.
- All active A tracks are mixed; there is no “single active audio clip” shortcut.
- Cache signatures include only properties that affect that pipeline.
- A stale render generation cannot replace a current source.
- Decoder failure invalidates and restarts only the failed pipeline.
- Text is a render-plan layer. It remains an interactive WPF overlay during editing and uses the same plan data for FFmpeg export.

Regression command:

```powershell
dotnet run --project tests\KadrStudio.AnalysisSmoke\KadrStudio.AnalysisSmoke.csproj -c Release -- --preview-composition-smoke
```
