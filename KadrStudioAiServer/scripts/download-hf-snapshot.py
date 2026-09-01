from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import sys
import time
from pathlib import Path, PurePosixPath

import requests
from huggingface_hub import HfApi, get_hf_file_metadata, hf_hub_url


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Resumable exact-file Hugging Face downloader")
    parser.add_argument("--repository", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--target", required=True, type=Path)
    parser.add_argument("--chunk-mib", type=int, default=64)
    parser.add_argument("--retries", type=int, default=12)
    parser.add_argument("--expected-size", required=True, type=int)
    parser.add_argument("--sha256", required=True)
    parser.add_argument(
        "--include", action="append", default=[],
        help="Download only an exact repository-relative file; may be repeated",
    )
    return parser.parse_args()


def safe_target(root: Path, repository_file: str) -> Path:
    relative = PurePosixPath(repository_file)
    if relative.is_absolute() or ".." in relative.parts:
        raise RuntimeError(f"Unsafe repository path: {repository_file}")
    target = (root / Path(*relative.parts)).resolve()
    if target != root and root not in target.parents:
        raise RuntimeError(f"Repository path escaped target: {repository_file}")
    return target


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(8 * 1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def retry_network(operation, label: str, retries: int):
    for attempt in range(1, retries + 2):
        try:
            return operation()
        except (requests.RequestException, OSError) as exception:
            if attempt > retries:
                raise RuntimeError(f"Network operation failed: {label}") from exception
            delay = min(30, 2 ** min(attempt, 5))
            print(f"  retry {attempt}/{retries}: {label}: {exception}", file=sys.stderr, flush=True)
            time.sleep(delay)
    raise AssertionError("unreachable")


def download_file(
    session: requests.Session,
    repository: str,
    revision: str,
    repository_file: str,
    target: Path,
    chunk_bytes: int,
    retries: int,
    manifest_size: int,
    manifest_sha256: str,
) -> None:
    url = hf_hub_url(repository, repository_file, revision=revision)
    metadata = retry_network(
        lambda: get_hf_file_metadata(url, timeout=30),
        f"metadata {repository_file}",
        retries,
    )
    if metadata.size is None or metadata.size < 0:
        raise RuntimeError(f"Missing size metadata for {repository_file}")
    expected_size = int(metadata.size)
    expected_etag = (metadata.etag or "").strip('"').lower()
    if expected_size != manifest_size:
        raise RuntimeError(
            f"Pinned manifest size mismatch for {repository_file}: "
            f"manifest={manifest_size}, remote={expected_size}")
    if len(manifest_sha256) != 64 or any(character not in "0123456789abcdef" for character in manifest_sha256):
        raise RuntimeError(f"Invalid manifest SHA-256 for {repository_file}")
    if len(expected_etag) == 64 and expected_etag != manifest_sha256:
        raise RuntimeError(f"Pinned manifest SHA-256 no longer matches LFS metadata for {repository_file}")
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.name + ".kadr-part")

    if target.is_file() and target.stat().st_size == expected_size:
        if sha256(target) == manifest_sha256:
            print(f"skip {repository_file} ({expected_size / 1024 / 1024:.1f} MiB)", flush=True)
            return
        print(f"redownload {repository_file}: existing SHA-256 mismatch", file=sys.stderr, flush=True)
    if target.exists():
        target.unlink()
    if partial.exists() and partial.stat().st_size > expected_size:
        partial.unlink()
    downloaded = partial.stat().st_size if partial.exists() else 0
    print(
        f"download {repository_file}: {downloaded / 1024 / 1024:.1f}/{expected_size / 1024 / 1024:.1f} MiB",
        flush=True,
    )

    failures = 0
    while downloaded < expected_size:
        end = min(expected_size - 1, downloaded + chunk_bytes - 1)
        headers = {"Range": f"bytes={downloaded}-{end}", "Accept-Encoding": "identity"}
        try:
            with session.get(metadata.location, headers=headers, stream=True, timeout=(30, 120)) as response:
                if response.status_code == 200 and downloaded == 0 and expected_size <= chunk_bytes:
                    content_range = ""
                elif response.status_code == 206:
                    content_range = response.headers.get("Content-Range", "")
                    if not content_range.startswith(f"bytes {downloaded}-"):
                        raise RuntimeError(f"Unexpected Content-Range: {content_range}")
                else:
                    raise RuntimeError(f"Expected HTTP 206, received {response.status_code}")
                with partial.open("ab") as output:
                    for block in response.iter_content(chunk_size=1024 * 1024):
                        if block:
                            output.write(block)
                new_size = partial.stat().st_size
                if new_size <= downloaded or new_size > end + 1:
                    raise RuntimeError(f"Invalid partial size {new_size} after range {downloaded}-{end}")
                downloaded = new_size
                failures = 0
                percent = downloaded * 100 / max(1, expected_size)
                print(
                    f"  {repository_file}: {downloaded / 1024 / 1024:.1f} MiB ({percent:.1f}%)",
                    flush=True,
                )
        except (requests.RequestException, OSError, RuntimeError) as exception:
            failures += 1
            if partial.exists():
                downloaded = partial.stat().st_size
            if failures > retries:
                raise RuntimeError(f"Download failed for {repository_file} at byte {downloaded}") from exception
            metadata = retry_network(
                lambda: get_hf_file_metadata(url, timeout=30),
                f"refresh metadata {repository_file}",
                retries,
            )
            delay = min(30, 2 ** min(failures, 5))
            print(f"  retry {failures}/{retries} at byte {downloaded}: {exception}", file=sys.stderr, flush=True)
            time.sleep(delay)

    if partial.stat().st_size != expected_size:
        raise RuntimeError(f"Size mismatch for {repository_file}")
    actual_hash = sha256(partial)
    if actual_hash != manifest_sha256:
        partial.unlink()
        raise RuntimeError(f"SHA-256 mismatch for {repository_file}")
    os.replace(partial, target)


def main() -> None:
    args = parse_args()
    if len(args.revision) != 40 or any(character not in "0123456789abcdef" for character in args.revision.lower()):
        raise RuntimeError("Revision must be a pinned 40-character commit hash")
    if args.chunk_mib < 8 or args.chunk_mib > 256:
        raise RuntimeError("chunk-mib must be in range 8..256")
    if len(args.include) != 1:
        raise RuntimeError("Exactly one --include file is required; full snapshots are forbidden")
    args.sha256 = args.sha256.lower()
    target = args.target.resolve()
    target.mkdir(parents=True, exist_ok=True)
    api = HfApi()
    files = retry_network(
        lambda: api.list_repo_files(args.repository, revision=args.revision),
        f"list files {args.repository}@{args.revision}",
        args.retries,
    )
    if args.include:
        requested = set(args.include)
        available = set(files)
        missing = requested - available
        if missing:
            raise RuntimeError("Requested files are absent at the pinned revision: " + ", ".join(sorted(missing)))
        files = [repository_file for repository_file in files if repository_file in requested]
    session = requests.Session()
    session.headers.update({"User-Agent": "kadr-ai-model-installer/2.1"})
    for repository_file in files:
        download_file(
            session,
            args.repository,
            args.revision,
            repository_file,
            safe_target(target, repository_file),
            args.chunk_mib * 1024 * 1024,
            args.retries,
            args.expected_size,
            args.sha256,
        )
    cache_directory = target / ".cache"
    if cache_directory.is_dir():
        shutil.rmtree(cache_directory)
    print(f"snapshot complete: {args.repository}@{args.revision} -> {target}", flush=True)


if __name__ == "__main__":
    main()
