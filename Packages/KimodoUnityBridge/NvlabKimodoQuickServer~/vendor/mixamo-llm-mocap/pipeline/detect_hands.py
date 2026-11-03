"""Hand keypoints (21 per hand) for a plate, from zoomed crops.

  python pipeline/detect_hands.py --video plates/<p>/<p>.mp4 [--kp plates/<p>/smplx.npz]
         [--out plates/<p>/hands2d.npz]

GVHMR estimates no fingers, and on a full-body 1280x720 plate a hand is
30-50 px: a hand detector run on the whole frame sees a blob. So each
hand is cropped around its wrist first — the ViTPose wrist and elbow of
the plate's estimate place and size the crop (the hand lies beyond the
wrist, along the forearm, within about one forearm length) — and the
crop is upscaled before MediaPipe's Hand Landmarker looks at it. The
landmarks are mapped back to the plate's pixels.

Runs in any Python with `mediapipe` and `opencv-python` (it does not need
the GVHMR venv). The model (hand_landmarker.task) is looked up in
tools/models/, then downloaded from Google's model store if missing.
Solo plates only.

Writes hands2d.npz, hands in (left, right) order — the performer's own:
  kp     (T, 2, 21, 3)  x px, y px, relative depth (px scale) in the plate
  world  (T, 2, 21, 3)  metric 3D landmarks, hand-centred, camera axes
  score  (T, 2)         handedness confidence of the kept detection, 0 = none
  crop   (T, 2, 3)      crop centre x, y and half-size, px
  fps
"""

from __future__ import annotations

import argparse
from pathlib import Path

import cv2
import numpy as np

import detect_feet as DF

MODEL_URL = ("https://storage.googleapis.com/mediapipe-models/hand_landmarker/"
             "hand_landmarker/float16/latest/hand_landmarker.task")
MODEL_PATHS = [DF.REPO / "tools" / "models" / "hand_landmarker.task"]
# ViTPose COCO-17: wrist, elbow per side (performer's left, right)
ARMS = ((9, 7), (10, 8))
CROP_PX = 256


def model_path(arg=None) -> Path:
    for p in ([DF.rpath(arg)] if arg else []) + MODEL_PATHS:
        if p.exists() and DF._is_task_file(p):
            return p
    dst = MODEL_PATHS[0]
    dst.parent.mkdir(parents=True, exist_ok=True)
    print(f"downloading {MODEL_URL} -> {dst}")
    import urllib.request
    urllib.request.urlretrieve(MODEL_URL, dst)
    return dst


def hand_boxes(kp2d: np.ndarray, min_conf: float = 0.3):
    """(T, 2, 3) crop centre and half-size per hand, NaN where the wrist is
    not seen. Centre: a third of a forearm beyond the wrist; half-size: 0.9
    forearm (at least 36 px). The forearm's screen length is smoothed over
    time — it shrinks when the arm points at the camera, and a crop that
    shrinks with it would cut the hand off."""
    T = kp2d.shape[0]
    out = np.full((T, 2, 3), np.nan)
    for k, (w, e) in enumerate(ARMS):
        fore = kp2d[:, w, :2] - kp2d[:, e, :2]
        length = np.linalg.norm(fore, axis=1)
        ok = (kp2d[:, w, 2] >= min_conf) & (kp2d[:, e, 2] >= min_conf)
        ref = np.percentile(length[ok], 75) if ok.any() else 60.0
        # never under 60 % of the arm's usual length on screen
        length = np.maximum(length, 0.6 * ref)
        c = kp2d[:, w, :2] + fore / np.maximum(np.linalg.norm(fore, axis=1, keepdims=True), 1e-6) * length[:, None] / 3.0
        half = np.maximum(0.9 * length, 36.0)
        out[ok, k, :2] = c[ok]
        out[ok, k, 2] = half[ok]
    return out


def crop(img: np.ndarray, cx: float, cy: float, half: float) -> np.ndarray:
    """Square crop, padded with edge pixels where it leaves the frame,
    upscaled to CROP_PX."""
    x0, y0, s = int(round(cx - half)), int(round(cy - half)), int(round(2 * half))
    H, W = img.shape[:2]
    pad = max(0, -x0, -y0, x0 + s - W, y0 + s - H)
    if pad:
        img = cv2.copyMakeBorder(img, pad, pad, pad, pad, cv2.BORDER_REPLICATE)
        x0, y0 = x0 + pad, y0 + pad
    return cv2.resize(img[y0:y0 + s, x0:x0 + s], (CROP_PX, CROP_PX), interpolation=cv2.INTER_CUBIC)


def detect(video: Path, kp2d: np.ndarray, model: Path):
    import mediapipe as mp
    from mediapipe.tasks import python as mp_python
    from mediapipe.tasks.python import vision

    opts = vision.HandLandmarkerOptions(
        base_options=mp_python.BaseOptions(model_asset_path=str(model)),
        running_mode=vision.RunningMode.IMAGE, num_hands=2,
        min_hand_detection_confidence=0.3, min_hand_presence_confidence=0.3)
    cap = cv2.VideoCapture(str(video))
    fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
    boxes = hand_boxes(kp2d)
    T = kp2d.shape[0]
    kp = np.zeros((T, 2, 21, 3))
    world = np.zeros((T, 2, 21, 3))
    score = np.zeros((T, 2))
    with vision.HandLandmarker.create_from_options(opts) as lm:
        for t in range(T):
            ok, bgr = cap.read()
            if not ok:
                break
            rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
            for k in range(2):
                cx, cy, half = boxes[t, k]
                if not np.isfinite(half):
                    continue
                img = mp.Image(image_format=mp.ImageFormat.SRGB, data=np.ascontiguousarray(crop(rgb, cx, cy, half)))
                res = lm.detect(img)
                if not res.hand_landmarks:
                    continue
                # the wrist we cropped around, in normalised crop coordinates
                w = kp2d[t, ARMS[k][0], :2]
                wx, wy = (w[0] - (cx - half)) / (2 * half), (w[1] - (cy - half)) / (2 * half)
                dist = [np.hypot(h[0].x - wx, h[0].y - wy) for h in res.hand_landmarks]
                i = int(np.argmin(dist))
                if dist[i] > 0.35:              # another hand in the crop, or a false palm
                    continue
                for j, p in enumerate(res.hand_landmarks[i]):
                    kp[t, k, j] = (cx - half + p.x * 2 * half, cy - half + p.y * 2 * half, p.z * 2 * half)
                for j, p in enumerate(res.hand_world_landmarks[i]):
                    world[t, k, j] = (p.x, p.y, p.z)
                score[t, k] = res.handedness[i][0].score if res.handedness else 0.5
    cap.release()
    return kp, world, score, boxes, float(fps)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--video", required=True)
    ap.add_argument("--kp", help="npz with the plate's ViTPose kp2d (default: smplx_raw.npz, else smplx.npz, next to the video)")
    ap.add_argument("--out", help="default: hands2d.npz next to the video")
    ap.add_argument("--model")
    args = ap.parse_args()
    video = DF.rpath(args.video)
    if args.kp:
        kp_path = DF.rpath(args.kp)
    else:
        kp_path = next((p for p in (video.with_name("smplx_raw.npz"), video.with_name("smplx.npz")) if p.exists()), None)
        if kp_path is None:
            raise SystemExit("no smplx_raw.npz / smplx.npz next to the video: run estimate_pose_gvhmr.py first, or pass --kp")
    kp2d = np.load(kp_path)["kp2d"]
    out = DF.rpath(args.out) if args.out else video.with_name("hands2d.npz")
    kp, world, score, boxes, fps = detect(video, kp2d, model_path(args.model))
    np.savez_compressed(out, kp=kp, world=world, score=score, crop=boxes, fps=np.array(fps))
    found = (score > 0).mean(0) * 100
    print(f"wrote {out}: {kp.shape[0]} frames, left hand found on {found[0]:.0f}%, right on {found[1]:.0f}%")


if __name__ == "__main__":
    main()
