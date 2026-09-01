# Performance Audit

Audit date: 2026-09-01  
Scope: directions 1, 2, 3, 4 and 6 only  
Representative source: 120.12 s, 1920×1080 HEVC 10-bit, 23.976 fps, two AAC streams  
Machine: 16 logical CPU cores, NVIDIA GeForce RTX 5060 Ti 8 GB, FFmpeg 8.0.1

The audit followed `MEASURE → PROVE BOTTLENECK → FIX → MEASURE AGAIN`. A long Full Gate was not started because the short gate still has a confirmed 4K frame-transport limit and production rejects the Critic model as not qualified. Missing values are explicitly marked `NOT MEASURED`.

## 1. Architecture Map

### Desktop

Root: `F:\KadrStudio\KadrStudio`

```text
ProjectState / editorial UI
  -> AnalysisProxyBuilder
       -> FFmpeg visual proxy (1280x720 H.264)
       -> one mono 16 kHz FLAC chunk stream per source audio stream
  -> AiServerV2Client
       -> SHA-256 / resumable asset upload / job polling / artifact download
  -> EditorialPipeline
       -> retrieval -> Planner -> Critic -> compile -> DraftQualityReport

Interactive preview (independent from Full Analysis):
source -> FFmpeg decoder/filter graph -> Kadr.MediaHost
       -> BGRA frame queue -> named pipe -> MediaHostClient
       -> WriteableBitmap -> WPF compositor
```

The Desktop owns `ProjectState`, timeline editing, preview presentation, proxy preparation and HTTP clients. It does not load AI weights or call server internals.

### AI Server

Root: `F:\KadrStudio\KadrStudioAiServer`

```text
ASP.NET API v2
  -> content-addressed AssetStore / JobStore / ArtifactStore
  -> LoopbackGrpcWorkerGateway
       -> Python analyzer worker
            -> OpenCV video pass -> scene/motion/sparse frames/OCR candidates
            -> vision worker on selected frames
            -> audio worker(s)
       -> structured-reasoning worker
            -> role-specific llama-server process
```

The server owns workers, capability manifests, models, inference runtime and `.kadr-ai`. It has no reference to Desktop projects or `ProjectState`.

### Desktop ↔ AI Server

The boundary is HTTP API v2 only: resumable assets, jobs, status/cancel, and artifacts. Assets and job requests are content/fingerprint-addressed. No local Desktop media path is passed to the server.

## 2. Baseline

### Representative Full Analysis

| Metric | Baseline |
|---|---:|
| [DESKTOP] visual proxy FFmpeg | 7,500.85 ms |
| [DESKTOP] audio proxy stream 1 | 102.61 ms |
| [DESKTOP] audio proxy stream 2 | 105.81 ms |
| [DESKTOP] visual proxy output | 13,446,293 B |
| [DESKTOP] audio outputs | 4,202,856 B + 4,093,533 B |
| [DESKTOP] visual proxy process CPU | 82.25 CPU-s; 68.53% average of machine; 86.34% sampled peak |
| [DESKTOP] visual proxy peak RAM | 557.74 MiB |
| [AI SERVER] concurrent video + audio jobs wall time | 28,002.89 ms |
| [AI SERVER] process-tree CPU | 20.0 CPU-s; 4.46% normalized average of 16 logical CPUs |
| [AI SERVER] process-tree peak RAM | 5,449.44 MiB |
| [AI SERVER] GPU peak | 97% |
| [AI SERVER] VRAM idle / peak | 928 MiB / 7,495 MiB |
| [AI SERVER] HTTP polling requests | 57 |

Peak process-tree CPU, disk read/write throughput, and per-worker CPU peaks were `NOT MEASURED`: the sampling run retained total tree CPU and memory but did not persist every instantaneous per-process sample.

### Preview baseline

The 5-second baseline used generated six-second files and the exact requested output size. Hardware decoding was disabled. A frame was decoded into BGRA and then alpha-composited into a second managed BGRA array.

| Case | Raw rate | Frames received / expected | Frames produced | Decode FPS | Gen2 GC |
|---|---:|---:|---:|---:|---:|
| H.264 1080p60 | 474.61 MiB/s | 0 / 300 | 154 | 187.0 | 61 |
| H.264 1440p60 | 843.75 MiB/s | 0 / 300 | 86 | 92.6 | 29 |
| HEVC 4K30 | 949.22 MiB/s | 0 / 150 | 32 | 20.5 | 11 |
| HEVC 4K60 | 1,898.44 MiB/s | 0 / 300 | 32 | 20.3 | 11 |

The zero-frame result was real: when managed composition fell behind by more than one frame, the presentation loop discarded every late frame even when no newer frame was available.

## 3. Full Analysis Decode Audit

### Actual decode graph

```text
[DESKTOP] original source
  ├─ FFmpeg #1: full visual proxy encode
  ├─ FFmpeg #2..N: bounded 600 s mono FLAC chunks per audio stream
  └─ preview decoders (separate interactive concern; excluded from decode-once)

[AI SERVER] uploaded visual proxy
  └─ OpenCV VideoCapture #1: one full sequential pass
       ├─ scene / shot candidates
       ├─ motion features
       ├─ sparse frame selection
       ├─ OCR candidate frames
       └─ selected frames retained for vision and boundary refinement

[AI SERVER] audio proxy streams
  ├─ decoder/open #1: stream 1 analysis
  └─ decoder/open #2: stream 2 analysis
```

| Owner | Location | Method/stage | Decode type | Opens per primary job | Reuse |
|---|---|---|---|---:|---|
| [DESKTOP] | `AnalysisProxyBuilder.cs` | `BuildAsync`, visual proxy | full sequential FFmpeg | 1 | Persistent proxy cache added |
| [DESKTOP] | `AnalysisProxyBuilder.cs` | `BuildAsync`, audio chunks | sequential bounded FFmpeg | streams × chunks | Server analyses each FLAC independently |
| [AI SERVER] | `workers/python/kadr_worker/analyzers.py` | `analyze_video` | full sequential OpenCV | 1 | Scene, motion, sampling and selected vision frames share the pass |
| [AI SERVER] | same | vision inference/refinement | no second video open | 0 additional | Uses selected in-memory frames/compact observations |
| [AI SERVER] | same | `analyze_audio` | full audio decode per asset | 2 for two streams | No shared PCM across separate audio assets/jobs |

Measured video decoder counters:

| Counter | Value |
|---|---:|
| DecoderInstances | 1 |
| SourceOpenCount | 1 |
| FullSequentialDecodeCount | 1 |
| PartialDecodeCount | 0 |
| SeekCount | 0 |
| TotalDecodedFrames | 2,880 |
| UniqueFramesNeeded | 240 |
| TotalDecodedDuration | 120.12 s |
| DecodeFPS | 502.35 |
| DecodeTime | 5,733.06 ms |
| Source bytes | 13,446,293 B |
| BytesRead | `NOT MEASURED` — OpenCV `VideoCapture` does not expose physical bytes read |

Primary server video analysis is decode-once. A later evidence-gap request is a separate job and currently reopens and sequentially scans the complete proxy even for a small dense range; a bounded seek decoder has not yet been implemented. There is no separate quality-video pass. Embeddings are text-based in the inspected pipeline.

## 4. Full Analysis Stage Performance

Video artifact `performance` instrumentation, based on 26,597.18 ms analyzer time:

| [AI SERVER] stage | Time | % analyzer |
|---|---:|---:|
| Source open | 114.10 ms | 0.43% |
| Full video decode | 5,733.06 ms | 21.56% |
| Scene/motion/sampling | 728.19 ms | 2.74% |
| OCR | 0 ms | 0% |
| Vision inference + boundary refinement | 19,082.62 ms | 71.75% |
| Other/artifact/serialization overhead | 939.21 ms | 3.53% |
| **Total** | **26,597.18 ms** | **100%** |

Audio jobs ran concurrently with video:

| [AI SERVER] stage | Time |
|---|---:|
| Audio decode + analysis | 197.77 ms |
| Analyzer total | 954.39 ms |
| API job duration | 2,083.65 ms |
| Samples analyzed | 3,840,072 |
| Decoder opens / full passes | 2 / 2 |

[DESKTOP] instrumentation was added for proxy generation, SHA-256, upload, logical/transferred bytes, HTTP request count, cache hit, polling, artifact download, retrieval, Planner, Critic, compile and DraftQualityReport. A production Critic run is rejected by the capability gate, so a complete Planner→Critic→MontageGraph stage table is `NOT MEASURED` rather than fabricated.

## 5. Preview Hardware Decode

Before: FFmpeg software decoding, no `-hwaccel` option. Export settings were not used as evidence and the export path was not changed.

After: preview frame-server inputs request CUDA hardware decoding. If the hardware process reaches end-of-stream/fails before a complete frame, `VideoLayerWorker` restarts the same bounded decode using software FFmpeg. Diagnostics expose the requested decoder, hardware mode, device and fallback state.

An isolated equivalent 4K30 FFmpeg graph measured:

| Mode | 150 frames | Effective FPS |
|---|---:|---:|
| Software | 1,896.9 ms | 79.1 |
| D3D11VA | 1,619.2 ms | 92.6 |
| CUDA | 1,479.9 ms | 101.4 |

End-to-end remains limited by BGRA allocation/transport rather than codec decode at 4K. Hardware initialization failure is covered by runtime fallback code, but a forced-failure benchmark and exact codec/pixel-format/device-name logging are still `NOT MEASURED`.

## 6. MediaHost BGRA / IPC Path

```text
FFmpeg stdout BGRA
  -> managed source byte[] allocation                         COPY/read
  -> (before) managed opaque destination + alpha loop        ALLOC + COPY
  -> VideoFrame / bounded host queue                         ownership retained
  -> MediaHostPacket + named-pipe write                      kernel copy
  -> client payload byte[]                                   ALLOC + pipe read
  -> (before) frame.Bgra.ToArray()                            ALLOC + COPY
  -> WriteableBitmap.WritePixels                             WPF upload/copy
```

Changes remove the redundant managed compositor allocation for the common safe case of one isolated layer and remove `ToArray()` when the received `ReadOnlyMemory<byte>` is array-backed. Multi-layer/transitional frames retain correct alpha composition.

The final same-code short gate:

| Case | Frames / expected | p95 interarrival | Decode FPS | Reported drops |
|---|---:|---:|---:|---:|
| H.264 1080p60 run 1 | 300 / 300 | 18.19 ms | 160.5 | 0 |
| H.264 1080p60 run 2 | 300 / 300 | 18.47 ms | 170.2 | 0 |
| H.264 1080p60 run 3 | 300 / 300 | 18.43 ms | 152.1 | 0 |
| H.264 1440p60 | 300 / 300 | 22.29 ms | 63.1 | 0 |
| HEVC 4K30 | 88 / 150 | 87.81 ms | 16.2 | 0 |
| HEVC 4K60 run 1 | 89 / 300 | 82.45 ms | 16.3 | 0 |
| HEVC 4K60 run 2 | 86 / 300 | 85.55 ms | 15.7 | 0 |
| HEVC 4K60 run 3 | 79 / 300 | 89.00 ms | 14.5 | 0 |

At 1080p60, the final counter records 6.97 GiB copied (FFmpeg stdout read, pipe transfer, and client read) and 4.64 GiB allocated for 300 frames; Gen2 was 38 versus 61 before while successfully delivering all frames. At 4K60, the final run copied 7.38 GiB and allocated 4.91 GiB while delivering 79 frames. This proves raw BGRA plus per-frame large-object allocation/IPC is still a high-impact data-plane bottleneck. Named pipes remain appropriate for control; a reusable shared-memory/ring-buffer frame plane is the next justified step. Shared GPU textures were not introduced.

Preview CPU, Desktop/MediaHost split CPU, RAM/VRAM, seek p95, consumer wait and WPF presentation latency are `NOT MEASURED` in the final run. The current instrumentation measures decoder read, producer wait, pipe read/write, queue, copies, allocations and GC, but does not yet sample those external process/UI values.

## 7. Planner / Critic VRAM

Planner and Critic resolve to the same 18,556,685,824-byte Qwen GGUF but use role-isolated worker/llama-server processes and contexts. `LoopbackGrpcWorkerGateway` serializes accelerator work and evicts the currently active accelerator worker before starting another role. Static lifecycle inspection therefore finds no simultaneous duplicate weight residency.

| Component | Idle VRAM | Active/retained | Peak | Status |
|---|---:|---:|---:|---|
| Vision Full Analysis | 928 MiB system idle | `NOT MEASURED` retained | 7,495 MiB total | measured as production job total |
| Planner/Director | 917 MiB before | 5,926 MiB after 2 s | 5,926 MiB | cold request 33,597.63 ms |
| Critic | 5,926 MiB before request | `NOT MEASURED` | `NOT MEASURED` | HTTP 503 `model_not_qualified` |
| AnimeSR | `NOT MEASURED` | `NOT MEASURED` | `NOT MEASURED` | not active in this gate |
| Preview CUDA | `NOT MEASURED` | `NOT MEASURED` | `NOT MEASURED` | separate Desktop process |

Planner process-tree peak RAM was 14,980.47 MiB and GPU utilization peak was 10%. Production gating only qualifies Director for `anime-episode`; bypassing the manifest to manufacture a Critic VRAM number would invalidate the test. A shared model-host refactor is not justified until Critic is qualified and role-switch/reload measurements can be completed. Role/context separation remains intact.

## 8. Desktop ↔ Server Transfer Performance

The representative logical proxy set is 21,742,682 bytes. Server storage and job creation are content/fingerprint-addressed and warm submissions were deduplicated. The Desktop still had to rebuild identical proxies before it could reach server-side deduplication; this was fixed with the persistent Desktop analysis-proxy cache.

| Metric | Result |
|---|---:|
| Logical representative asset bytes | 21,742,682 B |
| Duplicate server asset copies | 0 observed; content-addressed store |
| Poll requests for concurrent video/audio run | 57 |
| Cold upload time / bytes transferred | `NOT MEASURED` — initial generic job was rejected by the model gate before the sampler persisted its summary |
| Warm upload | Server asset cache hit observed; exact request time `NOT MEASURED` |
| Artifact download time/bytes | `NOT MEASURED` in the manual server run |
| Retries/reconnect overhead | `NOT MEASURED`; client has no general retry/backoff policy |

Client instrumentation now records these values on normal Desktop runs. API v2, resumable uploads, cancellation and binary artifacts were not changed.

## 9. Confirmed Bottlenecks

### CRITICAL — [DESKTOP] late-frame policy plus redundant single-layer composition

- Evidence: 0/300 delivered at 1080p60 while 154 frames were decoded; 2.39 GiB allocated in the short run and 61 Gen2 collections.
- Root cause: per-channel alpha loop into a second full frame made the producer late; the presenter discarded a late frame even with no replacement.
- Location: `Kadr.MediaHost/VideoWorkerSupervisor.cs`, `HostPlaybackSession.cs`.
- Measured impact: after fix 300/300 in two repeats, p95 18.19–18.47 ms.
- Fix: opaque one-layer pass-through; drop only late frames that already have a newer replacement.

### HIGH — [DESKTOP] raw BGRA data plane and large-object churn

- Evidence: theoretical 1.898 GiB/s at 4K60; final 79–89/300 frames, 14.5–16.3 decode/read FPS, 7.38 GiB copied and 4.91 GiB allocated in the final counter run.
- Root cause: new full-frame arrays on both sides of the named pipe and WPF upload; no reusable frame ownership protocol.
- Location: `MediaHostPacketIO`, `MediaHostServer`, `MediaHostClient`, `PreviewPresenter`.
- Recommended fix: keep named pipe as control plane; benchmark a bounded reusable memory-mapped ring buffer for BGRA/NV12 frame payloads.

### HIGH — [DESKTOP] repeated analysis proxy generation

- Evidence: identical 120 s proxy cost 6.231–7.709 s and visual transcode consumed 82.25 CPU-s.
- Root cause: disposable GUID temp directory built before server content-hash lookup.
- Location: `AnalysisProxyBuilder.cs`.
- Measured impact: persistent-cache warm open 0.492 ms, 12,654× faster than its 6.231 s cold run.
- Fix: versioned source/fingerprint/stream-keyed persistent cache with a completion marker.

### HIGH — [AI SERVER] vision inference dominates primary analysis

- Evidence: 19,082.62 ms, 71.75% of analyzer time.
- Root cause: selected-frame VLM inference and boundary refinement, not repeated video decode.
- Recommended fix: separately benchmark batching/model quantization/cache reuse; no quality-changing optimization was made without evidence.

### MEDIUM — [AI SERVER] small evidence gaps reopen the full proxy

- Evidence: code path creates a new `VideoCapture` and full sequential loop for a separate dense-gap job.
- Root cause: gap ranges guide sampling but do not bound decoding.
- Recommended fix: seek to merged bounded ranges, include keyframe preroll, and persist partial-decode counters; validate boundary equivalence.

### MEDIUM — [AI SERVER] Planner role switch cold-start cost

- Evidence: cold Director request 33.598 s, 5,926 MiB retained VRAM, 14,980 MiB peak RAM.
- Root cause: role-specific llama-server lifecycle; Critic cannot currently be measured under the capability manifest.
- Recommended fix: qualify Critic first, then measure eviction/reload versus a shared-weight model host with isolated contexts.

### LOW — [DESKTOP ↔ AI SERVER] fixed 500 ms polling and no general retry/backoff

- Evidence: 57 poll requests in the short concurrent analysis.
- Recommended fix: bounded exponential backoff or server events only after measuring real remote latency/load.

## 10. Changes Made

- [AI SERVER] Added decoder/open/full/partial/seek/frame/duration/FPS/bytes-reason and per-stage instrumentation to Python video/audio analyzer artifacts.
- [DESKTOP] Added proxy, hash/upload, polling, artifact, retrieval, Planner, Critic, compile and quality-report timing/byte/request instrumentation.
- [DESKTOP] Added versioned persistent analysis-proxy cache; incomplete entries are rejected and disposable temp semantics remain safe.
- [DESKTOP] Added CUDA preview decode request with bounded software fallback; export path unchanged.
- [DESKTOP] Added single-isolated-layer opaque pass-through, avoiding a second managed full-frame allocation/alpha pass.
- [DESKTOP] Corrected late-frame scheduling and frame-drop counter aggregation.
- [DESKTOP] Removed the normal `ReadOnlyMemory<byte>.ToArray()` copy before `WriteableBitmap`.
- [DESKTOP] Added opt-in reproducible preview and proxy performance integration benchmarks.

## 11. BEFORE vs AFTER

| Metric | Before | After | Change |
|---|---:|---:|---:|
| [DESKTOP] repeated 120 s analysis proxy | 6,230.73 ms cold | 0.492 ms warm | -99.992% wall time |
| [AI SERVER] primary full sequential video decodes | 1 | 1 | unchanged; already decode-once |
| [AI SERVER] decoded frames | 2,880 | 2,880 | unchanged |
| Preview 1080p60 delivered | 0/300 | 300/300 (3/3 repeats) | black/stalled → real-time |
| Preview 1080p60 p95 | unavailable | 18.19–18.47 ms | now measurable |
| Preview 1080p60 Gen2 | 61 | 35–38 | -38% to -43% while delivering 300× more frames |
| Preview 1440p60 delivered | 0/300 | 300/300 | black/stalled → real-time |
| Preview 4K30 delivered | 0/150 | 88/150 | usable degraded output; not real-time |
| Preview 4K60 delivered | 0/300 | 79–89/300 | usable degraded output; not real-time |
| Full Analysis total after optimization | 28,002.89 ms | `NOT MEASURED` | server inference path was instrumented, not changed |
| Full Analysis CPU/RAM/GPU/VRAM after | `NOT MEASURED` | `NOT MEASURED` | no second production inference run |
| Seek p95 | `NOT MEASURED` | `NOT MEASURED` | functional seek tests pass; performance sampler absent |
| Planner VRAM | 5,926 MiB | unchanged | no unjustified lifecycle rewrite |
| Critic/combined VRAM | `NOT MEASURED` | `NOT MEASURED` | production qualification gate |
| Desktop→Server cold transfer | `NOT MEASURED` | `NOT MEASURED` | instrumentation added for the next normal run |

## 12. Tests

| Suite/check | Result |
|---|---:|
| `dotnet build KadrStudio.sln -c Debug --no-restore` | PASS, 0 warnings |
| Desktop core tests | PASS 120/120 |
| Desktop UI adapter tests | PASS 38/38 |
| Desktop integration tests, including MediaHost pipes | PASS 32/32 |
| `dotnet test KadrStudioAiServer.sln -c Debug --no-build` | PASS 43/43 |
| Python analyzer syntax | PASS |
| Analysis proxy cache benchmark | PASS; 6,230.73 ms cold / 0.492 ms warm |
| Preview benchmark | PASS as harness; performance status is PARTIAL at 4K |
| `git diff --check` | PASS; line-ending conversion warnings only |

The initial sandboxed MediaHost run failed with `UnauthorizedAccessException` on named pipes. The same suite passed outside that sandbox restriction; this is an environment limitation, not a product failure.

## 13. Remaining Problems

1. [DESKTOP] Full-resolution 4K raw BGRA is not real-time across the current allocation/pipe/WPF data plane.
2. [DESKTOP] Client-side payload allocation remains one large object per delivered frame; frame ownership/reuse is not modeled by the protocol.
3. [DESKTOP] Exact seek/scrub latency, UI presentation latency, process CPU/RAM and preview VRAM need persistent benchmark sampling.
4. [AI SERVER] Dense evidence-gap jobs perform a new full sequential proxy scan instead of bounded partial decodes.
5. [AI SERVER] VLM inference consumes 71.75% of analyzer time and needs its own quality-preserving batching/cache experiment.
6. [AI SERVER] Critic is not production-qualified, preventing real Critic/combined VRAM and full Planner→Critic stage measurement.
7. [DESKTOP ↔ AI SERVER] Cold upload/download/retry timing was not captured in a normal Desktop-driven run.
8. [AI SERVER] A long episode Full Gate was intentionally deferred until the short 4K and Critic gates are resolved.

## 14. Next Recommended Optimizations

1. Prototype a three-slot memory-mapped reusable frame ring while retaining the named pipe for commands/metadata. Compare 1080p60 and 4K30 against the current final JSON before adopting it.
2. Consider NV12/P010 as the reusable data-plane format only if WPF/GPU conversion can be measured end-to-end; do not add CPU conversion blindly.
3. Add a benchmark sampler for Desktop, MediaHost, FFmpeg child CPU/RAM, NVML VRAM/GPU, seek p50/p95 and presentation latency.
4. Implement merged dense-gap ranges with bounded seek/preroll in the Python analyzer and assert observation/boundary equivalence.
5. Add a VLM batch-size/cache matrix on the representative source and require unchanged analysis-quality fixtures.
6. Qualify the Critic manifest, then measure role-switch reload and simultaneous pressure before considering shared model weights.
7. Run a Desktop-driven cold/warm HTTP benchmark and persist upload/download/retry/cache metrics already emitted by `AiServerV2Client`.
8. Only after the short gate passes, run the long Full Analysis with per-stage durable results.

## Final Status

| Gate | Status | Reason |
|---|---|---|
| Full Analysis decode-once | **PARTIAL** | Primary job PASS (one full decode); dense evidence-gap job still rescans fully |
| Full Analysis benchmark | **PARTIAL** | Representative server stages measured; complete Critic/MontageGraph and several system metrics unavailable |
| Preview hardware decode | **PARTIAL** | CUDA + software fallback implemented; 1080p60/1440p60 pass, 4K not real-time |
| MediaHost frame transport | **PARTIAL** | Critical correctness/copy issues fixed; raw 4K BGRA data plane remains bounded |
| Planner/Critic model-weight reuse | **PARTIAL** | Serialized eviction prevents observed simultaneous weights, but Critic production measurement is blocked |
| Desktop ↔ AI Server transport | **PARTIAL** | Boundary/dedup correct and client instrumentation added; cold transfer metrics missing |
| Release verification | **PARTIAL** | All short builds/tests pass; long Full Gate intentionally not run |
