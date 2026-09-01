from __future__ import annotations

import argparse
import base64
import json
import os
import subprocess
import sys
from pathlib import Path


def arguments() -> argparse.Namespace:
    project = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description="Sequential offline smoke for the pinned GGUF model pack")
    parser.add_argument("--ai-root", type=Path, default=project / ".kadr-ai")
    parser.add_argument("--role", choices=("vision", "planner"))
    parser.add_argument("--skip-vision", action="store_true")
    parser.add_argument("--skip-planner", action="store_true")
    return parser.parse_args()


def contained(root: Path, child: Path, label: str, directory: bool = False) -> Path:
    root, child = root.resolve(), child.resolve()
    if root != child and root not in child.parents:
        raise RuntimeError(f"{label} escaped the project AI root")
    if not (child.is_dir() if directory else child.is_file()):
        raise RuntimeError(f"{label} is unavailable: {child}")
    return child


def environment(ai_root: Path, role: str) -> dict[str, str]:
    models = contained(ai_root, ai_root / "models", "model store", directory=True)
    llama_candidates = list((ai_root / "runtime" / "llama.cpp").rglob("llama-server.exe"))
    if len(llama_candidates) != 1:
        raise RuntimeError("expected exactly one project-local llama-server.exe")
    data = ai_root / "data"
    data.mkdir(parents=True, exist_ok=True)
    values = os.environ.copy()
    values.update({
        "KADR_REASONING_BACKEND": "llama.cpp",
        "KADR_LLAMA_SERVER": str(llama_candidates[0]),
        "KADR_AI_RUNTIME_DATA_ROOT": str(data),
        "KADR_WORKER_ANALYZER": "video-understanding" if role == "vision" else "director",
        "KADR_VISION_MODEL": str(contained(models, models / "qwen3-vl-8b-instruct-q4-k-m" /
                                           "Qwen3VL-8B-Instruct-Q4_K_M.gguf", "vision GGUF")),
        "KADR_VISION_MMPROJ": str(contained(models, models / "qwen3-vl-8b-instruct-q4-k-m" /
                                            "mmproj-Qwen3VL-8B-Instruct-Q8_0.gguf", "vision projector")),
        "KADR_DIRECTOR_MODEL": str(contained(models, models / "qwen3-30b-a3b-instruct-2507-q4-k-m" /
                                             "Qwen3-30B-A3B-Instruct-2507-Q4_K_M.gguf", "planner GGUF")),
        "KADR_VISION_CONTEXT_TOKENS": "8192", "KADR_VISION_GPU_LAYERS": "99",
        "KADR_PLANNER_CONTEXT_TOKENS": "16384", "KADR_LLAMA_GPU_LAYERS": "12", "PYTHONUTF8": "1",
    })
    return values


def run_role(project: Path, ai_root: Path, role: str) -> None:
    os.environ.update(environment(ai_root, role))
    sys.path.insert(0, str(project / "workers" / "python"))
    from kadr_worker import analyzers

    schema = {"type": "object", "properties": {"status": {"type": "string", "const": "ok"}},
              "required": ["status"], "additionalProperties": False}
    if role == "planner":
        response = analyzers.analyze_reasoning({
            "schema": schema, "system": "Return the required smoke-test JSON only.",
            "user": "Confirm constrained local inference.", "maxTokens": 32}, "director")
        if response.get("content") != {"status": "ok"}:
            raise RuntimeError(f"unexpected planner result: {response}")
    else:
        import cv2
        import numpy as np
        import requests
        ok, encoded = cv2.imencode(".jpg", np.full((48, 48, 3), 127, dtype=np.uint8))
        if not ok:
            raise RuntimeError("could not encode vision smoke frame")
        payload = {
            "model": "kadr-vision", "temperature": 0, "max_tokens": 32, "stream": False,
            "messages": [{"role": "user", "content": [
                {"type": "text", "text": "Return the required JSON only."},
                {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," +
                    base64.b64encode(encoded.tobytes()).decode("ascii")}}]}],
            "response_format": {"type": "json_schema", "json_schema": {
                "name": "kadr_smoke", "strict": True, "schema": schema}}}
        session = requests.Session()
        session.trust_env = False
        response = session.post(analyzers._llama_endpoint() + "/v1/chat/completions", json=payload, timeout=(30, 900))
        response.raise_for_status()
        content = json.loads(response.json()["choices"][0]["message"]["content"])
        if content != {"status": "ok"}:
            raise RuntimeError(f"unexpected vision result: {content}")
    print(json.dumps({"role": role, "status": "ok"}, separators=(",", ":")))


def main() -> None:
    args = arguments()
    project = Path(__file__).resolve().parent.parent
    ai_root = args.ai_root.resolve()
    if args.role:
        run_role(project, ai_root, args.role)
        return
    results = []
    for role, skipped in (("vision", args.skip_vision), ("planner", args.skip_planner)):
        if skipped:
            continue
        process = subprocess.run(
            [sys.executable, str(Path(__file__).resolve()), "--ai-root", str(ai_root), "--role", role],
            check=True, capture_output=True, text=True, encoding="utf-8", env=environment(ai_root, role))
        results.append(json.loads(process.stdout.strip().splitlines()[-1]))
    print(json.dumps({"sequential": True, "results": results}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
