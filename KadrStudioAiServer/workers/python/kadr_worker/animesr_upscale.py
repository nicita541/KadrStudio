"""Frame-exact, video-only AnimeSR-X runner used by Kadr Studio."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import time
from collections.abc import Mapping
from fractions import Fraction
from pathlib import Path
from typing import Any

import cv2
import numpy as np
import torch
from torch import nn
from torch.nn import functional as F


MODEL_SHA256 = "5302034b463cded6497eb2c53e7e6be22d3e4f16bed190d7cfff4415ff81c548"
KADR_TICKS_PER_SECOND = 240_000


class ResidualBlockNoBN(nn.Module):
    def __init__(self, num_feat: int = 64, res_scale: float = 1.0):
        super().__init__()
        self.res_scale = res_scale
        self.conv1 = nn.Conv2d(num_feat, num_feat, 3, 1, 1, bias=True)
        self.conv2 = nn.Conv2d(num_feat, num_feat, 3, 1, 1, bias=True)
        self.relu = nn.ReLU(inplace=True)

    def forward(self, value: torch.Tensor) -> torch.Tensor:
        residual = self.conv2(self.relu(self.conv1(value)))
        return value + residual * self.res_scale


class RightAlignMSConvResidualBlocks(nn.Module):
    def __init__(self, num_in_ch: int = 3, num_state_ch: int = 64,
                 num_out_ch: int = 64, num_block: tuple[int, int, int] = (5, 3, 2)):
        super().__init__()
        self.num_block = num_block
        self.conv_s1_first = nn.Sequential(
            nn.Conv2d(num_in_ch, num_state_ch, 3, 1, 1, bias=True), nn.LeakyReLU(0.1, inplace=True))
        self.conv_s2_first = nn.Sequential(
            nn.Conv2d(num_state_ch, num_state_ch, 3, 2, 1, bias=True), nn.LeakyReLU(0.1, inplace=True))
        self.conv_s4_first = nn.Sequential(
            nn.Conv2d(num_state_ch, num_state_ch, 3, 2, 1, bias=True), nn.LeakyReLU(0.1, inplace=True))
        self.body_s1_first = nn.ModuleList([ResidualBlockNoBN(num_state_ch) for _ in range(num_block[0])])
        self.body_s2_first = nn.ModuleList([ResidualBlockNoBN(num_state_ch) for _ in range(num_block[1])])
        self.body_s4_first = nn.ModuleList([ResidualBlockNoBN(num_state_ch) for _ in range(num_block[2])])
        self.upsample_x2 = nn.Upsample(scale_factor=2, mode="bilinear", align_corners=False)
        self.upsample_x4 = nn.Upsample(scale_factor=4, mode="bilinear", align_corners=False)
        self.fusion = nn.Sequential(
            nn.Conv2d(3 * num_state_ch, 2 * num_out_ch, 3, 1, 1, bias=True),
            nn.LeakyReLU(0.1, inplace=True),
            nn.Conv2d(2 * num_out_ch, num_out_ch, 3, 1, 1, bias=True))

    def up(self, value: torch.Tensor | int, scale: int = 2) -> torch.Tensor | int:
        if isinstance(value, int):
            return value
        return self.upsample_x2(value) if scale == 2 else self.upsample_x4(value)

    def forward(self, value: torch.Tensor) -> torch.Tensor:
        s1 = self.conv_s1_first(value)
        s2 = self.conv_s2_first(s1)
        s4 = self.conv_s4_first(s2)
        flag_s2 = flag_s4 = False
        for index in range(self.num_block[0]):
            s1 = self.body_s1_first[index](
                s1 + (self.up(s2, 2) if flag_s2 else 0) + (self.up(s4, 4) if flag_s4 else 0))
            if index >= self.num_block[0] - self.num_block[1]:
                s2 = self.body_s2_first[index - self.num_block[0] + self.num_block[1]](
                    s2 + (self.up(s4, 2) if flag_s4 else 0))
                flag_s2 = True
            if index >= self.num_block[0] - self.num_block[2]:
                s4 = self.body_s4_first[index - self.num_block[0] + self.num_block[2]](s4)
                flag_s4 = True
        return self.fusion(torch.cat((s1, self.upsample_x2(s2), self.upsample_x4(s4)), dim=1))


class MSRSWVSR(nn.Module):
    def __init__(self):
        super().__init__()
        self.num_feat = 64
        self.netscale = 4
        self.recurrent_cell = RightAlignMSConvResidualBlocks(3 * 3 + 3 * 16 + 64, 64, 64 + 3 * 16, (5, 3, 2))
        self.lrelu = nn.LeakyReLU(0.1)
        self.pixel_shuffle = nn.PixelShuffle(4)

    def cell(self, value: torch.Tensor, previous: torch.Tensor,
             state: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        residual = value[:, 3:6]
        packed = torch.cat((value, F.pixel_unshuffle(previous, 4), state), dim=1)
        output = self.recurrent_cell(packed)
        image = self.pixel_shuffle(output[:, :48]) + F.interpolate(
            residual, scale_factor=4, mode="bilinear", align_corners=False)
        return image, self.lrelu(output[:, 48:])


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--ffmpeg", required=True, type=Path)
    parser.add_argument("--ffprobe", required=True, type=Path)
    parser.add_argument("--outscale", choices=("auto", "2", "4"), default="auto")
    parser.add_argument("--start-ticks", type=int, default=0)
    parser.add_argument("--duration-ticks", type=int, required=True)
    parser.add_argument("--tile", type=int, default=0,
                        help="Source tile size; 0 selects it from currently free VRAM")
    parser.add_argument("--overlap", type=int, default=32)
    parser.add_argument("--crf", type=int, default=14)
    return parser.parse_args()


def probe(path: Path, ffprobe: Path, count_frames: bool = False) -> dict[str, Any]:
    command = [str(ffprobe), "-v", "error"]
    if count_frames:
        command.append("-count_frames")
    command += ["-show_streams", "-show_format", "-of", "json", str(path)]
    result = subprocess.run(command, check=True, capture_output=True, text=True, encoding="utf-8")
    payload = json.loads(result.stdout)
    video = next(item for item in payload["streams"] if item.get("codec_type") == "video")
    return {
        "width": int(video["width"]), "height": int(video["height"]),
        "rate": video.get("avg_frame_rate") or video.get("r_frame_rate") or "24/1",
        "frames": int(video.get("nb_read_frames") or video.get("nb_frames") or 0),
        "audio": any(item.get("codec_type") == "audio" for item in payload["streams"]),
    }


def load_model(path: Path, device: torch.device) -> MSRSWVSR:
    checkpoint = torch.load(path, map_location="cpu", weights_only=True)
    if not isinstance(checkpoint, Mapping) or not checkpoint or not all(
            isinstance(value, torch.Tensor) for value in checkpoint.values()):
        raise ValueError("AnimeSR-X checkpoint must be a plain state dict")
    model = MSRSWVSR()
    model.load_state_dict(checkpoint, strict=True)
    return model.eval().to(device).half()


def tensor(frame: np.ndarray, device: torch.device) -> torch.Tensor:
    rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
    return torch.from_numpy(rgb.copy()).permute(2, 0, 1).unsqueeze(0).to(device).half().div_(255)


def tiles(width: int, height: int, size: int, overlap: int) -> list[tuple[int, int, int, int, int, int, int, int]]:
    result = []
    for y in range(0, height, size):
        for x in range(0, width, size):
            x1, y1 = min(width, x + size), min(height, y + size)
            px0, py0 = max(0, x - overlap), max(0, y - overlap)
            px1, py1 = min(width, x1 + overlap), min(height, y1 + overlap)
            result.append((x, y, x1, y1, px0, py0, px1, py1))
    return result


def encoder_command(ffmpeg: Path, output: Path, width: int, height: int,
                    rate: str, crf: int) -> list[str]:
    encoders = subprocess.run(
        [str(ffmpeg), "-hide_banner", "-encoders"], capture_output=True, text=True,
        encoding="utf-8", errors="replace").stdout
    if "hevc_nvenc" in encoders:
        codec = ["-c:v", "hevc_nvenc", "-preset", "p6", "-tune", "hq",
                 "-rc:v", "vbr", "-cq:v", str(crf), "-b:v", "0",
                 "-profile:v", "main10", "-pix_fmt", "p010le", "-tag:v", "hvc1"]
    elif "libx265" in encoders:
        codec = ["-c:v", "libx265", "-preset", "medium", "-crf", str(crf),
                 "-pix_fmt", "yuv420p10le", "-tag:v", "hvc1"]
    else:
        codec = ["-c:v", "libx264", "-preset", "medium", "-crf", str(crf), "-pix_fmt", "yuv420p"]
    return [str(ffmpeg), "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgr24", "-s:v", f"{width}x{height}",
            "-r", rate, "-i", "-", "-an", *codec, str(output)]


@torch.inference_mode()
def run(args: argparse.Namespace) -> None:
    source = args.input.resolve()
    output = args.output.resolve()
    metadata = probe(source, args.ffprobe)
    width, height = metadata["width"], metadata["height"]
    scale = min(4.0, max(1.0, 2160.0 / height)) if args.outscale == "auto" else float(args.outscale)
    out_width = max(2, int(round(width * scale)) // 2 * 2)
    out_height = max(2, int(round(height * scale)) // 2 * 2)
    device = torch.device("cuda:0" if torch.cuda.is_available() else "cpu")
    if device.type != "cuda":
        raise RuntimeError("AnimeSR-X production integration requires CUDA")
    model = load_model(args.model.resolve(), device)
    free_vram, total_vram = torch.cuda.mem_get_info(device)
    tile_size = args.tile if args.tile > 0 else (
        1024 if free_vram >= 5 * 1024 ** 3 else
        768 if free_vram >= 3 * 1024 ** 3 else
        512 if free_vram >= 1536 * 1024 ** 2 else 384)
    tile_size = max(128, tile_size)
    print("KADR_DIAGNOSTIC " + json.dumps({
        "device": torch.cuda.get_device_name(device),
        "freeVramMiB": round(free_vram / 1024 ** 2),
        "totalVramMiB": round(total_vram / 1024 ** 2),
        "tile": tile_size,
        "encoder": "hevc_nvenc" if "hevc_nvenc" in subprocess.run(
            [str(args.ffmpeg), "-hide_banner", "-encoders"], capture_output=True,
            text=True, encoding="utf-8", errors="replace").stdout else "software",
    }, separators=(",", ":")), flush=True)
    rate = Fraction(metadata["rate"])
    total = max(1, round(args.duration_ticks / KADR_TICKS_PER_SECOND * float(rate)))
    decoder = subprocess.Popen([
        str(args.ffmpeg), "-hide_banner", "-loglevel", "error", "-i", str(source),
        "-ss", f"{args.start_ticks / KADR_TICKS_PER_SECOND:.9f}",
        "-t", f"{args.duration_ticks / KADR_TICKS_PER_SECOND:.9f}",
        "-map", "0:v:0", "-an", "-sn", "-dn", "-vsync", "0",
        "-f", "rawvideo", "-pix_fmt", "bgr24", "-",
    ], stdout=subprocess.PIPE)
    if decoder.stdout is None:
        raise RuntimeError("Could not open FFmpeg decoder pipe")
    frame_bytes = width * height * 3

    def read_frame() -> np.ndarray | None:
        payload = decoder.stdout.read(frame_bytes)
        if not payload:
            return None
        if len(payload) != frame_bytes:
            raise RuntimeError("FFmpeg returned a truncated raw frame")
        return np.frombuffer(payload, dtype=np.uint8).reshape(height, width, 3).copy()
    part = output.with_name(output.name + ".kadr-part.mp4")
    part.parent.mkdir(parents=True, exist_ok=True)
    process = subprocess.Popen(
        encoder_command(args.ffmpeg, part, out_width, out_height, metadata["rate"], args.crf),
        stdin=subprocess.PIPE)
    if process.stdin is None:
        raise RuntimeError("Could not open FFmpeg encoder pipe")
    first = read_frame()
    if first is None:
        raise OSError("Input clip contains no decodable frames")
    second = read_frame()
    has_future = second is not None
    previous, current, following = first, first, second if has_future else first
    layout = tiles(width, height, tile_size, max(8, args.overlap))
    states: dict[int, tuple[torch.Tensor, torch.Tensor]] = {}
    started = time.monotonic()
    written = 0
    try:
        while True:
            source_triplet = [tensor(frame, device) for frame in (previous, current, following)]
            output_tensor = source_triplet[0].new_empty(1, 3, out_height, out_width)
            for index, (x0, y0, x1, y1, px0, py0, px1, py1) in enumerate(layout):
                frame_triplet = [frame[:, :, py0:py1, px0:px1] for frame in source_triplet]
                source_patch_height, source_patch_width = py1 - py0, px1 - px0
                padded_height = (source_patch_height + 3) // 4 * 4
                padded_width = (source_patch_width + 3) // 4 * 4
                if padded_height != source_patch_height or padded_width != source_patch_width:
                    frame_triplet = [F.pad(value, (0, padded_width - source_patch_width,
                                                   0, padded_height - source_patch_height), mode="replicate")
                                     for value in frame_triplet]
                patch_height, patch_width = padded_height, padded_width
                old_output, state = states.get(index, (
                    frame_triplet[0].new_zeros(1, 3, patch_height * 4, patch_width * 4),
                    frame_triplet[0].new_zeros(1, 64, patch_height, patch_width)))
                old_output, state = model.cell(torch.cat(frame_triplet, dim=1), old_output, state)
                states[index] = old_output, state
                core = old_output[:, :, (y0 - py0) * 4:(y1 - py0) * 4,
                                  (x0 - px0) * 4:(x1 - px0) * 4]
                ox0, oy0 = int(round(x0 * scale)), int(round(y0 * scale))
                ox1, oy1 = int(round(x1 * scale)), int(round(y1 * scale))
                if core.shape[-2:] != (oy1 - oy0, ox1 - ox0):
                    core = F.interpolate(core, size=(oy1 - oy0, ox1 - ox0),
                                         mode="bicubic", align_corners=False)
                output_tensor[:, :, oy0:oy1, ox0:ox1] = core
            bgr = (output_tensor[0, [2, 1, 0]].clamp_(0, 1).mul_(255).round_()
                    .to(torch.uint8).permute(1, 2, 0).cpu().numpy())
            process.stdin.write(bgr.tobytes())
            written += 1
            if written == 1 or written % 10 == 0:
                elapsed = max(0.001, time.monotonic() - started)
                eta = (total - written) * elapsed / written if total > written else 0
                print("KADR_PROGRESS " + json.dumps({
                    "frames": written, "totalFrames": total,
                    "progress": written / total if total else 0, "etaSeconds": eta,
                }, separators=(",", ":")), flush=True)
            if not has_future:
                break
            previous, current = current, following
            next_frame = read_frame()
            following, has_future = (next_frame, True) if next_frame is not None else (current, False)
    finally:
        decoder.stdout.close()
        if decoder.wait() != 0:
            raise RuntimeError("FFmpeg decoder failed")
        process.stdin.close()
    if process.wait() != 0:
        raise RuntimeError("FFmpeg encoder failed")
    verified = probe(part, args.ffprobe, count_frames=True)
    if verified["frames"] != written:
        raise RuntimeError(f"Frame verification failed: encoded={verified['frames']} expected={written}")
    if Fraction(verified["rate"]) != Fraction(metadata["rate"]):
        raise RuntimeError(f"FPS verification failed: {verified['rate']} != {metadata['rate']}")
    if verified["audio"]:
        raise RuntimeError("AnimeSR-X derivative unexpectedly contains audio")
    os.replace(part, output)
    print("KADR_RESULT " + json.dumps({
        "frames": written, "width": out_width, "height": out_height,
        "fps": metadata["rate"], "modelSha256": MODEL_SHA256,
    }, separators=(",", ":")), flush=True)


def main() -> int:
    args = parse_args()
    try:
        run(args)
        return 0
    except Exception as exception:
        print(f"KADR_ERROR {type(exception).__name__}: {exception}", flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
