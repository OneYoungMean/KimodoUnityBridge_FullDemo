from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any

from .mocap_assets import ensure_gvhmr_assets

VIDEO_EXTENSIONS = {".mp4", ".mov", ".m4v", ".avi", ".mkv"}
PICTURE_EXTENSIONS = {".png", ".jpg", ".jpeg"}
MAX_MEDIA_BYTES = 2 * 1024**3
WORKER_TIMEOUT_SECONDS = 1800


def analyze_media(kind: str, media_path: str, person: str | None = None) -> dict[str, Any]:
    normalized_kind = str(kind or "").strip().lower()
    if normalized_kind not in {"video", "picture"}:
        raise ValueError("Analysis kind must be 'video' or 'picture'.")
    if person not in (None, "left", "right"):
        raise ValueError("person must be 'left' or 'right'.")
    if normalized_kind == "picture" and person is not None:
        raise ValueError("person is supported only for video analysis.")

    media = Path(str(media_path or "")).expanduser().resolve(strict=True)
    if not media.is_file():
        raise ValueError("media_path must point to a file.")
    extensions = VIDEO_EXTENSIONS if normalized_kind == "video" else PICTURE_EXTENSIONS
    if media.suffix.lower() not in extensions:
        raise ValueError(f"Unsupported {normalized_kind} file extension: {media.suffix or '<none>'}.")
    if media.stat().st_size > MAX_MEDIA_BYTES:
        raise ValueError(f"Media file exceeds the {MAX_MEDIA_BYTES}-byte limit.")

    bundled_mocap_root = Path(__file__).resolve().parents[1] / "vendor" / "mixamo-llm-mocap"
    mocap_root_text = os.environ.get("KIMODO_MOCAP_ROOT", "").strip()
    if not mocap_root_text and bundled_mocap_root.is_dir():
        mocap_root_text = str(bundled_mocap_root)
    if not mocap_root_text:
        raise RuntimeError(
            "Mixamo LLM Mocap source was not found. Set KIMODO_MOCAP_ROOT or reinstall QuickServer."
        )
    try:
        mocap_root = Path(mocap_root_text).expanduser().resolve(strict=True)
    except FileNotFoundError as exc:
        raise RuntimeError("KIMODO_MOCAP_ROOT directory was not found.") from exc
    estimator = mocap_root / "pipeline" / "estimate_pose_gvhmr.py"
    if not estimator.is_file():
        raise RuntimeError(f"GVHMR estimator was not found: {estimator}")

    configured_python = os.environ.get("KIMODO_MOCAP_PYTHON", "").strip()
    python = Path(configured_python).expanduser().resolve(strict=True) if configured_python else Path(sys.executable)
    if not python.is_file():
        raise RuntimeError(
            "GVHMR Python was not found. Set KIMODO_MOCAP_PYTHON or install GVHMR dependencies "
            "in the QuickServer environment."
        )

    worker = Path(__file__).with_name("mocap_worker.py")
    command = [
        str(python), str(worker), "--kind", normalized_kind,
        "--media", str(media), "--mocap-root", str(mocap_root),
    ]
    if person is not None:
        command.extend(("--person", person))
    environment = dict(os.environ)
    # shortcut: trust only checkpoints provisioned by ensure_gvhmr_assets; required for Torch 2.6+ YOLO pickles.
    environment["TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD"] = "1"
    runtime_root = Path(__file__).resolve().parents[1]
    gvhmmr_root = Path(environment.get("GVHMR_ROOT") or runtime_root / "vendor" / "GVHMR").resolve()
    environment["GVHMR_ROOT"] = str(gvhmmr_root)
    ensure_gvhmr_assets(gvhmmr_root)

    try:
        completed = subprocess.run(
            command,
            cwd=str(mocap_root),
            env=environment,
            capture_output=True,
            text=True,
            timeout=WORKER_TIMEOUT_SECONDS,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(f"Mocap analysis exceeded {WORKER_TIMEOUT_SECONDS} seconds.") from exc
    if completed.returncode != 0:
        diagnostic = (completed.stderr or completed.stdout or "worker exited without diagnostics").strip()
        raise RuntimeError(f"Mocap analysis failed: {diagnostic[-4000:]}")
    try:
        result = json.loads(completed.stdout)
    except (TypeError, json.JSONDecodeError) as exc:
        raise RuntimeError("Mocap worker returned invalid JSON.") from exc
    if not isinstance(result, dict) or result.get("status") != "done" or not isinstance(result.get("poses"), list):
        raise RuntimeError("Mocap worker returned an incomplete pose result.")
    return result


def error_code_for_exception(exc: Exception) -> str:
    message = str(exc).lower()
    if "kimodo_mocap_root" in message or "gvhmr python" in message or "estimator was not found" in message:
        return "mocap_not_configured"
    if isinstance(exc, FileNotFoundError):
        return "media_not_found"
    if isinstance(exc, ValueError):
        return "invalid_mocap_request"
    return "mocap_analysis_failed"
