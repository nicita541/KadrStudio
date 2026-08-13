# Kadr Studio architecture

Kadr Studio is split into inward-pointing layers. New features must preserve this dependency direction:

```text
Kadr (WPF adapters and views)
  -> Kadr.Infrastructure (SQLite, FFmpeg, cache, scheduler)
  -> Kadr.Application (transactions, jobs, render plans, automation proposals)
  -> Kadr.Core (immutable project state, exact time, validation)
```

`Kadr.Core` has no WPF, file-system, network, process, database, FFmpeg or AI dependency. `Kadr.Application` depends only on the core. Infrastructure implements application contracts. WPF maps UI models at the outer boundary and never becomes the source of truth for persisted or rendered state.

## Invariants

- Timeline time uses integer `TimelineTime` ticks at 240,000 ticks per second. Floating-point seconds exist only in WPF compatibility adapters.
- Every edit is applied through an `EditTransaction`; validation happens before commit. Undo and redo restore whole immutable revisions.
- `.kadr` files are normalized SQLite documents written through a verified temporary file. Tracks, clips, text and markers preserve their explicit project order. Schema 2 still reads schema 1 projects.
- Recovery and project history are separate SQLite operations. UI code awaits storage and never blocks the dispatcher on `.Result` or `GetAwaiter().GetResult()`.
- Preview and export consume the same immutable `RenderPlan`. Audio and video have independent playback state, while their composition rules come from the same plan.
- Background work runs through typed lanes. Identical cache/decode work is deduplicated, exports can pause background decoding, and cancellation is reference-counted.
- Thumbnails and waveforms are derived cache artifacts, never project state. A cache key includes the source fingerprint, artifact kind and pyramid level.
- AI analysis and subtitle generation work on isolated snapshots and return proposals. A proposal is rejected if the project revision changed while automation was running.
- Window shutdown first cancels work, then asynchronously disposes the AI and render schedulers, and closes only after they stop.

## Rules for new code

1. Add domain rules and exact types to `Kadr.Core`; do not reference WPF models there.
2. Add a command, query or proposal contract to `Kadr.Application` before adding an external implementation.
3. Put SQLite, FFmpeg, WASAPI, local-model, disk-cache and process code in infrastructure or a WPF edge adapter.
4. Do not make one feature mutate another feature's cache or playback objects. Communicate with immutable state, plans, proposals and scheduler jobs.
5. Add a regression test for every fixed failure. Large-project tests must continue to cover a four-hour, 18-track project with thousands of clips.
6. Keep `dotnet build KadrStudio.sln -c Release -warnaserror` and `dotnet test KadrStudio.sln -c Release -m:1` green.

`ArchitectureBoundaryTests` enforce the project dependency direction so accidental outer-layer references fail the test suite immediately.
