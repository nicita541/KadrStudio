# KadrStudio P0/P1 fix report

Date: 2026-09-01  
Desktop: `F:\KadrStudio\KadrStudio`  
AI Server: `F:\KadrStudio\KadrStudioAiServer`

## 1. Cache safety

**STATUS:** PASS  
**FILES:** `MediaCacheContracts.cs`, `DiskMediaArtifactCache.cs`, `WorkspaceSettingsService.cs`, `EditorWorkspaceCompositionRoot.cs`, `MainWindow.xaml.cs`, cache/settings tests.  
**ROOT CAUSE:** выбранная пользователем папка могла стать удаляемым cache root без доказательства владения.  
**FIX:** выбранный каталог теперь только parent для `KadrStudioCache`; добавлен валидируемый `.kadr-cache-owner.json`, canonical/protected-path guards, отказ без marker, безопасный move без overwrite чужих данных и сохранение ownership ID.  
**TESTS:** foreign files, non-empty target, missing/forged marker, drive root, protected project/source, restart after move.  
**RESULT:** PASS.

## 2. Cancellation and terminal state

**STATUS:** PASS  
**FILES:** `AnalyzerJobService.cs`, `LoopbackGrpcWorkerGateway.cs`, `server.py`, `analyzers.py`, cancellation/state tests.  
**ROOT CAUSE:** cancellation не доходила до Python loops/subprocess, а stale completion мог перезаписать terminal state.  
**FIX:** C#→gRPC→Python cancellation chain, terminate/kill subprocess, cancellation checks в long loops; per-job synchronization/version и явные разрешённые переходы state machine.  
**TESTS:** queued/running/completion-race/late-completion/repeated/shutdown cancellation; реальный 30-second subprocess завершается примерно за 0.2 s.  
**RESULT:** PASS.

## 3. Worker/GPU lifecycle

**STATUS:** PASS  
**FILES:** `LoopbackGrpcWorkerGateway.cs`, `AiServerOptions.cs`, gateway/options tests.  
**ROOT CAUSE:** concurrent cold start мог создать два процесса; CountTokens обходил accelerator lifecycle; CPU embedding ошибочно классифицировался как accelerator workload.  
**FIX:** per-analyzer startup lock + double check, CountTokens под тем же accelerator lease/eviction, embedding удалён из accelerator set; crash/restart сохранён.  
**TESTS:** concurrent cold start, CountTokens during Vision, embedding during Vision, worker crash/restart.  
**RESULT:** PASS.

## 4. Remote auth/TLS

**STATUS:** PASS  
**FILES:** `KadrApiAuthorizationMiddleware.cs`, `AiServerOptions.cs`, `Program.cs`, `run-ai-server.ps1`, auth/options/API tests.  
**ROOT CAUSE:** loopback peer ошибочно считался доверенным и в remote mode за reverse proxy; remote HTTP не запрещался fail-fast.  
**FIX:** explicit Local/Remote mode; Remote всегда требует Bearer; Remote+HTTP отказывается стартовать без `KADR_AI_ALLOW_INSECURE_REMOTE_HTTP=1`.  
**TESTS:** local, remote direct/proxy, missing Bearer=401, remote HTTP startup failure.  
**RESULT:** PASS.

## 5. Model integrity and model-aware dedup

**STATUS:** PASS  
**FILES:** `ModelCapabilityGate.cs`, `AnalyzerJobService.cs`, `AiServerOptions.cs`, install/activate scripts, model/job tests.  
**ROOT CAUSE:** capability manifest не был связан с runtime payload; AnimeSR хешировался на каждом request; другие model-backed analyzers не включали модель в dedup fingerprint.  
**FIX:** verified identity (`path/size/mtime/sha256/revision`), SHA recompute только при metadata change и cached result; mismatch инвалидирует production capability. Fingerprint включает model hash/revision/config, analyzer implementation и pipeline version для ASR, embedding, diarization, vision, director, critic и upscale.  
**TESTS:** payload mutation rejects capability; metadata change hashes once; model A→B с тем же request создаёт новый job/worker execution.  
**RESULT:** PASS.

## 6. Bounded AI Server resources

**STATUS:** PASS  
**FILES:** `AiServerOptions.cs`, `AnalyzerJobService.cs`, `DataRootQuota.cs`, asset/artifact stores, API endpoints, resource-limit tests.  
**ROOT CAUSE:** unbounded channel/history/storage и отсутствие cleanup для partial uploads.  
**FIX:** bounded queue/active workers; `MaxQueuedJobs`, `MaxActiveJobs`, `JobRetention`, `MaxJobHistory`, `PartialUploadTtl`, `MaxDataRootBytes`; queue full=429; shared data-root quota; terminal history/abandoned upload cleanup; `/v2/metrics`. Stale content-addressed assets/artifacts не удаляются без безопасного reference proof.  
**TESTS:** saturation, retention/history, abandoned `.upload`, quota-before-write.  
**RESULT:** PASS.

## 7. 4K preview transport

**STATUS:** PARTIAL  
**FILES:** `SharedFrameRing.cs`, `MediaHostProtocol.cs`, `PreviewContracts.cs`, `MediaHostServer.cs`, `MediaHostClient.cs`, `PreviewPresenter.cs`, `VideoWorkerSupervisor.cs`, `HostPlaybackSession.cs`, protocol/integration/benchmark tests.  
**ROOT CAUSE:** BGRA payload travelled through named pipe with per-frame managed allocations; pipe reader and WPF dispatcher accumulated work.  
**FIX:** protocol v2 uses named pipe only for control/descriptor and a three-slot reusable MMF BGRA ring for frame data. Reading slots cannot be overwritten; producer uses round-robin/free-or-oldest-ready; client coalesces notifications; WPF uses latest-frame-wins with at most one pending callback. Decoder buffers use `ArrayPool`; existing late-frame/single-layer/CUDA fallback paths remain covered.  
**TESTS:** ring ownership/latest tests 2/2; MediaHost protocol 6/6; integration 10/10; 1000-seek stress 1/1 (3m21s). Final 5-second benchmarks are in `benchmark-results/final-*.json`.

| Mode | Frames | Drops | Interarrival p95 | Presentation p95 | Seek p95 | Host+client CPU | RAM | Allocations | Gen2 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1080p60 | 273/300 | 27 | 23.7 ms | 163.0 ms | 1111.5 ms | 5.8% | 314.1 MiB | 137.1 MiB | 2 |
| 1440p60 | 175/300 | 125 | 66.0 ms | 320.4 ms | 1157.3 ms | 9.8% | 447.1 MiB | 251.4 MiB | 2 |
| 4K30 | 67/150 | 83 | 182.1 ms | 2807.4 ms | 1272.7 ms | 13.3% | 756.9 MiB | 415.4 MiB | 2 |
| 4K60 | 57/300 | 243 | 211.5 ms | 4919.3 ms | 1332.4 ms | 13.5% | 724.6 MiB | 383.4 MiB | 2 |

CPU is normalized over logical processors; RAM is host+client working set. The remaining measured bottleneck is FFmpeg→MediaHost raw BGRA over redirected stdout (4K decode/read about 16 fps), so the required 4K30 150/150 target is not met.  
**RESULT:** PARTIAL.

## Final verification

- Desktop build: PASS, 0 warnings / 0 errors.
- Core tests: PASS, 130/130.
- UI tests: PASS, 38/38.
- MediaHost integration: PASS, 10/10 plus 1000-seek stress 1/1.
- AI Server build: PASS, 0 warnings / 0 errors.
- AI Server tests: PASS, 69/69.
- PowerShell parser: PASS for install/activate/run scripts.
- Python compile: PASS.
- Python cancellation tests: PASS, 2/2 via project runtime `unittest` (`pytest` is not installed in that runtime).
- `git diff --check`: PASS (no whitespace errors; only Git LF→CRLF conversion notices).
- Automatic commit: not performed.

## Status table

| Item | Status |
|---|---|
| Cache safety | PASS |
| Cancellation | PASS |
| Job state machine | PASS |
| Worker cold-start | PASS |
| CountTokens isolation | PASS |
| Embedding classification | PASS |
| Remote auth | PASS |
| TLS | PASS |
| Model integrity | PASS |
| Model-aware dedup | PASS |
| Server limits | PASS |
| 4K30 | PARTIAL |
| 4K60 | PARTIAL |
| Release gate | PARTIAL |
