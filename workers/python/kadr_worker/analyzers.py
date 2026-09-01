from __future__ import annotations

import base64
import atexit
import hashlib
import json
import math
import os
import socket
import subprocess
import threading
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any


# KadrStudio TimelineTime is a rational 240 kHz clock (divisible by common
# cinema and broadcast frame rates). Keep every worker boundary on that clock.
TICKS_PER_SECOND = 240_000
CHANNEL_FRAMES = 0
CHANNEL_MOTION = 1
CHANNEL_AUDIO = 2
CHANNEL_TRANSCRIPT = 3
CHANNEL_OCR = 4


class CapabilityUnavailable(RuntimeError):
    pass


_LLAMA_PROCESS: subprocess.Popen[Any] | None = None
_LLAMA_LOG: Any | None = None
_LLAMA_ENDPOINT: str | None = None
_LLAMA_LOCK = threading.Lock()
_LLAMA_INFERENCE_LOCK = threading.Lock()


def _shutdown_llama() -> None:
    global _LLAMA_PROCESS, _LLAMA_LOG, _LLAMA_ENDPOINT
    with _LLAMA_LOCK:
        process = _LLAMA_PROCESS
        _LLAMA_PROCESS = None
        _LLAMA_ENDPOINT = None
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=15)
        if _LLAMA_LOG is not None:
            _LLAMA_LOG.close()
            _LLAMA_LOG = None


atexit.register(_shutdown_llama)


def _reasoning_backend() -> str:
    return os.environ.get("KADR_REASONING_BACKEND", "llama.cpp").strip().lower()


def _llama_endpoint() -> str:
    global _LLAMA_PROCESS, _LLAMA_LOG, _LLAMA_ENDPOINT
    import requests

    analyzer = os.environ.get("KADR_WORKER_ANALYZER", "director").strip().lower()
    with _LLAMA_LOCK:
        if (
            _LLAMA_PROCESS is not None
            and _LLAMA_PROCESS.poll() is None
            and _LLAMA_ENDPOINT is not None
        ):
            return _LLAMA_ENDPOINT
        executable = Path(os.environ.get("KADR_LLAMA_SERVER", ""))
        vision = analyzer == "video-understanding"
        model_variable = "KADR_VISION_MODEL" if vision else "KADR_DIRECTOR_MODEL"
        model = Path(os.environ.get(model_variable, ""))
        mmproj = Path(os.environ.get("KADR_VISION_MMPROJ", "")) if vision else None
        if not executable.is_file():
            raise CapabilityUnavailable("KADR_LLAMA_SERVER does not point to llama-server.exe")
        if not model.is_file() or model.suffix.lower() != ".gguf":
            raise CapabilityUnavailable(f"{model_variable} does not point to a GGUF model")
        if vision and (mmproj is None or not mmproj.is_file() or mmproj.suffix.lower() != ".gguf"):
            raise CapabilityUnavailable("KADR_VISION_MMPROJ does not point to a GGUF projector")
        configured_port = os.environ.get("KADR_LLAMA_PORT", "").strip()
        if configured_port:
            port = int(configured_port)
            if not 1 <= port <= 65535:
                raise CapabilityUnavailable("KADR_LLAMA_PORT must be between 1 and 65535")
        else:
            # Let Windows choose a currently available loopback port. Director
            # and critic are separate supervised processes, so a fixed global
            # port only creates collisions after crashes or parallel test runs.
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
                probe.bind(("127.0.0.1", 0))
                port = int(probe.getsockname()[1])
        endpoint = f"http://127.0.0.1:{port}"
        _LLAMA_ENDPOINT = endpoint
        context_variable = "KADR_VISION_CONTEXT_TOKENS" if vision else "KADR_PLANNER_CONTEXT_TOKENS"
        layers_variable = "KADR_VISION_GPU_LAYERS" if vision else "KADR_LLAMA_GPU_LAYERS"
        context = max(2048, int(os.environ.get(context_variable, "8192" if vision else "16384")))
        gpu_layers = max(0, int(os.environ.get(layers_variable, "99" if vision else "12")))
        threads = max(1, int(os.environ.get("KADR_LLAMA_THREADS", "12")))
        data_root = Path(os.environ.get("KADR_AI_RUNTIME_DATA_ROOT", ".")).resolve()
        log_root = data_root / "worker-logs"
        log_root.mkdir(parents=True, exist_ok=True)
        if _LLAMA_LOG is not None:
            _LLAMA_LOG.close()
        _LLAMA_LOG = (log_root / f"llama-{analyzer}.log").open("ab", buffering=0)
        command = [
            str(executable), "--model", str(model),
            "--host", "127.0.0.1", "--port", str(port),
            "--ctx-size", str(context), "--parallel", "1",
            "--n-gpu-layers", str(gpu_layers),
            "--threads", str(threads), "--threads-batch", str(threads),
            "--cache-type-k", "q8_0", "--cache-type-v", "q8_0",
            "--flash-attn", "on", "--jinja", "--no-webui",
        ]
        if mmproj is not None:
            command.extend(["--mmproj", str(mmproj)])
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        _LLAMA_PROCESS = subprocess.Popen(
            command,
            cwd=str(executable.parent),
            stdin=subprocess.DEVNULL,
            stdout=_LLAMA_LOG,
            stderr=subprocess.STDOUT,
            creationflags=creation_flags,
        )
        session = requests.Session()
        session.trust_env = False
        timeout_at = time.monotonic() + max(60, int(os.environ.get("KADR_LLAMA_STARTUP_TIMEOUT", "600")))
        last_error: Exception | None = None
        while time.monotonic() < timeout_at:
            if _LLAMA_PROCESS.poll() is not None:
                _LLAMA_ENDPOINT = None
                raise CapabilityUnavailable(
                    f"llama.cpp exited during startup with code {_LLAMA_PROCESS.returncode}; "
                    f"see {log_root / f'llama-{analyzer}.log'}")
            try:
                response = session.get(endpoint + "/health", timeout=2)
                if response.status_code == 200:
                    return endpoint
            except requests.RequestException as exception:
                last_error = exception
            time.sleep(0.5)
        _LLAMA_PROCESS.terminate()
        _LLAMA_ENDPOINT = None
        raise CapabilityUnavailable("llama.cpp startup timed out") from last_error


def _evenly_spaced_indexes(indexes: list[int], limit: int) -> list[int]:
    if len(indexes) <= limit:
        return indexes
    if limit <= 1:
        return [indexes[len(indexes) // 2]]
    return [indexes[round(position * (len(indexes) - 1) / (limit - 1))] for position in range(limit)]


def _anime_frame_batches(frames: list[Any], timestamps: list[int], duration: int) -> list[tuple[list[Any], list[int], str]]:
    """Bound visual tokens while reserving equal evidence for OP and ED regions."""
    total_limit = max(8, int(os.environ.get("KADR_VISION_MAX_FRAMES", "48")))
    batch_limit = max(4, int(os.environ.get("KADR_VISION_BATCH_FRAMES", "12")))
    edge_span = min(6 * 60 * TICKS_PER_SECOND, max(TICKS_PER_SECOND, duration // 2))
    start_indexes = [index for index, value in enumerate(timestamps) if value <= edge_span]
    end_indexes = [index for index, value in enumerate(timestamps) if value >= duration - edge_span]
    per_edge = max(4, total_limit // 2)
    start_indexes = _evenly_spaced_indexes(start_indexes, per_edge)
    start_set = set(start_indexes)
    end_indexes = _evenly_spaced_indexes([index for index in end_indexes if index not in start_set], per_edge)
    batches: list[tuple[list[Any], list[int], str]] = []
    for offset in range(0, len(start_indexes), batch_limit):
        indexes = start_indexes[offset:offset + batch_limit]
        batches.append(
            ([frames[index] for index in indexes], [timestamps[index] for index in indexes],
             "These samples cover a consecutive window in the beginning region. A normal dialogue/action cold open "
             "is EpisodeBody, not Opening. Look for the later musical credit montage."))
    for offset in range(0, len(end_indexes), batch_limit):
        indexes = end_indexes[offset:offset + batch_limit]
        batches.append(
            ([frames[index] for index in indexes], [timestamps[index] for index in indexes],
             "These samples cover a consecutive window in the ending region. The Ending stops at the first return "
             "to normal narrative; PostCredits and Preview after that are protected content."))
    return batches


@dataclass(frozen=True)
class Asset:
    asset_id: str
    path: Path
    kind: str
    order: int
    start_ticks: int
    stream_index: int | None


def resolve_assets(data_root: Path, asset_ids: list[str], parameters: dict[str, Any]) -> list[Asset]:
    descriptors = {item["id"]: item for item in parameters.get("assets", [])}
    output: list[Asset] = []
    for asset_id in asset_ids:
        if len(asset_id) != 64 or any(character not in "0123456789abcdef" for character in asset_id.lower()):
            raise ValueError("invalid content-addressed asset id")
        path = data_root / "assets" / asset_id[:2] / f"{asset_id}.blob"
        if not path.is_file():
            raise FileNotFoundError(f"asset {asset_id} is unavailable")
        descriptor = descriptors.get(asset_id, {})
        output.append(Asset(
            asset_id, path, str(descriptor.get("kind", "unknown")),
            int(descriptor.get("order", 0)), int(descriptor.get("startTicks", 0)),
            descriptor.get("streamIndex")))
    return output


def _time(ticks: int) -> dict[str, int]:
    return {"ticks": int(ticks)}


def _range(start: int, duration: int) -> dict[str, Any]:
    return {"start": _time(start), "duration": _time(max(1, duration))}


def _fact(source_id: str, start: int, duration: int, channel: int, kind: int,
          summary: str, confidence: float, analyzer: str, stream_index: int | None = None,
          measurements: dict[str, str] | None = None) -> dict[str, Any]:
    return {
        "id": str(uuid.uuid4()), "sourceId": source_id,
        "sourceRange": _range(start, duration), "channel": channel, "kind": kind,
        "summary": summary, "confidence": confidence, "analyzerId": analyzer,
        "analyzerVersion": "2", "attributes": measurements or {},
        "artifactReference": "", "streamIndex": stream_index,
    }


def _coverage(source_id: str, fingerprint: str, duration: int,
              intervals: dict[int, list[dict[str, Any]]]) -> dict[str, Any]:
    return {
        "sourceId": source_id, "sourceFingerprint": fingerprint,
        "sourceDuration": _time(duration),
        "channels": {str(channel): values for channel, values in intervals.items()},
    }


def _interval(start: int, duration: int, samples: int, continuous: bool, analyzer: str) -> dict[str, Any]:
    seconds = max(duration / TICKS_PER_SECOND, 1e-6)
    return {
        "range": _range(start, duration), "sampleCount": max(1, samples),
        "samplingDensityHz": max(0.0, samples / seconds), "isContinuous": continuous,
        "analyzerId": analyzer, "analyzerVersion": "2", "confidence": 1.0,
    }


def empty_index(parameters: dict[str, Any], analyzer: str, channels: list[int]) -> dict[str, Any]:
    now = time.strftime("%Y-%m-%dT%H:%M:%S.0000000+00:00", time.gmtime())
    source_id = str(parameters["sourceId"])
    fingerprint = str(parameters["sourceFingerprint"])
    duration = int(parameters["sourceDurationTicks"])
    return {
        "id": str(uuid.uuid4()), "sourceId": source_id, "sourceFingerprint": fingerprint,
        "pipelineVersion": str(parameters.get("pipelineVersion", "editorial-v2.1")),
        "sourceDuration": _time(duration), "coverage": _coverage(source_id, fingerprint, duration, {}),
        "analyzers": [{
            "id": analyzer, "version": "2", "model": os.environ.get("KADR_WORKER_MODEL", "local"),
            "runtime": os.environ.get("KADR_WORKER_RUNTIME", "python-cuda"),
            "capabilities": channels, "createdAt": now,
        }],
        "chapters": [], "scenes": [], "shots": [], "moments": [], "facts": [],
        "semanticHypotheses": [], "audioEvents": [], "speakerTurns": [], "transcriptWords": [],
        "createdAt": now, "updatedAt": now, "artifactReference": "",
        "segmentRoleHypotheses": [],
    }


def analyze_video(assets: list[Asset], parameters: dict[str, Any]) -> dict[str, Any]:
    try:
        import cv2
        import numpy as np
    except ImportError as exception:
        raise CapabilityUnavailable("opencv production dependency is unavailable") from exception
    visual = next((asset for asset in assets if asset.kind == "visual-proxy"), None)
    if visual is None:
        raise ValueError("video-understanding needs a visual proxy")
    capture = cv2.VideoCapture(str(visual.path))
    if not capture.isOpened():
        raise ValueError("visual proxy cannot be decoded")
    fps = float(capture.get(cv2.CAP_PROP_FPS) or 24.0)
    frame_count = int(capture.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    source_duration = int(parameters["sourceDurationTicks"])
    facts: list[dict[str, Any]] = []
    intervals: dict[int, list[dict[str, Any]]] = {CHANNEL_FRAMES: [], CHANNEL_MOTION: [], CHANNEL_OCR: []}
    dense_ranges = [
        (int(gap.get("startTicks", 0)),
         int(gap.get("startTicks", 0)) + int(gap.get("durationTicks", 0)))
        for gap in parameters.get("gaps", [])
        if str(gap.get("channel", "")).lower() in {"frames", "motion"}
    ]
    previous_hist = None
    previous_gray = None
    shot_starts = [0]
    sampled = 0
    frame_index = 0
    previous_timestamp = -1
    sample_stride = max(1, int(round(fps * 0.5)))
    vlm_frames: list[Any] = []
    vlm_timestamps: list[int] = []
    last_vlm_timestamp = -15 * TICKS_PER_SECOND
    ocr = None
    if os.environ.get("KADR_OCR_ENABLED", "").lower() in {"1", "true", "yes"}:
        try:
            import pytesseract
            if os.environ.get("KADR_TESSERACT_EXE"):
                pytesseract.pytesseract.tesseract_cmd = os.environ["KADR_TESSERACT_EXE"]
            ocr = pytesseract
        except ImportError:
            ocr = None
    while True:
        ok, frame = capture.read()
        if not ok:
            break
        position_ms = float(capture.get(cv2.CAP_PROP_POS_MSEC) or 0.0)
        pts_timestamp = int(position_ms / 1000.0 * TICKS_PER_SECOND)
        fallback_timestamp = int(frame_index / fps * TICKS_PER_SECOND)
        timestamp = pts_timestamp if frame_index == 0 or pts_timestamp > previous_timestamp else fallback_timestamp
        timestamp = max(0, min(source_duration, timestamp))
        previous_timestamp = timestamp
        dense = any(start <= timestamp <= end for start, end in dense_ranges)
        if not dense and frame_index % sample_stride:
            frame_index += 1
            continue
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        histogram = cv2.calcHist([frame], [0, 1], None, [24, 24], [0, 256, 0, 256])
        cv2.normalize(histogram, histogram)
        edge = timestamp <= 360 * TICKS_PER_SECOND or timestamp >= source_duration - 360 * TICKS_PER_SECOND
        vlm_interval = (15 if edge else 45) * TICKS_PER_SECOND
        if (parameters.get("profile") == "anime-episode" and
                timestamp - last_vlm_timestamp >= vlm_interval):
            vlm_frames.append(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
            vlm_timestamps.append(timestamp)
            last_vlm_timestamp = timestamp
            if ocr is not None:
                data = ocr.image_to_data(frame, output_type=ocr.Output.DICT)
                tokens = [str(value).strip() for value in data.get("text", []) if str(value).strip()]
                confidences = [float(value) for value in data.get("conf", []) if str(value) not in {"", "-1"}]
                if tokens:
                    frame_duration = max(1, int(TICKS_PER_SECOND / fps))
                    confidence = min(0.99, max(0.5, (sum(confidences) / max(1, len(confidences))) / 100))
                    facts.append(_fact(
                        str(parameters["sourceId"]), timestamp, frame_duration,
                        CHANNEL_OCR, 2, "Measured OCR text: " + " ".join(tokens)[:240],
                        confidence, "video-understanding",
                        measurements={"token_count": str(len(tokens))}))
                    intervals[CHANNEL_OCR].append(
                        _interval(timestamp, frame_duration, 1, False, "video-understanding"))
        if previous_hist is not None:
            distance = float(cv2.compareHist(previous_hist, histogram, cv2.HISTCMP_BHATTACHARYYA))
            motion = float(np.mean(cv2.absdiff(previous_gray, gray))) / 255.0
            if distance >= 0.48:
                shot_starts.append(timestamp)
                facts.append(_fact(str(parameters["sourceId"]), timestamp, max(1, int(TICKS_PER_SECOND / fps)),
                                   CHANNEL_FRAMES, 3, "Measured shot boundary", min(0.99, 0.6 + distance / 2),
                                   "video-understanding", measurements={"histogram_distance": f"{distance:.6f}"}))
            facts.append(_fact(str(parameters["sourceId"]), timestamp, max(1, int(0.5 * TICKS_PER_SECOND)),
                               CHANNEL_MOTION, 1, "Measured frame motion", 0.95,
                               "video-understanding", measurements={"motion": f"{motion:.6f}"}))
        previous_hist, previous_gray = histogram, gray
        sampled += 1
        frame_index += 1
    capture.release()
    intervals[CHANNEL_FRAMES].append(_interval(0, source_duration, sampled, False, "video-understanding"))
    intervals[CHANNEL_MOTION].append(_interval(0, source_duration, sampled, False, "video-understanding"))
    for start, end in dense_ranges:
        duration = max(1, min(source_duration, end) - max(0, start))
        dense_samples = max(1, int(duration / TICKS_PER_SECOND * fps))
        intervals[CHANNEL_FRAMES].append(_interval(max(0, start), duration, dense_samples, True, "video-understanding"))
        intervals[CHANNEL_MOTION].append(_interval(max(0, start), duration, dense_samples, True, "video-understanding"))
    channels = [CHANNEL_FRAMES, CHANNEL_MOTION] + ([CHANNEL_OCR] if intervals[CHANNEL_OCR] else [])
    result = empty_index(parameters, "video-understanding", channels)
    result["facts"] = facts
    result["coverage"] = _coverage(str(parameters["sourceId"]), str(parameters["sourceFingerprint"]), source_duration, intervals)
    result["shots"] = [{
        "id": str(uuid.uuid4()), "sourceId": str(parameters["sourceId"]),
        "sourceRange": _range(start, max(1, (shot_starts[index + 1] if index + 1 < len(shot_starts) else source_duration) - start)),
        "momentIds": [], "factIds": [fact["id"] for fact in facts if fact["kind"] == 3 and fact["sourceRange"]["start"]["ticks"] == start],
        "summary": "Measured shot", "camera": "", "composition": "", "embeddingReference": "",
    } for index, start in enumerate(shot_starts) if start < source_duration]
    if parameters.get("profile") == "anime-episode" and vlm_frames:
        role_facts, hypotheses = classify_anime_segments(
            vlm_frames, vlm_timestamps, shot_starts, parameters, assets)
        result["facts"].extend(role_facts)
        result["segmentRoleHypotheses"] = hypotheses
    return result


def _select_structural_segments(segments: list[dict[str, Any]], duration_seconds: float) -> list[dict[str, Any]]:
    """Collapse overlapping VLM proposals before the dense boundary probe."""
    valid = [item.copy() for item in segments
             if str(item.get("role", "")) in {"Opening", "Ending", "PostCredits", "Preview", "Recap", "SponsorCard"}
             and float(item.get("end_seconds", 0)) > float(item.get("start_seconds", 0))]
    openings = [item for item in valid if item["role"] == "Opening"
                and float(item["start_seconds"]) < min(360.0, duration_seconds / 2)]
    later_openings = [item for item in openings if float(item["start_seconds"]) >= 30.0]
    if later_openings:
        openings = later_openings
    openings.sort(key=lambda item: float(item["start_seconds"]))
    if openings:
        merged = openings[0].copy()
        for item in openings[1:]:
            if float(item["start_seconds"]) - float(merged["end_seconds"]) <= 30.0:
                merged["end_seconds"] = max(float(merged["end_seconds"]), float(item["end_seconds"]))
                merged["confidence"] = max(float(merged["confidence"]), float(item["confidence"]))
            elif float(item["confidence"]) > float(merged["confidence"]):
                merged = item.copy()
        openings = [merged]
    endings = [item for item in valid if item["role"] == "Ending"
               and float(item["start_seconds"]) >= max(duration_seconds / 2, duration_seconds - 210.0)]
    if endings:
        endings = [max(endings, key=lambda item: (
            float(item["end_seconds"]), float(item["confidence"])))]
    protected = [item for item in valid if item["role"] in {"PostCredits", "Preview"}]
    return openings + endings + protected


def _refine_music_segments(segments: list[dict[str, Any]], assets: list[Asset],
                           parameters: dict[str, Any]) -> list[dict[str, Any]]:
    """Refine coarse OP/ED spans with independent audio-boundary evidence."""
    try:
        import numpy as np
        import soundfile as sf
    except ImportError:
        return segments
    primary = parameters.get("primaryAsrStreamIndex")
    chunks = sorted((item for item in assets if item.kind == "audio-chunk"
                     and (primary is None or item.stream_index == primary)), key=lambda item: item.order)
    if not chunks:
        return segments
    sample_rate = 0
    decoded: list[Any] = []
    for chunk in chunks:
        values, current_rate = sf.read(str(chunk.path), dtype="float32", always_2d=False)
        if getattr(values, "ndim", 1) > 1:
            values = np.mean(values, axis=1)
        if sample_rate and current_rate != sample_rate:
            return segments
        sample_rate = current_rate
        decoded.append(values)
    if not sample_rate or not decoded:
        return segments
    samples = np.concatenate(decoded)
    squares = np.square(samples, dtype=np.float64)
    cumulative = np.concatenate((np.zeros(1, dtype=np.float64), np.cumsum(squares)))

    def db_at(seconds: float) -> float:
        radius = max(1, int(sample_rate * 0.5))
        center = int(seconds * sample_rate)
        left = max(0, center - radius)
        right = min(len(samples), center + radius)
        energy = float(cumulative[right] - cumulative[left]) / max(1, right - left)
        return 20.0 * math.log10(max(math.sqrt(energy), 1e-8))

    refined: list[dict[str, Any]] = []
    for item in segments:
        if item.get("role") not in {"Opening", "Ending"}:
            refined.append(item)
            continue
        coarse_start = float(item["start_seconds"])
        coarse_end = float(item["end_seconds"])
        best: tuple[float, float, float] | None = None
        start_min = max(0.0, coarse_start - 30.0)
        start_max = min(coarse_end, coarse_start + 45.0)
        end_min = max(coarse_start, coarse_end - 35.0)
        end_max = min(len(samples) / sample_rate, coarse_end + 15.0)
        start_step = math.ceil(start_min * 2.0) / 2.0
        end_step = math.ceil(end_min * 2.0) / 2.0
        start_count = max(0, int((start_max - start_step) * 2.0) + 1)
        end_count = max(0, int((end_max - end_step) * 2.0) + 1)
        for start_index in range(start_count):
            start = start_step + start_index * 0.5
            for end_index in range(end_count):
                end = end_step + end_index * 0.5
                length = end - start
                if not 88.0 <= length <= 94.0:
                    continue
                quiet_score = -db_at(start) - db_at(end)
                duration_score = -abs(length - 90.0) * 1.75
                proximity_score = -(abs(start - coarse_start) + abs(end - coarse_end)) * 0.08
                score = quiet_score + duration_score + proximity_score
                if best is None or score > best[0]:
                    best = (score, start, end)
        if best is None:
            refined.append(item)
            continue
        adjusted = item.copy()
        # RMS is measured in a one-second centered window. Its quietest center
        # trails the separator onset; use the preceding whole-second boundary
        # for the segment start and the first whole second of the end valley.
        adjusted_start = max(0.0, math.floor(best[1] - 1.0))
        adjusted_end = max(adjusted_start + 0.5, math.floor(best[2]))
        adjusted["start_seconds"] = adjusted_start
        adjusted["end_seconds"] = adjusted_end
        adjusted["audio_start_dbfs"] = db_at(best[1])
        adjusted["audio_end_dbfs"] = db_at(best[2])
        refined.append(adjusted)
    return refined


def _classify_anime_segments_gguf(
        frames: list[Any], timestamps: list[int], shot_starts: list[int],
        parameters: dict[str, Any], assets: list[Asset]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    try:
        import cv2
        import requests
        from jsonschema import validate as validate_json
    except ImportError as exception:
        raise CapabilityUnavailable("llama.cpp multimodal client dependencies are unavailable") from exception
    payload_segments: list[dict[str, Any]] = []
    output_schema = {
        "type": "object",
        "properties": {
            "segments": {
                "type": "array",
                "maxItems": 8,
                "items": {
                    "type": "object",
                    "properties": {
                        "role": {"type": "string", "enum": [
                            "Opening", "Ending", "PostCredits", "Preview", "Recap", "SponsorCard"]},
                        "start_seconds": {"type": "number", "minimum": 0},
                        "end_seconds": {"type": "number", "minimum": 0},
                        "confidence": {"type": "number", "minimum": 0, "maximum": 1},
                    },
                    "required": ["role", "start_seconds", "end_seconds", "confidence"],
                    "additionalProperties": False,
                },
            },
        },
        "required": ["segments"],
        "additionalProperties": False,
    }
    source_duration = int(parameters["sourceDurationTicks"])
    session = requests.Session()
    session.trust_env = False
    for batch_frames, batch_timestamps, batch_hint in _anime_frame_batches(
            frames, timestamps, source_duration):
        timestamp_list = ", ".join(f"image {index + 1}={ticks / TICKS_PER_SECOND:.3f}s"
                                   for index, ticks in enumerate(batch_timestamps))
        prompt = (
            "The ordered images are measured, position-balanced samples from one complete anime episode. "
            f"The full episode duration is {source_duration / TICKS_PER_SECOND:.3f}s. {batch_hint} "
            "Classify only clearly evidenced structural sections. Opening and Ending are musical credit sequences. "
            "Never call ordinary story dialogue/action before the theme song an Opening: that is a cold open and "
            "must remain EpisodeBody. A common anime Opening begins after a 1-5 minute cold open at a hard transition "
            "to a stylized song montage with staff/production credits. Do not mislabel that montage as Recap. "
            "The Ending begins when the main story transitions into the ending song/credits, not at the first sampled "
            "credit frame. It ends at the first return to ordinary dialogue or next-episode material. "
            "PostCredits and Preview are protected content: keep them separate and never include them in Ending. "
            "Position is only a prior; visible title, credits, preview cards or repeated musical imagery are required. "
            "Propose coarse boundaries; a deterministic dense frame probe will refine them. Timestamps: " + timestamp_list
        )
        content: list[dict[str, Any]] = [{"type": "text", "text": prompt}]
        for frame in batch_frames:
            height, width = frame.shape[:2]
            maximum = max(height, width)
            if maximum > 768:
                factor = 768.0 / maximum
                frame = cv2.resize(frame, (max(2, int(width * factor)), max(2, int(height * factor))),
                                   interpolation=cv2.INTER_AREA)
            bgr = cv2.cvtColor(frame, cv2.COLOR_RGB2BGR)
            ok, encoded = cv2.imencode(".jpg", bgr, [cv2.IMWRITE_JPEG_QUALITY, 82])
            if not ok:
                raise RuntimeError("Could not encode a measured frame for Qwen3-VL")
            uri = "data:image/jpeg;base64," + base64.b64encode(encoded.tobytes()).decode("ascii")
            content.append({"type": "image_url", "image_url": {"url": uri}})
        request = {
            "model": "kadr-vision",
            "messages": [
                {"role": "system", "content": "You are a conservative anime structure classifier."},
                {"role": "user", "content": content},
            ],
            "temperature": 0,
            "seed": 3407,
            "max_tokens": 768,
            "stream": False,
            "response_format": {
                "type": "json_schema",
                "json_schema": {"name": "anime_segments", "strict": True, "schema": output_schema},
            },
        }
        with _LLAMA_INFERENCE_LOCK:
            response = session.post(
                _llama_endpoint() + "/v1/chat/completions", json=request,
                timeout=(30, max(120, int(os.environ.get("KADR_LLAMA_INFERENCE_TIMEOUT", "1800")))))
        if response.status_code != 200:
            raise RuntimeError(f"Qwen3-VL GGUF failed with HTTP {response.status_code}: {response.text[:500]}")
        raw = str(response.json()["choices"][0]["message"]["content"]).strip()
        payload = json.loads(raw)
        validate_json(instance=payload, schema=output_schema)
        payload_segments.extend(item for item in payload.get("segments", []) if isinstance(item, dict))
    payload_segments = _select_structural_segments(
        payload_segments, source_duration / TICKS_PER_SECOND)
    payload_segments = _refine_music_segments(payload_segments, assets, parameters)
    role_values = {"Opening": 1, "Ending": 2, "PostCredits": 4, "Preview": 5, "Recap": 6, "SponsorCard": 7}
    source_duration = int(parameters["sourceDurationTicks"])

    def snap(seconds: float) -> tuple[int, float]:
        requested = max(0, min(source_duration, int(seconds * TICKS_PER_SECOND)))
        nearest = min(shot_starts, key=lambda value: abs(value - requested)) if shot_starts else requested
        distance = abs(nearest - requested)
        return (nearest, 1.0 if distance <= 2 * TICKS_PER_SECOND else 0.6)

    facts: list[dict[str, Any]] = []
    hypotheses: list[dict[str, Any]] = []
    for item in payload_segments:
        role_name = str(item.get("role", ""))
        if role_name not in role_values:
            continue
        if "audio_start_dbfs" in item and "audio_end_dbfs" in item:
            start = max(0, min(source_duration, int(float(item.get("start_seconds", 0)) * TICKS_PER_SECOND)))
            end = max(0, min(source_duration, int(float(item.get("end_seconds", 0)) * TICKS_PER_SECOND)))
            start_measurement = end_measurement = 0.9
        else:
            start, start_measurement = snap(float(item.get("start_seconds", 0)))
            end, end_measurement = snap(float(item.get("end_seconds", 0)))
        if end <= start:
            continue
        confidence = max(0.0, min(1.0, float(item.get("confidence", 0))))
        role_fact = _fact(str(parameters["sourceId"]), start, end - start, CHANNEL_FRAMES, 0,
                          f"Qwen3-VL classified {role_name}", confidence, "video-understanding",
                          measurements={
                              "segment_role": role_name,
                              "audio_start_dbfs": f"{float(item.get('audio_start_dbfs', 0)):.4f}",
                              "audio_end_dbfs": f"{float(item.get('audio_end_dbfs', 0)):.4f}",
                          })
        facts.append(role_fact)
        evidence_fact_ids = [role_fact["id"]]
        evidence_channels = [CHANNEL_FRAMES]
        if "audio_start_dbfs" in item and "audio_end_dbfs" in item:
            audio_fact = _fact(
                str(parameters["sourceId"]), start, end - start, CHANNEL_AUDIO, 8,
                f"Measured music boundaries support {role_name}", confidence,
                "video-understanding",
                measurements={
                    "segment_role": role_name,
                    "start_dbfs": f"{float(item['audio_start_dbfs']):.4f}",
                    "end_dbfs": f"{float(item['audio_end_dbfs']):.4f}",
                })
            facts.append(audio_fact)
            evidence_fact_ids.append(audio_fact["id"])
            evidence_channels.append(CHANNEL_AUDIO)
        start_boundary = {
            "id": str(uuid.uuid4()), "sourceId": str(parameters["sourceId"]), "time": _time(start),
            "confidence": min(confidence, start_measurement), "evidenceFactIds": evidence_fact_ids,
        }
        end_boundary = {
            "id": str(uuid.uuid4()), "sourceId": str(parameters["sourceId"]), "time": _time(end),
            "confidence": min(confidence, end_measurement), "evidenceFactIds": evidence_fact_ids,
        }
        hypotheses.append({
            "id": str(uuid.uuid4()), "sourceId": str(parameters["sourceId"]),
            "sourceRange": _range(start, end - start), "role": role_values[role_name],
            "confidence": confidence, "startBoundary": start_boundary, "endBoundary": end_boundary,
            "evidenceFactIds": evidence_fact_ids, "evidenceChannels": evidence_channels,
            "analyzerId": "video-understanding", "analyzerVersion": "2",
        })
    return facts, hypotheses


def classify_anime_segments(frames: list[Any], timestamps: list[int], shot_starts: list[int],
                            parameters: dict[str, Any], assets: list[Asset]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    if _reasoning_backend() not in {"llama.cpp", "llama-cpp", "gguf"}:
        raise CapabilityUnavailable("Only pre-quantized GGUF vision inference is allowed")
    return _classify_anime_segments_gguf(frames, timestamps, shot_starts, parameters, assets)


def analyze_audio(assets: list[Asset], parameters: dict[str, Any]) -> dict[str, Any]:
    try:
        import numpy as np
        import soundfile as sf
    except ImportError as exception:
        raise CapabilityUnavailable("soundfile/numpy production dependencies are unavailable") from exception
    result = empty_index(parameters, "audio-events", [CHANNEL_AUDIO])
    facts: list[dict[str, Any]] = []
    events: list[dict[str, Any]] = []
    intervals: list[dict[str, Any]] = []
    for asset in sorted((item for item in assets if item.kind == "audio-chunk"), key=lambda item: (item.stream_index or -1, item.order)):
        audio_info = sf.info(str(asset.path))
        sample_rate = audio_info.samplerate
        state_window = max(1, int(sample_rate * 2.0))
        sample_count = 0
        square_sum = 0.0
        for block in sf.blocks(str(asset.path), blocksize=state_window, dtype="float32", always_2d=False):
            if getattr(block, "ndim", 1) > 1:
                block = np.mean(block, axis=1)
            if len(block) == 0:
                continue
            block_start = sample_count
            sample_count += len(block)
            square_sum += float(np.sum(np.square(block, dtype=np.float64)))
            block_rms = float(np.sqrt(np.mean(np.square(block)) + 1e-12))
            block_db = 20 * math.log10(max(block_rms, 1e-8))
            zero_crossing = float(np.mean(np.abs(np.diff(np.signbit(block)))))
            spectrum = np.abs(np.fft.rfft(block * np.hanning(len(block)))) + 1e-10
            flatness = float(np.exp(np.mean(np.log(spectrum))) / np.mean(spectrum))
            music_like = block_db > -42 and flatness < 0.18 and zero_crossing > 0.015
            block_ticks = asset.start_ticks + int(block_start / sample_rate * TICKS_PER_SECOND)
            facts.append(_fact(
                str(parameters["sourceId"]), block_ticks, 2 * TICKS_PER_SECOND,
                CHANNEL_AUDIO, 8 if music_like else 9,
                "Measured music-like audio state" if music_like else "Measured speech/noise audio state",
                0.72 if music_like else 0.68, "audio-events", asset.stream_index,
                {"dbfs": f"{block_db:.4f}", "spectral_flatness": f"{flatness:.6f}",
                 "zero_crossing": f"{zero_crossing:.6f}"}))
        duration = int(sample_count / sample_rate * TICKS_PER_SECOND)
        loudness = float(20 * np.log10(max(math.sqrt(square_sum / max(1, sample_count)), 1e-8)))
        summary = "Measured active audio" if loudness > -45 else "Measured silence/near-silence"
        facts.append(_fact(
            str(parameters["sourceId"]), asset.start_ticks, max(1, duration), CHANNEL_AUDIO, 10,
            summary, 0.98, "audio-events", asset.stream_index,
            {"integrated_dbfs": f"{loudness:.4f}", "sample_rate": str(sample_rate)}))
        events.append({
            "id": str(uuid.uuid4()), "sourceId": str(parameters["sourceId"]),
            "sourceRange": _range(asset.start_ticks, max(1, duration)), "kind": "audio-activity",
            "confidence": 0.98, "measurements": {"integratedLufs": loudness},
            "streamIndex": asset.stream_index,
        })
        intervals.append(_interval(asset.start_ticks, max(1, duration), max(1, sample_count), True, "audio-events"))
    result["facts"], result["audioEvents"] = facts, events
    result["coverage"] = _coverage(str(parameters["sourceId"]), str(parameters["sourceFingerprint"]),
                                    int(parameters["sourceDurationTicks"]), {CHANNEL_AUDIO: intervals})
    return result


def analyze_asr(assets: list[Asset], parameters: dict[str, Any]) -> dict[str, Any]:
    try:
        from faster_whisper import WhisperModel
    except ImportError as exception:
        raise CapabilityUnavailable("faster-whisper is unavailable") from exception
    model_path = os.environ.get("KADR_ASR_MODEL")
    if not model_path:
        raise CapabilityUnavailable("KADR_ASR_MODEL is not configured")
    language = os.environ.get("KADR_ASR_LANGUAGE", "ru")
    model = WhisperModel(model_path, device="cuda", compute_type="int8_float16")
    primary = parameters.get("primaryAsrStreamIndex")
    selected = [item for item in assets if item.kind == "audio-chunk" and (primary is None or item.stream_index == primary)]
    result = empty_index(parameters, "asr-align", [CHANNEL_TRANSCRIPT])
    words: list[dict[str, Any]] = []
    facts: list[dict[str, Any]] = []
    intervals: list[dict[str, Any]] = []
    for asset in sorted(selected, key=lambda item: item.order):
        segments, _ = model.transcribe(
            str(asset.path), language=language, beam_size=5,
            word_timestamps=True, vad_filter=True)
        count = 0
        max_end = 0
        for segment in segments:
            for word in segment.words or []:
                if word.start is None or word.end is None:
                    continue
                start_seconds = float(word.start)
                end_seconds = float(word.end)
                start = asset.start_ticks + int(start_seconds * TICKS_PER_SECOND)
                duration = max(1, int((end_seconds - start_seconds) * TICKS_PER_SECOND))
                confidence = float(word.probability or 0.5)
                item = {
                    "id": str(uuid.uuid4()), "sourceId": str(parameters["sourceId"]),
                    "sourceRange": _range(start, duration), "text": str(word.word or "").strip(),
                    "confidence": confidence, "speakerId": "", "streamIndex": asset.stream_index,
                }
                if not item["text"]:
                    continue
                words.append(item)
                facts.append(_fact(str(parameters["sourceId"]), start, duration, CHANNEL_TRANSCRIPT, 5,
                                   item["text"], item["confidence"], "asr-align", asset.stream_index))
                count += 1
                max_end = max(max_end, int(end_seconds * TICKS_PER_SECOND))
        if max_end:
            intervals.append(_interval(asset.start_ticks, max_end, max(1, count), True, "asr-align"))
    result["facts"], result["transcriptWords"] = facts, words
    result["coverage"] = _coverage(str(parameters["sourceId"]), str(parameters["sourceFingerprint"]),
                                    int(parameters["sourceDurationTicks"]), {CHANNEL_TRANSCRIPT: intervals})
    return result


def analyze_diarization(assets: list[Asset], parameters: dict[str, Any]) -> dict[str, Any]:
    try:
        import torch
        from pyannote.audio import Pipeline
    except ImportError as exception:
        raise CapabilityUnavailable("pyannote.audio is unavailable") from exception
    model_path = os.environ.get("KADR_DIARIZATION_MODEL")
    if not model_path:
        raise CapabilityUnavailable("KADR_DIARIZATION_MODEL is not configured")
    pipeline = Pipeline.from_pretrained(model_path)
    if pipeline is None:
        raise CapabilityUnavailable("local pyannote pipeline could not be loaded")
    pipeline.to(torch.device("cuda"))
    primary = parameters.get("primaryAsrStreamIndex")
    selected = [item for item in assets if item.kind == "audio-chunk" and
                (primary is None or item.stream_index == primary)]
    result = empty_index(parameters, "diarization", [CHANNEL_AUDIO])
    turns: list[dict[str, Any]] = []
    facts: list[dict[str, Any]] = []
    intervals: list[dict[str, Any]] = []
    for asset in sorted(selected, key=lambda item: item.order):
        annotation = pipeline(str(asset.path))
        max_end = 0.0
        for turn, _, speaker in annotation.itertracks(yield_label=True):
            start = asset.start_ticks + int(float(turn.start) * TICKS_PER_SECOND)
            duration = max(1, int(float(turn.end - turn.start) * TICKS_PER_SECOND))
            speaker_id = str(speaker)
            turns.append({
                "id": str(uuid.uuid4()), "sourceId": str(parameters["sourceId"]),
                "sourceRange": _range(start, duration), "speakerId": speaker_id,
                "text": "", "confidence": 0.9, "words": [], "streamIndex": asset.stream_index,
            })
            facts.append(_fact(
                str(parameters["sourceId"]), start, duration, CHANNEL_AUDIO, 6,
                f"Measured speaker turn {speaker_id}", 0.9, "diarization", asset.stream_index,
                {"speaker_id": speaker_id}))
            max_end = max(max_end, float(turn.end))
        if max_end > 0:
            duration = int(max_end * TICKS_PER_SECOND)
            intervals.append(_interval(asset.start_ticks, duration, max(1, int(max_end * 100)), True, "diarization"))
    result["facts"], result["speakerTurns"] = facts, turns
    result["coverage"] = _coverage(
        str(parameters["sourceId"]), str(parameters["sourceFingerprint"]),
        int(parameters["sourceDurationTicks"]), {CHANNEL_AUDIO: intervals})
    return result


def analyze_embedding(assets: list[Asset], parameters: dict[str, Any], data_root: Path) -> dict[str, Any]:
    try:
        import numpy as np
        import onnxruntime as ort
        from tokenizers import Tokenizer
    except ImportError as exception:
        raise CapabilityUnavailable("ONNX Runtime/tokenizers are unavailable") from exception
    model_path = os.environ.get("KADR_EMBEDDING_MODEL")
    if not model_path:
        raise CapabilityUnavailable("KADR_EMBEDDING_MODEL is not configured")
    documents = parameters.get("documents", [])
    if not isinstance(documents, list) or not documents:
        raise ValueError("embedding needs a non-empty documents corpus")
    ids = [str(item["id"]) for item in documents]
    texts = [str(item.get("text", "")).strip() for item in documents]
    if any(not value for value in texts) or len(ids) != len(set(ids)):
        raise ValueError("embedding documents need unique ids and non-empty text")
    query = str(parameters.get("query", "")).strip()
    if not query:
        raise ValueError("embedding query is required")
    model_root = Path(model_path)
    onnx_path = model_root / "onnx" / "model_quint8_avx2.onnx"
    tokenizer_path = model_root / "tokenizer.json"
    if not onnx_path.is_file() or not tokenizer_path.is_file():
        raise CapabilityUnavailable("The pinned MiniLM INT8 ONNX files are incomplete")
    tokenizer = Tokenizer.from_file(str(tokenizer_path))
    tokenizer.enable_truncation(max_length=128)
    tokenizer.enable_padding(length=None)
    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])

    def encode(values: list[str]) -> Any:
        encoded = tokenizer.encode_batch(values)
        input_ids = np.asarray([item.ids for item in encoded], dtype=np.int64)
        attention = np.asarray([item.attention_mask for item in encoded], dtype=np.int64)
        available = {item.name for item in session.get_inputs()}
        inputs: dict[str, Any] = {"input_ids": input_ids, "attention_mask": attention}
        if "token_type_ids" in available:
            inputs["token_type_ids"] = np.asarray([item.type_ids for item in encoded], dtype=np.int64)
        token_embeddings = session.run(None, inputs)[0]
        mask = attention[:, :, None].astype(np.float32)
        vectors = (token_embeddings * mask).sum(axis=1) / np.maximum(mask.sum(axis=1), 1e-9)
        return vectors / np.maximum(np.linalg.norm(vectors, axis=1, keepdims=True), 1e-12)
    vector_root = data_root / "vectors"
    vector_root.mkdir(parents=True, exist_ok=True)
    corpus_identity = {"model": model_path, "documents": [{"id": id_, "text": text_} for id_, text_ in zip(ids, texts)]}
    key = hashlib.sha256(json.dumps(corpus_identity, sort_keys=True).encode()).hexdigest()
    vector_path = vector_root / f"{key}.npy"
    metadata_path = vector_root / f"{key}.json"
    if vector_path.is_file() and metadata_path.is_file():
        cached_ids = json.loads(metadata_path.read_text(encoding="utf-8"))["ids"]
        if cached_ids != ids:
            raise ValueError("vector cache metadata does not match corpus")
        vectors = np.load(vector_path)
    else:
        vectors = encode(texts)
        np.save(vector_path, vectors)
        metadata_path.write_text(json.dumps({"ids": ids}, separators=(",", ":")), encoding="utf-8")
    query_vector = encode([query])[0]
    scores = vectors @ query_vector
    top_k = max(1, min(int(parameters.get("topK", 128)), len(ids)))
    order = np.argsort(-scores)[:top_k]
    return {
        "kind": "semantic-search",
        "vectorArtifactReference": f"vectors/{key}.npy",
        "hits": [{"id": ids[int(index)], "score": float(scores[int(index)])} for index in order],
    }


def _analyze_llama_reasoning(parameters: dict[str, Any], analyzer: str) -> dict[str, Any]:
    try:
        import requests
        from jsonschema import validate as validate_json
    except ImportError as exception:
        raise CapabilityUnavailable("llama.cpp client dependencies are unavailable") from exception
    schema = parameters.get("schema")
    if not isinstance(schema, dict):
        raise ValueError("structured reasoning requires a JSON schema object")
    system = str(parameters.get("system", "")).strip()
    user = str(parameters.get("user", "")).strip()
    if not system or not user:
        raise ValueError("structured reasoning requires bounded system and user prompts")
    payload = {
        "model": "kadr-planner",
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": user},
        ],
        "temperature": 0,
        "seed": 3407,
        "max_tokens": max(32, min(int(parameters.get("maxTokens", 4096)), 8192)),
        "stream": False,
        "response_format": {
            "type": "json_schema",
            "json_schema": {"name": "kadr_output", "strict": True, "schema": schema},
        },
    }
    session = requests.Session()
    session.trust_env = False
    with _LLAMA_INFERENCE_LOCK:
        response = session.post(
            _llama_endpoint() + "/v1/chat/completions",
            json=payload,
            timeout=(30, max(120, int(os.environ.get("KADR_LLAMA_INFERENCE_TIMEOUT", "1800")))),
        )
    if response.status_code != 200:
        raise RuntimeError(f"llama.cpp reasoning failed with HTTP {response.status_code}: {response.text[:500]}")
    answer = response.json()
    raw = str(answer["choices"][0]["message"]["content"]).strip()
    content = json.loads(raw)
    validate_json(instance=content, schema=schema)
    return {
        "kind": "structured-reasoning",
        "roleWorker": analyzer,
        "content": content,
        "attemptCount": 1,
    }


def analyze_reasoning(parameters: dict[str, Any], analyzer: str) -> dict[str, Any]:
    if _reasoning_backend() not in {"llama.cpp", "llama-cpp", "gguf"}:
        raise CapabilityUnavailable("Only pre-quantized GGUF reasoning is allowed")
    return _analyze_llama_reasoning(parameters, analyzer)


def run_analyzer(analyzer: str, assets: list[Asset], parameters: dict[str, Any], data_root: Path) -> dict[str, Any]:
    if analyzer == "video-understanding":
        return analyze_video(assets, parameters)
    if analyzer == "audio-events":
        return analyze_audio(assets, parameters)
    if analyzer == "asr-align":
        return analyze_asr(assets, parameters)
    if analyzer == "embedding":
        return analyze_embedding(assets, parameters, data_root)
    if analyzer == "diarization":
        return analyze_diarization(assets, parameters)
    if analyzer in {"director", "critic"}:
        return analyze_reasoning(parameters, analyzer)
    raise ValueError(f"unknown analyzer {analyzer}")


def count_tokens(model: str, value: str) -> int:
    if _reasoning_backend() not in {"llama.cpp", "llama-cpp", "gguf"}:
        raise CapabilityUnavailable("Only the GGUF tokenizer endpoint is allowed")
    try:
        import requests
    except ImportError as exception:
        raise CapabilityUnavailable("llama.cpp tokenizer client is unavailable") from exception
    session = requests.Session()
    session.trust_env = False
    with _LLAMA_INFERENCE_LOCK:
        response = session.post(
            _llama_endpoint() + "/tokenize",
            json={"content": value, "add_special": True},
            timeout=(30, 120))
    if response.status_code != 200:
        raise RuntimeError(f"llama.cpp tokenize failed with HTTP {response.status_code}")
    tokens = response.json().get("tokens", [])
    if not isinstance(tokens, list):
        raise RuntimeError("llama.cpp returned an invalid token list")
    return len(tokens)
