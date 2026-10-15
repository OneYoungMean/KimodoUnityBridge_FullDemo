"""Optional motion rendering shared with the UniMate visualization pipeline.

The QuickServer owns the protocol and model runtime.  UniMate owns the
matplotlib skeleton renderer, so this module only adapts the server's posed
joint arrays to that renderer and encodes the returned frames as MP4 bytes.
"""

from __future__ import annotations

import io
import os
from pathlib import Path
import sys
from typing import Sequence

import numpy as np


def _unimate_root() -> Path:
    configured = str(os.environ.get("KIMODO_UNIMATE_ROOT") or "").strip()
    candidates = [Path(configured)] if configured else []
    candidates.append(Path(__file__).resolve().parents[1] / "vendor" / "UniMate")
    # Support both a normal checkout (QuickServer~/core -> KimodoUnityBridge
    # -> nvlab -> UniMate) and a managed worktree (…/worktrees/<name>/
    # QuickServer~/core -> nvlab -> UniMate).
    source_path = Path(__file__).resolve()
    candidates.extend(
        (
            source_path.parents[3] / "UniMate",
            source_path.parents[4] / "UniMate",
        )
    )
    for candidate in candidates:
        plotting = candidate / "data_process" / "utils" / "plotting.py"
        if plotting.is_file():
            return candidate.resolve()
    raise RuntimeError(
        "UniMate plotting code was not found. Set KIMODO_UNIMATE_ROOT to the UniMate checkout."
    )


def _load_plotting():
    root = str(_unimate_root())
    if root not in sys.path:
        sys.path.insert(0, root)
    try:
        from data_process.utils import plotting
    except Exception as exc:
        raise RuntimeError(
            "UniMate MP4 rendering requires matplotlib, imageio, and imageio-ffmpeg."
        ) from exc
    return plotting


def render_skeleton_mp4(
    positions: np.ndarray,
    parents: Sequence[int],
    fps: float,
    *,
    title: str | None = None,
) -> bytes:
    """Render ``(frames, joints, xyz)`` positions using UniMate's renderer."""
    points = np.asarray(positions, dtype=np.float32)
    if points.ndim != 3 or points.shape[0] < 1 or points.shape[1] < 1 or points.shape[2] < 3:
        raise ValueError(
            f"positions must have shape (frames, joints, >=3), got {points.shape!r}."
        )
    if not np.isfinite(points).all():
        raise ValueError("positions contains NaN or Infinity.")

    parent_values = tuple(int(parent) for parent in parents)
    if len(parent_values) != points.shape[1]:
        raise ValueError(
            f"parents has {len(parent_values)} entries but positions has {points.shape[1]} joints."
        )
    if any(parent < -1 or parent >= len(parent_values) or parent == index
           for index, parent in enumerate(parent_values)):
        raise ValueError("parents contains an invalid self, out-of-range, or negative parent index.")

    try:
        fps_value = float(fps)
    except (TypeError, ValueError) as exc:
        raise ValueError("fps must be a finite positive number.") from exc
    if not np.isfinite(fps_value) or fps_value <= 0.0:
        raise ValueError("fps must be a finite positive number.")

    plotting = _load_plotting()
    frames = plotting.render_skeleton_motion(
        parent_values,
        points[..., :3],
        title=title,
        figsize=(6.4, 6.4),
        dpi=100,
    )
    import imageio.v2 as imageio

    payload = io.BytesIO()
    imageio.mimwrite(payload, frames, format="mp4", fps=max(1, int(round(fps_value))))
    result = payload.getvalue()
    if not result:
        raise RuntimeError("UniMate MP4 renderer returned an empty payload.")
    return result
