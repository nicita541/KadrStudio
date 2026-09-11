# KadrStudio P0/P1 Fixes Implementation Plan

> **For agentic workers:** Execute inline in the strict task order below. Do not commit automatically and do not combine the Desktop and AI Server folders.

**Goal:** Fix and verify the seven confirmed correctness, security, lifecycle, resource, and preview-transport problems listed in the approved user specification.

**Architecture:** Make one independently testable change at a time. Security and correctness blocks 1-6 stay isolated from the block-7 BGRA shared-memory prototype; named pipes remain the MediaHost control plane.

**Tech Stack:** .NET 10, C#, WPF, ASP.NET Core, named pipes, memory-mapped files, Python, gRPC, pytest/xUnit, PowerShell.

**Spec:** User attachment `pasted-text.txt` from 2026-09-01.

## Global Constraints

- Strict order: cache, cancellation/state, worker/GPU, auth/TLS, model integrity/dedup, server limits, 4K transport.
- Before every production change, add and run a regression test that fails for the intended reason.
- After every block, run its targeted tests and build.
- Do not run the long Full Gate until targeted regressions are green.
- Preserve 1080p60, 1440p60, single-layer fast path, late-frame policy, and CUDA fallback.
- Do not commit automatically.

---

### Task 1: Owned Desktop media cache

**Files:**
- Modify: `src/Kadr.Infrastructure/Caching/DiskMediaArtifactCache.cs`
- Modify: `src/Kadr/Services/WorkspaceSettingsService.cs`
- Modify: `src/Kadr/Views/MainWindow.xaml.cs`
- Test: `tests/KadrStudio.Core.Tests/MediaArtifactCacheTests.cs`
- Test: `tests/KadrStudio.UiAdapters.Tests/ProjectViewMapperTests.cs`

**Interfaces:** `MoveAsync(selectedParent)` resolves `<selectedParent>/KadrStudioCache`; destructive operations require a valid `.kadr-cache-owner.json` bound to the canonical cache root. A collision aborts a move without overwriting either tree.

- [ ] Add literal-behavior tests for foreign files, non-empty target collision, missing/forged marker, protected roots, source/project overlap, and restart after move.
- [ ] Run the new tests and verify RED against direct-root deletion/overwrite.
- [ ] Add canonical root resolution, marker validation, protected-root checks, collision-safe copy/publish, and settings migration.
- [ ] Run targeted Core/UI tests and Desktop build.

### Task 2: Cancellation and terminal job state

**Files:**
- Modify: `src/Kadr.AiServer/Jobs/AnalyzerJobService.cs`
- Modify: `src/Kadr.AiServer/Workers/LoopbackGrpcWorkerGateway.cs`
- Modify: `workers/python/kadr_worker/server.py`
- Modify: `workers/python/kadr_worker/analyzers.py`
- Modify: `workers/python/kadr_worker/animesr_upscale.py`
- Test: `tests/KadrStudio.AiServer.Tests/AnalyzerJobServiceTests.cs`
- Test: `workers/python/tests/test_cancellation.py`

**Interfaces:** Per-job synchronization exposes only Queued->Running/Cancelled and Running->Succeeded/Failed/Cancelled. gRPC cancellation becomes a Python cancellation predicate/event checked by analyzer loops and used to terminate/kill owned subprocesses.

- [ ] Add RED tests for queued/running/completion-race/repeated/shutdown cancellation.
- [ ] Implement atomic transitions/versioning and cancellation propagation.
- [ ] Add RED Python tests proving loops and subprocesses stop.
- [ ] Implement cooperative cancellation plus terminate/kill fallback.
- [ ] Run targeted .NET/Python tests and AI Server build.

### Task 3: Worker and GPU lifecycle

**Files:**
- Modify: `src/Kadr.AiServer/Workers/LoopbackGrpcWorkerGateway.cs`
- Test: `tests/KadrStudio.AiServer.Tests/LoopbackGrpcWorkerGatewayTests.cs`
- Test: `tests/KadrStudio.AiServer.Tests/AiServerOptionsTests.cs`

**Interfaces:** `GetRuntimeAsync` uses a per-analyzer async startup lock with double-check publication. Token counting participates in the accelerator lease/eviction lifecycle unless a verified CPU-only tokenizer path is available. `embedding` is CPU-only.

- [ ] Add RED concurrent cold-start, token-count-during-vision, embedding-during-vision, and crash/restart tests.
- [ ] Implement startup locks, safe dead-runtime replacement, token isolation, and CPU embedding classification.
- [ ] Run targeted tests and AI Server build.

### Task 4: Remote authentication and TLS

**Files:**
- Modify: `src/Kadr.AiServer/Infrastructure/KadrApiAuthorizationMiddleware.cs`
- Modify: `src/Kadr.AiServer/Configuration/AiServerOptions.cs`
- Modify: `src/Kadr.AiServer/Program.cs`
- Modify: `scripts/run-ai-server.ps1`
- Test: `tests/KadrStudio.AiServer.Tests/ApiContractTests.cs`
- Test: `tests/KadrStudio.AiServer.Tests/AiServerOptionsTests.cs`

**Interfaces:** Explicit server mode controls authentication; peer IP never downgrades remote mode. Remote HTTP startup fails unless `KADR_AI_ALLOW_INSECURE_REMOTE_HTTP=1`.

- [ ] Add RED tests for local, remote direct/proxied, missing bearer, and insecure remote startup.
- [ ] Implement mode-derived auth and startup URL validation.
- [ ] Run targeted tests and AI Server build.

### Task 5: Model integrity and model-aware deduplication

**Files:**
- Modify: `src/Kadr.AiServer/Inference/ModelCapabilityGate.cs`
- Modify: `src/Kadr.AiServer/Jobs/AnalyzerJobService.cs`
- Modify: `scripts/install-production-models.ps1`
- Modify: `scripts/activate-production-model.ps1`
- Modify: `scripts/run-ai-server.ps1`
- Test: `tests/KadrStudio.AiServer.Tests/ModelCapabilityGateTests.cs`
- Test: `tests/KadrStudio.AiServer.Tests/AnalyzerJobServiceTests.cs`

**Interfaces:** Verified identity contains canonical path, size, mtime, SHA-256, and revision. SHA-256 is reused only while size/mtime remain unchanged. Model-backed job fingerprints include identity plus analyzer/pipeline implementation versions.

- [ ] Add RED tamper and model-replacement dedup tests.
- [ ] Extend manifests/runtime verification and fingerprints for every listed model-backed analyzer.
- [ ] Run targeted tests, script checks, and AI Server build.

### Task 6: Bounded AI Server resources

**Files:**
- Modify: `src/Kadr.AiServer/Configuration/AiServerOptions.cs`
- Modify: `src/Kadr.AiServer/Jobs/AnalyzerJobService.cs`
- Modify: `src/Kadr.AiServer/Storage/ContentAddressedAssetStore.cs`
- Modify: artifact storage and endpoint files as required by existing boundaries.
- Test: `tests/KadrStudio.AiServer.Tests/AnalyzerJobServiceTests.cs`
- Test: `tests/KadrStudio.AiServer.Tests/ContentAddressedArtifactStoreTests.cs`

**Interfaces:** Bounded queue/active work/history/storage are configured by validated options. Saturation returns a typed error mapped to 429/503. Cleanup removes only terminal history, expired `.upload` files, and unreferenced stale content.

- [ ] Add RED saturation, retention, expired-upload, and quota tests.
- [ ] Implement limits, safe cleanup, metrics, and endpoint error mapping.
- [ ] Run targeted tests and AI Server build.

### Task 7: Three-slot shared-memory BGRA preview

**Files:**
- Modify/create focused protocol classes under `src/Kadr.Application/Preview`, `src/Kadr/Playback`, and `src/Kadr.MediaHost`.
- Modify: `src/Kadr/Playback/PreviewPresenter.cs`
- Test: `tests/KadrStudio.Core.Tests/MediaHostProtocolTests.cs`
- Test: `tests/KadrStudio.Integration.Tests/MediaHostIntegrationTests.cs`
- Add/update the existing preview benchmark harness.

**Interfaces:** Named pipe carries control and shared-memory negotiation. Three reusable slots expose frame id, generation, timestamp, dimensions, stride, valid length, and state. Producer never overwrites Reading; Desktop consumes latest generation/frame and permits at most one pending Dispatcher callback.

- [ ] Add RED protocol/ring ownership/latest-frame/dispatcher tests.
- [ ] Implement the BGRA memory-mapped ring and control-plane negotiation.
- [ ] Run 1080p60, 1440p60, 4K30, and 4K60 benchmarks with the approved metrics.
- [ ] Preserve prior preview correctness tests and record 4K30/4K60 results truthfully.

### Task 8: Final verification and report

**Files:**
- Create: `KADR_P0_P1_FIX_REPORT.md`

- [ ] Run Desktop build, Core, UI, Integration/MediaHost tests.
- [ ] Run AI Server build, server tests, Python syntax/tests.
- [ ] Run `git diff --check` from the shared git root.
- [ ] Write per-item status/files/root cause/fix/tests/result and the required PASS/PARTIAL/FAIL/ALREADY FIXED table.
