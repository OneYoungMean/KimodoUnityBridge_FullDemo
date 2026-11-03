"""2D foot keypoints (heel, toe tip) for a plate — what COCO-17 lacks.

  python pipeline/detect_feet.py --video plates/<p>/<p>.mp4 [--out plates/<p>/feet2d.npz]

GVHMR's detector (ViTPose, COCO-17) stops at the ankles, so nothing in
the estimate says which way a foot points or whether its heel is up —
and the retarget showed it: feet turned the wrong way, heels flat where
the performer stood on the ball of the foot. MediaPipe's Pose Landmarker
tracks the heel and the toe tip of each foot; refine_smplx.py fits the
SMPL-X feet to them.

Runs in any Python with `mediapipe` and `opencv-python` (it does not need
the GVHMR venv). The model (pose_landmarker_heavy.task) is looked up in
tools/models/, then downloaded from Google's model store if missing.
Solo plates only: one pose is tracked.

Writes feet2d.npz: `kp` (T, 33, 4) — x px, y px, visibility, presence —
for the MediaPipe landmarks, and `fps`. The refinement uses 27-32
(ankles, heels, toe tips), with ankles as a sanity check against ViTPose.
"""

from __future__ import annotations

import argparse
import urllib.request
from pathlib import Path

import cv2
import numpy as np

REPO = Path(__file__).resolve().parents[1]
MODEL_URL = ("https://storage.googleapis.com/mediapipe-models/pose_landmarker/"
             "pose_landmarker_heavy/float16/latest/pose_landmarker_heavy.task")
MODEL_PATHS = [REPO / "tools" / "models" / "pose_landmarker_heavy.task",
               REPO / "local" / "bible" / "scripts" / "models" / "pose_landmarker_heavy.task"]
# MediaPipe BlazePose landmark ids (person's own left/right)
FEET = {"L_ankle": 27, "R_ankle": 28, "L_heel": 29, "R_heel": 30, "L_toe": 31, "R_toe": 32}


def rpath(p) -> Path:
    p = Path(p)
    return p if p.is_absolute() else (REPO / p)


def _is_task_file(p: Path) -> bool:
    """A .task model is a zip archive; a truncated or zero-filled copy
    (a failed download, a partial restore) is not."""
    try:
        with open(p, "rb") as f:
            return f.read(2) == b"PK"
    except OSError:
        return False


def model_path(arg=None) -> Path:
    for p in ([rpath(arg)] if arg else []) + MODEL_PATHS:
        if p.exists() and _is_task_file(p):
            return p
        if p.exists():
            print(f"skipping {p}: not a valid .task archive")
    dst = MODEL_PATHS[0]
    dst.parent.mkdir(parents=True, exist_ok=True)
    print(f"downloading {MODEL_URL} -> {dst}")
    urllib.request.urlretrieve(MODEL_URL, dst)
    return dst


def detect(video: Path, model: Path) -> tuple[np.ndarray, float]:
    import mediapipe as mp
    from mediapipe.tasks import python as mp_python
    from mediapipe.tasks.python import vision

    opts = vision.PoseLandmarkerOptions(
        base_options=mp_python.BaseOptions(model_asset_path=str(model)),
        running_mode=vision.RunningMode.VIDEO, num_poses=1,
        min_pose_detection_confidence=0.5, min_pose_presence_confidence=0.5,
        min_tracking_confidence=0.5)
    cap = cv2.VideoCapture(str(video))
    fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
    W, H = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)), int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    out = []
    with vision.PoseLandmarker.create_from_options(opts) as lm:
        i = 0
        while True:
            ok, bgr = cap.read()
            if not ok:
                break
            img = mp.Image(image_format=mp.ImageFormat.SRGB, data=cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
            res = lm.detect_for_video(img, int(round(i * 1000.0 / fps)))
            kp = np.zeros((33, 4))
            if res.pose_landmarks:
                for k, p in enumerate(res.pose_landmarks[0]):
                    kp[k] = (p.x * W, p.y * H, p.visibility or 0.0, p.presence or 0.0)
            out.append(kp)
            i += 1
    cap.release()
    return np.stack(out), float(fps)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--video", required=True)
    ap.add_argument("--out", help="default: feet2d.npz next to the video")
    ap.add_argument("--model")
    args = ap.parse_args()
    video = rpath(args.video)
    out = rpath(args.out) if args.out else video.with_name("feet2d.npz")
    kp, fps = detect(video, model_path(args.model))
    np.savez_compressed(out, kp=kp, fps=np.array(fps))
    vis = kp[:, list(FEET.values()), 2]
    print(f"wrote {out}: {kp.shape[0]} frames, pose found on {(kp[:, 0, 3] > 0).mean() * 100:.0f}% "
          f"of them, mean foot visibility {vis.mean():.2f}")


if __name__ == "__main__":
    main()
