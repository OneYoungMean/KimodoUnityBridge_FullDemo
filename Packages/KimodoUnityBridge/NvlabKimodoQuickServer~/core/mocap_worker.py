from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from typing import Any


BODY_JOINTS = [
    "left_hip", "right_hip", "spine1", "left_knee", "right_knee", "spine2",
    "left_ankle", "right_ankle", "spine3", "left_foot", "right_foot", "neck",
    "left_collar", "right_collar", "head", "left_shoulder", "right_shoulder",
    "left_elbow", "right_elbow", "left_wrist", "right_wrist",
]


def _make_still_video(image_path: Path, output_path: Path) -> None:
    # shortcut: still-image inference repeats one frame for one second; replace when GVHMR exposes a still-image path.
    import cv2

    image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
    if image is None:
        raise RuntimeError(f"Could not decode image: {image_path}")
    height, width = image.shape[:2]
    if width < 2 or height < 2:
        raise RuntimeError("Image dimensions must be at least 2x2 pixels.")
    if width % 2 or height % 2:
        image = cv2.copyMakeBorder(image, 0, height % 2, 0, width % 2, cv2.BORDER_REPLICATE)
        height, width = image.shape[:2]

    writer = cv2.VideoWriter(
        str(output_path), cv2.VideoWriter_fourcc(*"mp4v"), 24.0, (width, height)
    )
    if not writer.isOpened():
        raise RuntimeError("OpenCV could not create a temporary video from the picture.")
    try:
        for _ in range(24):
            writer.write(image)
    finally:
        writer.release()


def _numbers(value: Any) -> list[float]:
    import numpy as np

    array = np.asarray(value, dtype=np.float32)
    if not np.isfinite(array).all():
        raise RuntimeError("GVHMR returned a non-finite pose value.")
    return array.tolist()


def _optional_numbers(value: Any) -> list[float | None]:
    import numpy as np

    array = np.asarray(value, dtype=np.float32)
    return [float(item) if np.isfinite(item) else None for item in array.reshape(-1)]


def _build_result(kind: str, npz_path: Path, person: str | None, sampled_video_frame: int | None) -> dict[str, Any]:
    import numpy as np

    with np.load(npz_path, allow_pickle=False) as data:
        body_pose = np.asarray(data["body_pose"], dtype=np.float32)
        global_orient = np.asarray(data["global_orient"], dtype=np.float32)
        translation = np.asarray(data["transl"], dtype=np.float32)
        fps = float(np.asarray(data["fps"]).reshape(-1)[0])
        betas = _numbers(data["betas"])
        rest_joints = _numbers(data["rest_joints"])
        source_frame_count = int(body_pose.shape[0])
        if body_pose.ndim != 2 or body_pose.shape[1] != 63:
            raise RuntimeError(f"Expected SMPL-X body_pose shape (frames, 63), got {body_pose.shape}.")
        if source_frame_count < 1:
            raise RuntimeError("GVHMR returned no pose frames.")
        if not np.isfinite(fps) or fps <= 0:
            raise RuntimeError(f"GVHMR returned an invalid frame rate: {fps}.")
        if global_orient.shape != (source_frame_count, 3) or translation.shape != (source_frame_count, 3):
            raise RuntimeError("GVHMR returned inconsistent SMPL-X frame arrays.")

        if kind == "picture":
            index = source_frame_count // 2
            selected = [index]
        else:
            selected = list(range(source_frame_count))

        poses = []
        static_conf = data["static_conf"] if "static_conf" in data.files else None
        for output_frame, source_frame in enumerate(selected):
            pose = {
                "frame": output_frame,
                "source_frame": int(sampled_video_frame if sampled_video_frame is not None else source_frame),
                "timestamp_seconds": float(source_frame / fps) if fps > 0 else 0.0,
                "global_orient_axis_angle": _numbers(global_orient[source_frame]),
                "body_pose_axis_angle": _numbers(body_pose[source_frame]),
                "translation_m": _numbers(translation[source_frame]),
            }
            if "incam_global_orient" in data.files:
                pose["incam_global_orient_axis_angle"] = _numbers(data["incam_global_orient"][source_frame])
                pose["incam_body_pose_axis_angle"] = _numbers(data["incam_body_pose"][source_frame])
                pose["incam_translation_m"] = _numbers(data["incam_transl"][source_frame])
            if static_conf is not None:
                pose["static_confidence"] = _optional_numbers(static_conf[source_frame])
            poses.append(pose)

    result = {
        "status": "done",
        "analysis_schema_version": "smplx_pose_array_v1",
        "source_type": kind,
        "skeleton": "SMPL-X",
        "fps": fps,
        "source_frame_count": source_frame_count,
        "frame_count": len(poses),
        "joint_names": BODY_JOINTS,
        "pose_parameterization": {
            "global_orient": "axis-angle radians",
            "body_pose": "21 local joint axis-angle triples in joint_names order",
            "translation": "meters",
        },
        "coordinate_spaces": {
            "global": "gravity-aligned, Y-up, meters",
            "incam": "OpenCV camera, X-right/Y-down/Z-forward, meters",
        },
        "betas": betas,
        "rest_joints_m": rest_joints,
        "poses": poses,
    }
    if person is not None:
        result["person"] = person
    return result


def analyze(kind: str, media: Path, mocap_root: Path, person: str | None = None) -> dict[str, Any]:
    import numpy as np

    estimator = mocap_root / "pipeline" / "estimate_pose_gvhmr.py"
    with tempfile.TemporaryDirectory(prefix="kimodo-mocap-") as temp_text:
        temp = Path(temp_text)
        video = media
        sampled_frame = None
        if kind == "picture":
            video = temp / "still.mp4"
            _make_still_video(media, video)
            sampled_frame = 12
        smplx_path = temp / "smplx.npz"
        landmarks_path = temp / "landmarks.json"
        command = [
            sys.executable, str(estimator), "--video", str(video),
            "--out", str(landmarks_path), "--smplx-out", str(smplx_path), "--smplx-only",
        ]
        if person is not None:
            command.extend(("--person", person))
        completed = subprocess.run(
            command,
            cwd=str(mocap_root),
            capture_output=True,
            text=True,
            check=False,
        )
        if completed.returncode != 0:
            detail = (completed.stderr or completed.stdout or "GVHMR exited without diagnostics").strip()
            raise RuntimeError(detail[-4000:])
        if not smplx_path.is_file():
            raise RuntimeError("GVHMR completed without writing smplx.npz.")
        with np.load(smplx_path, allow_pickle=False) as data:
            source_frame_count = int(data["body_pose"].shape[0])
            if kind == "picture" and source_frame_count > 0:
                sampled_frame = source_frame_count // 2
        return _build_result(kind, smplx_path, person, sampled_frame)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--kind", choices=("video", "picture"), required=True)
    parser.add_argument("--media", type=Path, required=True)
    parser.add_argument("--mocap-root", type=Path, required=True)
    parser.add_argument("--person", choices=("left", "right"))
    args = parser.parse_args()
    result = analyze(args.kind, args.media, args.mocap_root, args.person)
    sys.stdout.write(json.dumps(result, separators=(",", ":"), allow_nan=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
