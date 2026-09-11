# READY FOR USER implementation ledger

Authority: user-approved KadrStudio readiness plan in this task, 2026-09-07.
Baseline: `LocalData/readiness-baseline/20260907`, including working-tree patch,
copies and SHA256 manifest. Original branch: fix/ollama-server, HEAD 2e1d88c.

## Constraints and rulings

- Preserve immutable ProjectState, exact time, migrations, MediaHost boundary,
  HTTP v2, model capability gates, and isolated Agent Draft.
- Keep existing uncommitted work. No automatic commits, resets or cleaning.
- Ruling: implement in the user-selected working tree with an independently
  captured baseline; switching to a clean HEAD would omit approved uncommitted code.
- Ruling: tests and fixes precede performance changes. Unknown UI/production-model
  readiness stays unverified until the real scenario is executed.
- Installed data: LOCALAPPDATA/KadrStudio; portable: explicit LocalData beside app.
- Performance targets: native WPF 1080p60 >=59.4 unique fps, <=1% drops,
  interval p95 <=25ms; seek p95 <=200ms warm/1s cold; AV drift <=40ms/30min.
- High-resolution preview uses automatic <=1080p proxies; export retains originals.

## Execution order and acceptance

### Current checkpoint (2026-09-11; supersedes older evidence below)

- E2.4 partial: desktop preview and export now share project-coordinate WPF text
  layout; export uses leased transparent raster artifacts with actual font hashes.
  Preview selects layers in RenderPlan order with exact half-open time ranges.
  UI adapters 96/96 PASS (`LocalData/readiness-results/20260911/ui-common-text.trx`);
  four real FFmpeg raster/export cases pass, including portrait, rotation and 4K
  (`LocalData/readiness-results/20260911/text-export.trx`). Full MainWindow/DPI/font
  matrix, ASS and resource bounds for large caption counts remain open. The direct
  infrastructure drawtext fallback is not covered by the shared-layout guarantee.
- E5.3 review: completed-result reuse now durably binds cross-owner request aliases,
  enforces a combined alias limit and rechecks cancellation under the job gate.
  Reviewed server suite 98/98 PASS; a fresh persisted full-suite result follows.
- E4.1/E11 render lifetime: FFmpeg/FFprobe completion now awaits owned process exit
  and stream readers on cancellation/fault before raster leases are released.
  Two targeted real-process regressions pass; full process regression after these
  and the text changes is pending. No READY FOR USER or final E13 claim.

- E5.3 partial: additive OwnerId/RequestId, owner-scoped active deduplication,
  durable request aliases, owner cancellation and response confirmation implemented.
  Global completed result reuse remains available; legacy requests keep their route.
  Server 95/95 PASS includes HTTP foreign cancellation, reload owner enforcement,
  terminal retry aliases and completed reuse. Desktop rejects unconfirmed ownership
  from older servers; fake HTTP + real audio proxy test checks cleanup of all known
  jobs after polling failure/cancel. Lost-create-response cleanup, persisted desktop
  owner/checkpoints, runtime handshake and real production resume remain open.
- E3.1 partial: composition root now constructs editorial client/indexer/reasoner,
  pipeline, telemetry and agent log; MainViewModel receives these dependencies.
  Architecture gate reproduced old construction and now passes. Workflow delegate
  inversion and remaining session orchestration extraction are still open.
- Verification this continuation: Core 174/174, server 95/95, process integration
  35 PASS + 1 explicit opt-in benchmark SKIP (3m22s). Process evidence is before the
  subsequent owner-confirmation/composition-root changes; those have UI/server
  verification, not a second full process run yet. Release preflight now runs before
  package deletion and requires environment tests; it rejected missing real-AI and
  benchmark variables without modifying release output. No installer compiler was
  found on PATH. These facts do not close E12/E13.
- E12.1 implementation: explicit portable marker and per-user installed resolver;
  offline `--migrate-data-from` copies to sibling staging, verifies hashes, refuses
  existing/overlapping/link paths, and publishes without deleting source data.
  Packaging includes portable marker and installer excludes marker/LocalData.
  Actual installer/upgrade/readonly launch not verified. Migration requires closed
  application processes; failed staging is retained. Tests use explicit data root.
- E4.1 preview: completed jobs and retired generation tokens are released during
  lifetime instead of retained until shutdown. Regression with 100 reconfigurations
  reproduced retention and now passes. UI adapter suite 83/83 PASS, including data
  root and migration tests (`LocalData/readiness-results/20260910/ui-storage-proxy.trx`).
  Parallel implementation agents stopped due to execution usage limit; incomplete
  changes were inspected and continued locally. No READY status or stage completion
  is inferred from delegated work.

- B16 degraded-storage reporting: `/health/ready` now includes `jobStorage` and
  requires its healthy state as well as existing model/worker gates. Initial
  recovery, pending scan/reads/commits and jobs with storage errors are reported
  without exposing filesystem paths or performing a disk scan per health request.
  HTTP tests verify locked-history degradation and recovery while `/health/live`
  remains available; recovery/cancel tests verify failed scan/read/commit counters
  and their return to healthy after retry. Server 89/89 PASS
  (`LocalData/readiness-results/20260910/server-storage-health.trx`).
  README documents the additive contract and limits: this is observed job-storage
  health, not a writability probe, worker-liveness check or power-loss guarantee.
  Next: durable execution receipts/restart semantics and owner/resume (E5.3/E11);
  full B16 remains open. Earlier references to missing degraded reporting below
  are superseded for these observed job-storage failures only.

- B16/E11 job-history cleanup: two regressions reproduced removal from memory
  despite a locked history file and disposal of a semaphore still held by another
  operation. Cleanup now follows recovery's creation/job gate order, rechecks the
  terminal record, deletes its file before removing memory/index state, and leaves
  failed deletions visible with StorageError for the next cleanup pass. Existing
  gate holders/waiters can finish safely. Tests also verify healthy expired jobs
  are removed while a blocked job is retained, active jobs survive, and removed
  records do not reappear on a recovery scan. Server 88/88 PASS
  (`LocalData/readiness-results/20260910/server-cleanup-storage.trx`).
  This covers same-process cleanup coordination and Windows file locks; multi-process
  storage ownership, power-loss durability and the full crash matrix remain open.

- B16 cancellation during storage failure: failed Cancel commits now retain a
  pending cancellation and suppress queue starts until it is durably published.
  Public state remains the last committed state with StorageError; the Cancel
  caller still receives the write failure. A late worker failure cannot replace
  the pending cancellation, and storage retries re-read pending state under the
  job gate instead of committing an obsolete snapshot. Three Windows file-lock
  regressions cover queued/recovered-running jobs and an active worker failing
  before storage unlock. Server 86/86 PASS
  (`LocalData/readiness-results/20260910/server-cancel-storage.trx`);
  Release warnings-as-errors build PASS (single MSBuild node), diff check PASS.
  Pending cancellation is retained in memory only: server crash before its commit
  remains unverified/unsolved, as do durable execution receipts, degraded health,
  owner-scoped remote cancellation and the full B16 restart/fault matrix.
  Next continuation: durable job transition/restart and cleanup concurrency risks;
  E5.3 owner/resume and E11 reliability are still open.

- B16 server recovery: structural validation precedes fingerprint calculation,
  in-memory registration and queue reservation. Six regressions reproduced missing
  parameters, null required fields, invalid state and mismatched file/job ID.
  Such files are preserved and isolated; healthy jobs still execute. A valid legacy
  record without fingerprint/version fields remains readable. Server 83/83 PASS
  (`LocalData/readiness-results/20260910/server-recovery-validation.trx`).
  Durable terminal-state crash idempotency, degraded-health reporting and full
  restart/fault matrix remain open; B16 is not globally closed.

- E5.1 audio selection: multi-audio drag/drop and double-click now ask for one
  audio stream (container default preselected) or all streams on separate tracks.
  Cancel does not add clips. Invalid/stale stream IDs are rejected before editing;
  selected IDs survive linked editing history and SQLite save/reopen. UI suite
  76/76 PASS (`LocalData/readiness-results/20260910/ui-audio-selection.trx`).
  Real FFmpeg export/decode verifies the selected 880Hz stream instead of the first
  440Hz stream (`LocalData/readiness-results/20260910/selected-audio-export.trx`).
  Full dialog/user-route E2E and a separate persisted primary-audio choice for AI
  analysis remain open; this change selects timeline audio, not server analysis policy.

- B06/B07/D03 preview continuation: STA tests with a delayed fake MediaHost
  reproduced stale Prepare starting the previous project and disposal overlapping
  active Prepare. Presenter now captures a project/quality epoch, rejects stale
  queued operations and acknowledgements, serializes Invalidate with updates, and
  cancels/awaits active updates before host disposal. Repeated identical SetProject
  does not invalidate work. Sequence/settings changes advance frame generations.
  UI dispatch uses the owning Image dispatcher. Six barrier cases cover project,
  quality, sequence settings, invalidate, close and unchanged input. UI suite:
  73/73 PASS (`LocalData/readiness-results/20260909/ui-presenter-state-lifetime.trx`).
  These are real WPF controls on STA with a controlled engine, not full user E2E.
  Full host/UI interleavings, blocked frame presentation and soak remain open.

- Current continuation: E3.2/E4.2 artifact lifetime before automatic proxy wiring.
  Added reference-counted raw-artifact pins, acquired before publication. Trim,
  Clear and Invalidate preserve pinned payloads/checksums; Move rejects active pins
  and prevents new pins while relocating. Pins are shared by cache instances in
  the same process, releases are idempotent. Core 174/174; final cache subset 24/24.
  Preview keeps active paths pinned; retired paths are released after host
  acknowledgement of the replacement plan. Cross-process leases, persisted
  derived-source pins and hard quota admission remain open.
- E5.2 continuation: SetProject queues high-resolution timeline video automatically,
  with one encoder. Proxy v4 fits within 1920x1080 without upscaling/padding and uses
  source timing (no project-FPS conversion). Default preview now preserves native
  1080p instead of halving it. UI offers Auto/original and proxy preparation status.
  Real FFmpeg regression 3/3 PASS (`LocalData/readiness-results/20260909/automatic-proxy.trx`):
  native 1080 skips encoding, 1440 prepares 1080p30, original paths/audio stay intact,
  corruption/relink and acknowledged pin release pass. Sizing tests 4/4 PASS.
  Full WPF routes, cancellation/lifetime, VFR/portrait media and performance gates
  remain open. Automatic activation now uses an owned, cancellable/coalesced task;
  shutdown awaits it before disposing MediaHost. Three deterministic tests cover
  burst serialization, cancellation cleanup and failure recovery. UI adapters 67/67
  PASS (`LocalData/readiness-results/20260909/ui-proxy-lifetime.trx`). Full process
  regression after automatic wiring: 34/34 PASS, 4m06s, no skips
  (`LocalData/readiness-results/20260909/process-automatic-proxy.trx`). Both Release
  solutions compile with warnings-as-errors. No WPF performance claim.

- NOT READY FOR USER. Work remains across E1–E13; no production AI, full WPF,
  clean install or performance qualification has been completed.
- Latest Core run: 174/174 Release after artifact pin support. Latest UI adapter run:
  76/76 including audio selection/save/reopen
  (`LocalData/readiness-results/20260910/ui-audio-selection.trx`).
  Desktop Release warnings-as-errors build after these changes: 0 warnings/errors.
- Earlier process runs selected an old Debug MediaHost even in Release tests.
  They do NOT verify the changed host. Resolver now requires the test assembly's
  configuration. Corrected Release run: 32/32, 3m33s, stored at
  `LocalData/readiness-results/20260909/process-current-release.trx`.
  Post-relocation/lifetime Release run: 32/32, 3m44s, stored at
  `LocalData/readiness-results/20260909/process-after-transport-lifetime.trx`.
- Server latest verified run: Release 83/83 including startup storage failures,
  structural corruption isolation and legacy recovery. Root ran the full suite.
  Python last verified 7/7; unchanged since then. Global E13 remains pending.
- B03: raw legacy tasks and older envelopes preserve their format, cancel missing
  Draft references, and survive SQLite reopen. Reviewer found default immutable
  arrays broke older-envelope Undo; root reproduced and fixed it (4 targeted PASS).
- B09: seek carries explicit generation through IPC; WPF clears its pending frame
  before seek, client preserves operation identity during frame updates. Real
  seek/restart regression passes. Full blocked-WPF/concurrency matrix remains open.
- D02: MMF/Mutex implementation moved to Infrastructure/Preview; descriptor,
  lease contract and frame dispatcher remain Application. Core/protocol tests pass;
  process recheck passed 32/32. Common managed-artifact policy remains open.
- B16: unreadable job records, recovery transition/overflow write failures and
  directory enumeration failure now retry without prematurely publishing new state.
  Pending-terminal crash idempotency and startup deduplication gaps remain open.
- E5.1: Save As menu/Ctrl+Shift+S and single/batch relink preview routes added.
  Relink rejects stale session/ABA and changed candidate metadata; stored SHA-256
  cannot be skipped by fast search. Folder scans return control to the caller.
  Actual WPF route, cancellation lifetime and primary-audio selection remain open.
- Export UI now offers 1440p and 2160p; build passed; actual 4K export verification
  for these additions remain pending. Originals remain the export inputs.
- D03 ProcessRunner: agent stopped at usage limit after authoring tests; root
  reproduced four failures and implemented 16Mi-character stdout limit (explicit
  failure), 64Ki stderr tail, 4096-character callback lines, fail-fast drain
  supervision and kill/await cleanup. All seven safety tests passed, including
  pre-cancel start protection. Broader task ownership/log rotation is open.
- B20: analysis cache v3 verifies SHA-256 for every expected payload and publishes
  immutable generations. Truncated/same-length corruption, missing chunks, malformed
  manifest, concurrent rebuild and new-builder reopen pass real FFmpeg tests.
  Invalid/legacy directories are preserved; retention, pinning, shared-store policy
  and crash injection remain open. This deliberately trades disk retention for safety.
  Bundle disposal no longer recursively deletes arbitrary Temp directories; builder
  failure cleanup removes only its registered staging files, with reparse guards.
- B21 prerequisites: preview keys/configuration include physical source metadata,
  fingerprints and path; relink/same-path changes invalidate ready entries and
  generation identity rejects retired jobs. Substitution matches the source path
  and changes video/decode signatures so MediaHost can observe the switch. Extended
  real FFmpeg regression passes (including original audio and corruption rebuild).
  Automatic scheduling, <=1080p policy and status UI are now implemented as described
  above; full UI/format coverage, cross-process leases and performance remain pending.

| Task | Work / regression criteria | Status |
|---|---|---|
| E0.1 | Capture current tracked/untracked source baseline and hashes | Captured; reproduction verification pending |
| E0.2 | Honest environment-required and production test gates | Implemented; release runner integration pending |
| E1.1 | B01/B02 owned cache deletion and protected export publication | In progress |
| E1.2 | B03/B04 preserve non-history metadata, recovery save chronology | Recovery/chat and missing-Draft cancellation tested; legacy/concurrency matrix pending |
| E1.3 | B05 coordinated save/history/recovery/leases and saved stamp | Implemented; four document safety tests pass; broader faults pending |
| E2.1 | B06/B07 non-rewinding session stamps, serialized mutations | Stamps implemented; async route and dispatcher work in progress |
| E2.2 | B08 linked media/subtitle operations, frame boundaries, locks | Linked edits and session lock checks implemented; extended coverage pending |
| E2.3 | B09-B11 frame ordering, cancellation, EOF state | Reader/presenter, cancellable reads and video-only EOF implemented; combined regression ongoing |
| E2.4 | B12 shared overlay layout preview/export | Pending |
| E2.5 | B13/B15/B16 artifact download/publication and durable jobs | Initial fixes tested; crash/restart/storage fault matrix pending |
| E2.6 | B19 measured coverage including EOF/VFR/silence | Video coverage and cancellation tests pass; audio/QC/VFR pending |
| E3.1 | D01 application workflow and composition root | Editorial construction moved to root; workflow/session extraction pending |
| E3.2 | D02 transport implementation outside Application, artifact policy | MMF moved, process 32/32; shared artifact policy pending |
| E4.1 | D03 owned tasks/processes and bounded diagnostics | ProcessRunner bounded/fault cleanup implemented; other tasks/logs pending |
| E4.2 | B20/B24 valid cache rebuild, retention and bounded resources | Analysis cache integrity/rebuild tested; retention, pins and resource budgets pending |
| E4.3 | D07 actual contracts/documentation, preserve migrations | Pending |
| E5.1 | B22 Save As, relink, primary audio selection | Save As/relink and timeline audio choice wired; UI E2E, relink lifetime and AI primary-audio policy pending |
| E5.2 | B21 automatic proxy scheduling/invalidation | Automatic timeline scheduling, 1080p, status and lifetime implemented; WPF E2E/format matrix/performance pending |
| E5.3 | B14/B17/B18/B25 qualified runtime identity, owned jobs/resume | Owned jobs and known-job cleanup tested; runtime handshake, lost responses and durable resume pending |
| E6.1 | Real WPF manual editing/save/reopen/export route | Pending |
| E6.2 | Production AI Draft/QC/Accept plus manual offline mode | Pending |
| E11.1 | Persistence/process fault injection and crash recovery | Pending |
| E11.2 | Cancellation <=1s UI/10s process cleanup, concurrency/soak | Pending |
| E7.1 | Calibrated WPF/FFmpeg/MMF frame/time/resource instrumentation | Pending |
| E7.2 | Correlated Full Analysis/inference decode/network tracing | Pending |
| E8.1/E8.2 | Measured preview targets, bounded queues/proxies/copies | Pending |
| E9.1/E9.2 | Range gap decode and bounded reusable audio/features | Pending |
| E10.1/E10.2 | Identity-based runtime reuse, bounded idempotent network | Pending |
| E12.1 | Installed/portable resolver and reversible migration | Resolver, explicit verified copy and packaging mode implemented; installer/readonly/crash matrix pending |
| E12.2 | Clean restore/build/tests/publish/install/update smoke | Pending |
| E13 | Final regression and before/after gates after last modification | Pending |

## Dependency review

| Shared interface | Ordering / ruling |
|---|---|
| E1.2 -> E1.3/E2.1: EditorSession | Preserve metadata first; stamps must not invalidate own AI progress |
| E1.1 -> E3.2/E4.2/E5.2: artifact store | All destruction must enforce ownership; new cache paths reuse safety |
| E2.3 -> E3.2/E5.2/E7.1: preview transport | Keep leases and frame ownership through relocation and instrumentation |
| E2.5 -> E5.3/E10.2: jobs/artifacts | Durable/idempotent semantics before retries and shared results |
| E2.6 -> E9: coverage | Optimize only against actual decoded evidence, never requested duration |
| E6 -> E11 -> E7-E10 | Reliability gate before performance; no false READY claims |
| E12 -> E13 | Final tests run against released configuration, not only dev tree |

## Evidence and remaining work

Implementation not complete. Audit test results are historical baseline evidence,
not evidence that the subsequently modified tree passes.

### 2026-09-08: active implementation

- E1.1 cache: six new behavioral cases failed on original code; after fixing
  ownership, selective deletion, marker retention and memory eviction, all 20
  then 23 cache tests passed, including real Windows junction and foreign-file move.
- Export boundary: fixed fixture setup (early failures were missing FFmpeg,
  not the defect); with a non-executed executable fixture, removing the guard
  produced three expected renderer-reached failures. Guard restored; full Core
  suite passed 142/142. Desktop Release build passed 0 warnings/errors.
- B03/B04: RED demonstrated empty chat after Undo and recovery selecting Edit 22
  instead of Undo state. GREEN: 38 history/editor/persistence tests passed.
  Chat survives Undo/Redo/rollback; recovery schema v2 uses save ordinal and saved_at.
  Agent-memory/Draft reconciliation and explicit legacy recovery tests remain.
- E1.3: document coordinator, atomic checkpoint transfer, delayed Save As lease
  replacement and snapshot-aware dirty state implemented; verification ongoing.
- Ruling: retain recovery for a different snapshot when saving, because older
  persisted revisions cannot safely establish chronology after Undo.
- Additional finding: ExportSettings.GetSize currently caps UI export at 1080p.
  E5/release must add explicit 1440p/4K choices and verify actual output; preserving
  4K capability cannot be claimed solely from the generic render engine.
- First state/recovery subagent hit usage limits without edits; root resumed it.
  Environment-gates implementer completed E0.2 and now owns only B13 download/tests.
- E0.2 review: optional tests have xUnit discovery skips; required mode fails at
  test entry. Real-AI client always requires production qualification. Agent ran
  three helper cases, five explicit skips and a required-mode expected failure.
  The preflight script checks fixture variables only; it is not proof of model,
  WPF or benchmark readiness. See environment-gates-implementation-report.md.
- Recovery legacy test reproduced list ordering by a future document timestamp.
  List now orders each document by save ordinal, including legacy migration.
  Three recovery/history safety tests pass, including retention of 20 versions.
- Four document safety cases passed: original lease after failed Save As,
  checkpoint transfer through first Save and Save As, shared operation queue,
  and keeping a newer recovery when saving an older snapshot.
- ABA regression failed with an incorrectly applied proposal, then passed after
  editable input validation. Session ID + monotonic edit version additionally
  reject Undo/Redo and reopened identical snapshots. State version advances for
  chat/progress while edit version does not. Six proposal tests passed.
  State-only callers get conservative immutable input comparison; production
  VM snapshots additionally carry session stamps. Remaining async routes and
  thread ownership are not yet considered fixed.
- Latest full UI adapter run: 42/42 passed after stamp and save-version wiring.
  Windows hardlink export regression authored; execution pending build slot.
- Next verification: Core 149/149 (including real hardlink), UI adapters 43/43
  after owner dispatcher and stale import/upscale/media-preparation guards.
  Dispatcher tests use a real STA dispatcher; complete VM scheduler interleavings
  and post-close task ownership remain open. No claim of full B07 closure.
- B09 reader RED returned frame 2 after frame 3. Reader now discards superseded
  Ready slots and tracks high-water ID per ring. Seven protocol tests pass,
  including read-slot ownership and deferred generation filtering/queue clear.
  Presenter rechecks generation on publish. Seek-specific generation propagation,
  B10 cancellable stdout and B11 EOF remain open.
- Real MediaHost suite: 10/10 passed in 3m19s outside the known sandbox named-pipe
  restriction; sandbox run failed connection permissions before exercising host.
- B13: three Windows download RED failures (sharing violation); four GREEN cases
  cover close before rename, SHA-256 ID, content length, cancel and old destination.
  Follow-up agent stayed pending_init and was interrupted without B13 edits;
  root implemented and tested this task.
- B15: corrupted JSON returned a cache hit in RED. Atomic payload/metadata
  publication and checksum verification repair repeated Put; two artifact tests
  and full server suite 70/70 passed. Full crash-point injection remains open.
  Current Find verifies by hashing; identity-safe warm hash reuse belongs to E10.
- B19: synthetic decoder RED reported 243 samples from three frames, fabricated
  empty-decoder coverage and bridged PTS gaps. Coverage now uses processed PTS,
  excludes non-monotonic fallback timestamps, and bounds last shot by decoded end.
  Cancellation RED left capture open; decoder release now uses finally.
  Audio silence/unknown semantics, real VFR/corrupt fixtures and QC integration
  remain open; this is not production model qualification.
- B10: real child process writes one byte then stalls. RED blocked the RunAsync
  caller; cancellable ReadAsync now returns control and cancels the pending read.
  GREEN explicitly waits for the child's partial-write signal before cancellation.
- B11: video-only EOF RED stayed Playing; monitor now awaits pumps/presentation,
  clock/audio drain, stops resources and publishes exact end in Paused. Repeat
  Play restarts from range start. Two targeted process cases passed.
- B16: real filesystem publication locks reproduced false Succeeded and dead
  consumer. State publishes after durable save; failed starts requeue and terminal
  commits retry without inference. Per-job StorageError exposes degraded writes.
  Two failure cases and server suite 72/72 passed. Startup recovery I/O failures,
  crash during a pending commit and pending-state admission bounds remain open.
- Combined integration run: 31 passed, one proxy regression failed. Legacy default
  artifact root had no ownership marker, so safe writes refused it. Added factory
  selects a separately owned child while preserving old/unmarked contents; settings
  persist the selected root. Proxy test now uses its own artifact store, not user
  LocalData. Narrow proxy case passes; full UI adapter suite now 48/48 passes.
  This is cache namespace selection, not migration/deletion of legacy contents.
- Next: repeat combined regression after namespace fix, finish B03 Draft/task
  reconciliation and E2.1 interleavings, then linked subtitle/lock semantics.
- Combined integration repeat after namespace fix: 32/32 passed (before linked
  subtitle edits). No production AI, real WPF route or performance claim.
- Linked subtitles now follow move/trim/split/delete/ripple/unlink. Fractional
  FPS tests cover 24 operations with Undo/Redo; validator recognizes a video +
  subtitle group without audio. Core passed 154/154 after removing synchronous
  DisposeAsync waiting from the cache factory via an actual IDisposable method.
- Locked subtitle regressions initially allowed all six linked edits. Session,
  batch and proposal paths now validate locked track contents before publication;
  all six reject atomically and succeed after explicit unlock. Core 160/160 and
  UI adapters 48/48 passed. Broader track/sequence and derived-rendition policies
  still need tests; this is not full B22 closure.
- Split/razor and drag minimums now use the project's frame duration instead of
  0.1 seconds. A shared exact-tick boundary helper covers fractional FPS and
  non-finite input. Latest full verification is recorded below when completed.
- B03: Undo Draft creation reproduced a persisted ReviewingDraft reference to a
  missing sequence. Reconciliation clears the reference/checkpoint and cancels
  nonterminal tasks with a journal reason; terminal task phases remain historical.
  Redo restores timeline content without reviving cancelled work. VM Undo/Redo and
  task recovery reconcile the orchestrator too. Core 164/164, UI adapters 48/48
  and server 72/72 passed at this checkpoint. Legacy raw task memories, repeated
  save/reopen and broader concurrent workflow scenarios still need coverage.
- Lock guard review found container initialization incorrectly treated a locked
  legacy timeline as deleted. A regression now checks identity initialization
  against the unchanged captured timeline; no permission bypass for content edits.
- 2026-09-09 checkpoint: Core 165/165, UI adapters 48/48, server 72/72,
  Python 7/7. Both Release solutions built with --no-restore and warnings-as-errors,
  zero warnings/errors. These are development-tree checks, not clean installation
  or production qualification. Full process integration repeat follows below.
- Full process integration after the latest Desktop changes: 32/32 passed in
  3m23s outside the known named-pipe sandbox restriction; EnvironmentRequired
  suites were explicitly excluded. Process-leak soak testing remains pending.

- D03 MediaHost stderr now retains a bounded 64Ki-character tail; shutdown owns
  and awaits its reader with process exit. Current process regression: 32/32 PASS.
